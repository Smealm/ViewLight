using System.IO.Compression;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What came of reading a backup archive.</summary>
/// <param name="Manifest">The manifest the archive carries.</param>
/// <param name="Databases">Which databases the archive actually holds files for.</param>
public sealed record BackupArchiveContents(BackupManifest Manifest, IReadOnlySet<string> Databases);

/// <summary>
/// The backup itself: one zip holding the database directories and the manifest, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file, because the cloud returns one file.</b> A backup is a set of database directories plus a
/// manifest; carrying it as a single archive is what lets the whole thing move over one upload and one
/// download, and lets a half-finished transfer be thrown away whole rather than leaving a database directory
/// with some of its files replaced. The manifest is inside the archive as well as beside it in the cloud, so
/// a restored archive can say what it is without anything else having survived.
/// </para>
/// <para>
/// <b>A restore never writes into a live database.</b> RavenDB holds its data directory under an exclusive
/// lock, so a restore is only ever applied while nothing is running - which is the same window the launcher
/// already manages for its own database. Each database is unpacked into a sibling
/// <c>&lt;name&gt;.restoring</c> directory first, and only moved into place once it has been read out
/// completely: an archive that turns out to be truncated leaves the directory that is there untouched rather
/// than half-replaced.
/// </para>
/// </remarks>
public static class BackupArchive {

    /// <summary>The suffix a database directory is unpacked into before it replaces the real one.</summary>
    public const string StagingSuffix = ".restoring";

    /// <summary>What the manifest is called inside the archive.</summary>
    public const string ManifestEntryName = BackupManifest.FileName;

    /// <summary>
    /// Writes the archive: the manifest, then every database's files under its own folder.
    /// </summary>
    /// <param name="archivePath">Where to write it. Overwritten if it is there.</param>
    /// <param name="manifest">The manifest to write, and to put in the archive.</param>
    /// <param name="directories">The database directories, keyed by <see cref="BackupDatabases"/>. A missing one is skipped.</param>
    /// <returns><c>null</c> when it was written, or why it was not.</returns>
    public static async Task<string?> CreateAsync(
        string archivePath, BackupManifest manifest,
        IReadOnlyDictionary<string, string> directories, CancellationToken token = default) {

        try {
            var directory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            // Written to a temporary name and moved into place, so a transfer that reads the archive path
            // never sees a half-written one - the same habit the patch server's copy has.
            var staging = archivePath + ".writing";

            await using (var file = File.Create(staging)) {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using (var stream = manifestEntry.Open()) {
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(manifest.ToJson().AsMemory(), token).ConfigureAwait(false);
                }

                foreach (var (database, source) in directories) {
                    if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) {
                        continue;
                    }

                    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                        token.ThrowIfCancellationRequested();

                        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
                        var entry = archive.CreateEntry($"{database}/{relative}", CompressionLevel.Optimal);

                        await using var entryStream = entry.Open();
                        await using var source_stream = File.OpenRead(path);
                        await source_stream.CopyToAsync(entryStream, token).ConfigureAwait(false);
                    }
                }
            }

            File.Move(staging, archivePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or InvalidDataException) {
            return $"{Path.GetFileName(archivePath)} could not be written ({ex.Message}).";
        }
    }

    /// <summary>
    /// Reads what an archive holds, without unpacking it: the manifest and which databases it has files for.
    /// </summary>
    /// <returns>The contents, or <c>null</c> when the file is not a backup this build understands.</returns>
    public static BackupArchiveContents? Read(string archivePath) {
        try {
            if (!File.Exists(archivePath)) {
                return null;
            }

            using var archive = ZipFile.OpenRead(archivePath);

            var manifestEntry = archive.GetEntry(ManifestEntryName);
            if (manifestEntry is null) {
                return null;
            }

            using var stream = manifestEntry.Open();
            using var reader = new StreamReader(stream);
            var manifest = BackupManifest.TryParse(reader.ReadToEnd());

            if (manifest is null) {
                return null;
            }

            var databases = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in archive.Entries) {
                var slash = entry.FullName.IndexOf('/');
                if (slash > 0) {
                    databases.Add(entry.FullName[..slash]);
                }
            }

            return new BackupArchiveContents(manifest, databases);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or NotSupportedException) {
            return null;
        }
    }

    /// <summary>
    /// Unpacks an archive over the database directories it holds, replacing each one.
    /// </summary>
    /// <param name="archivePath">The archive to unpack.</param>
    /// <param name="directories">Where each database lives, keyed by <see cref="BackupDatabases"/>.</param>
    /// <returns><c>null</c> when it was restored, or why it was not.</returns>
    /// <remarks>
    /// Only databases the archive actually has files for are touched. A manifest that lists the server's
    /// database as absent - a session that has never been played - therefore restores the launcher's notes
    /// without inventing an empty server database beside them.
    /// </remarks>
    public static async Task<string?> RestoreAsync(
        string archivePath, IReadOnlyDictionary<string, string> directories, CancellationToken token = default) {

        try {
            if (!File.Exists(archivePath)) {
                return $"{Path.GetFileName(archivePath)} is not there.";
            }

            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var (database, target) in directories) {
                token.ThrowIfCancellationRequested();

                var hasFiles = archive.Entries.Any(
                    entry => entry.FullName.StartsWith($"{database}/", StringComparison.Ordinal)
                             && entry.FullName.Length > database.Length + 1);

                if (!hasFiles) {
                    continue;
                }

                var staging = target + StagingSuffix;

                if (Directory.Exists(staging)) {
                    Directory.Delete(staging, recursive: true);
                }

                Directory.CreateDirectory(staging);

                foreach (var entry in archive.Entries) {
                    var prefix = $"{database}/";
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.Length <= prefix.Length) {
                        continue;
                    }

                    var relative = entry.FullName[prefix.Length..];
                    var destination = Path.GetFullPath(Path.Combine(staging, relative));

                    // Nothing is allowed to escape the staging directory, whatever the archive's own names say.
                    if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)) {
                        return $"{Path.GetFileName(archivePath)} holds a path that leaves the database directory: {relative}.";
                    }

                    var parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) {
                        Directory.CreateDirectory(parent);
                    }

                    entry.ExtractToFile(destination, overwrite: true);
                }

                // Only now, with the whole database read out, is the live one moved aside. A failure above
                // leaves what was there exactly as it was.
                if (Directory.Exists(target)) {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(staging, target);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or NotSupportedException) {
            return $"The backup could not be restored ({ex.Message}).";
        }
    }
}
