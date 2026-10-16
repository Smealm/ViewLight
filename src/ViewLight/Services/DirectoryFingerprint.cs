using System.Security.Cryptography;
using System.Text;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One database directory as a single content hash, for the sync to compare.
/// </summary>
/// <param name="State">The hash, the size and the file count, ready to be put into a manifest.</param>
/// <param name="Problem">Why it could not be read, when it could not - a lock held by something else, most likely.</param>
public sealed record DirectoryFingerprintResult(BackupDatabaseState State, string? Problem = null);

/// <summary>
/// Turns a directory into one content hash, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content, not metadata.</b> The hash is taken over each file's archive-relative path and its bytes, in a
/// fixed order, so two machines with the same data agree and a file that has been touched but not changed does
/// not look like a change. Timestamps, sizes on their own and directory entries are all deliberately left out:
/// a RavenDB writes and rewrites its own bookkeeping constantly, and a hash that moved with the mtimes would
/// say "changed" every time the launcher started.
/// </para>
/// <para>
/// <b>Order is fixed by the path, not by the file system.</b> The files are walked and sorted with an ordinal
/// comparison on the normalized (forward-slash) relative path, because <c>EnumerateFiles</c> offers no order
/// and two runs that found the same files must write the same hash - otherwise "unchanged" could never be
/// true and every start would upload everything again.
/// </para>
/// <para>
/// <b>An absent directory is a real answer, not an error.</b> A server that has never run has no data
/// directory, and that is a state a backup has to be able to describe - so it is recorded as
/// <see cref="BackupDatabaseState.Absent"/> rather than as a failure, and the sync compares "absent" with
/// "absent" as equal.
/// </para>
/// </remarks>
public static class DirectoryFingerprint {

    /// <summary>
    /// Hashes a directory's contents, or says why it could not be read.
    /// </summary>
    /// <param name="directory">The directory to hash. A missing one is <see cref="BackupDatabaseState.Absent"/>.</param>
    public static DirectoryFingerprintResult Of(string? directory) {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) {
            return new DirectoryFingerprintResult(BackupDatabaseState.Absent);
        }

        try {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => (Full: file, Relative: Normalize(Path.GetRelativePath(directory, file))))
                .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
                .ToList();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long bytes = 0;

            foreach (var (full, relative) in files) {
                var info = new FileInfo(full);

                // The path is fed in first, with a separator that cannot appear in a path, so two files that
                // happen to hold the same bytes are still two different entries.
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);

                var length = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                hash.AppendData(Encoding.UTF8.GetBytes(length));
                hash.AppendData([0]);

                AppendFile(hash, full);
                bytes += info.Length;
            }

            return new DirectoryFingerprintResult(new BackupDatabaseState(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                bytes,
                files.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return new DirectoryFingerprintResult(BackupDatabaseState.Absent,
                $"{directory} could not be read ({ex.Message}).");
        }
    }

    /// <summary>Reads a file into the hash in chunks, so a multi-gigabyte database is never held in memory whole.</summary>
    private static void AppendFile(IncrementalHash hash, string path) {
        using var stream = File.OpenRead(path);
        var buffer = new byte[1024 * 256];

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
            hash.AppendData(buffer, 0, read);
        }
    }

    /// <summary>
    /// A path in one shape on every machine, so a backup taken on Windows compares against one taken
    /// elsewhere - the same habit <see cref="LauncherPaths.ToStoredPath"/> has for the folders.
    /// </summary>
    private static string Normalize(string relative)
        => relative.Replace('\\', '/');
}
