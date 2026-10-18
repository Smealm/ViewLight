using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ViewLight.Services;

/// <summary>Progress of an install/update, with <see cref="Fraction"/> in the 0..1 range.</summary>
public sealed record InstallProgress(double Fraction, string Stage, string Detail) {
    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);
}

public sealed record InstallResult(
    bool Success,
    string Tag,
    int FilesWritten,
    int SettingsRetained,
    string Message,
    string? Warning = null,
    int? EmbeddedDatabasePort = null,
    string? PatchServerUrl = null,
    int FilesUnchanged = 0);

/// <summary>
/// Installs an Imlight build over an existing install and puts the user's settings back afterwards -
/// unpacking a release archive first, or taking a folder a source build produced.
/// </summary>
/// <remarks>
/// The download and the build differ only in where the build folders come from - one unpacked folder, or
/// the three folders a source build leaves its output in; everything after that is
/// <see cref="InstallFromFoldersAsync"/>, deliberately, so an install from source keeps the same settings,
/// config backup and repairs as a published one.
/// <para>
/// The update strategy is deliberately "overlay, never wipe". Releases are flat self-contained
/// folders (verified against <c>Imlight-win-x64.zip</c> for <c>r806919.Wizard_1_610</c>: the archive
/// root <i>is</i> the install root), so extracting in place replaces binaries and leaves everything
/// the server generates at runtime - <c>logs/</c>, <c>cache/</c>, <c>spiraldb-cache/</c> and the
/// embedded RavenDB directory - untouched, because we simply never delete anything.
/// </para>
/// <para>
/// The one file that must not be blindly overwritten is <c>Config/Imlight.ini</c>, since that is where
/// every user setting lives. It is read before the overlay and merged back afterwards, so a new build
/// contributes new keys at their defaults while the user's existing values win.
/// </para>
/// </remarks>
public sealed class ServerInstallService {

    private readonly HttpClient _http;

    public ServerInstallService(HttpClient? http = null) => _http = http ?? Http.Client;

    /// <summary>Where a release archive is cached between attempts.</summary>
    public string GetCachedArchivePath(Models.ImlightRelease release) {
        var tag = string.Concat(release.Tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(LauncherPaths.DownloadsDirectory, $"{tag}-{release.AssetName}");
    }

    /// <summary>True when a complete copy of this release is already on disk.</summary>
    public bool HasCachedArchive(Models.ImlightRelease release) {
        var path = GetCachedArchivePath(release);
        if (!File.Exists(path)) {
            return false;
        }

        // If GitHub told us the size, an exact match means we can skip the download entirely.
        return release.AssetSize <= 0 || new FileInfo(path).Length == release.AssetSize;
    }

    /// <summary>
    /// Installs or updates a server, asking the caller to stand anything down that is using the folder first.
    /// </summary>
    /// <param name="release">The release to install.</param>
    /// <param name="serverPath">The install to write over.</param>
    /// <param name="progress">Where to report progress.</param>
    /// <param name="beforeWriting">
    /// Called once, immediately before the first file is written over the install - and not at all if the
    /// release never gets that far, so a download that fails costs nothing. This is where the launcher stops
    /// the databases it runs out of the server's own <c>RavenDBServer</c> folder, because Windows will not let
    /// a loaded DLL be replaced and an update is exactly that.
    /// </param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public async Task<InstallResult> InstallAsync(
        Models.ImlightRelease release,
        string serverPath,
        IProgress<InstallProgress>? progress = null,
        Func<Task>? beforeWriting = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(serverPath)) {
            return new InstallResult(false, release.Tag, 0, 0, "No server folder is configured.");
        }

        Directory.CreateDirectory(serverPath);
        Directory.CreateDirectory(LauncherPaths.DownloadsDirectory);

        var archivePath = GetCachedArchivePath(release);
        var stagingRoot = Path.Combine(LauncherPaths.StagingDirectory, release.Tag);

        try {
            // ---------------------------------------------------------- download (0 - 70%)
            if (HasCachedArchive(release)) {
                progress?.Report(new InstallProgress(0.70, "Download", "Using cached archive."));
            }
            else {
                await DownloadAsync(release, archivePath, progress, cancellationToken).ConfigureAwait(false);
            }

            // ---------------------------------------------------------- extract (70 - 88%)
            var contentRoot = await Task.Run(
                () => ExtractAsync(archivePath, stagingRoot, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            return await InstallFromFolderAsync(
                    contentRoot, release.Tag, serverPath, progress, beforeWriting, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return new InstallResult(false, release.Tag, 0, 0, "Install cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A file that will not be written over is the one install failure a user can act on, and Windows'
            // own sentence about it names the file rather than the thing holding it. Said properly instead.
            return new InstallResult(false, release.Tag, 0, 0, LockedFileMessage(ex));
        }
        catch (Exception ex) {
            return new InstallResult(false, release.Tag, 0, 0, $"Install failed: {ex.Message}");
        }
        finally {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// Installs the build in a folder over a server folder, keeping its settings and runtime data.
    /// </summary>
    /// <param name="contentRoot">The build itself - the folder a release archive would have unpacked to.</param>
    /// <param name="tag">What to call the build in messages and in the install record.</param>
    public Task<InstallResult> InstallFromFolderAsync(
        string contentRoot,
        string tag,
        string serverPath,
        IProgress<InstallProgress>? progress = null,
        Func<Task>? beforeWriting = null,
        CancellationToken cancellationToken = default)
        => InstallFromFoldersAsync([contentRoot], tag, serverPath, progress, beforeWriting, cancellationToken);

    /// <summary>
    /// Installs a build that is spread over several folders - which is what a source build produces - over
    /// a server folder, keeping its settings and runtime data.
    /// </summary>
    /// <param name="contentRoots">
    /// The projects' own output folders, in the order they are deployed: Director's runnable tree first,
    /// then the library folders that belong beside it. A file present in a later folder replaces the same
    /// relative path from an earlier one.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the half an archive install and a build from source share. The archive path downloads and
    /// unpacks first; a source build points at the folders <c>dotnet build</c> left behind. Everything from
    /// here on is the same, which is the point: a build from source must keep the same settings, write the
    /// same config backup and get the same port and patch-server repairs as a published one.
    /// </para>
    /// <para>
    /// <b>Only files that differ are written.</b> A build puts its output where it always does, so a deploy
    /// of the same commit a second time writes none of its binaries: each file is compared with the one in
    /// the server folder by length and then by content. (Never by timestamp - two different builds can
    /// share a length and a timestamp, and treating that pair as unchanged is how an old server survives an
    /// install that says it succeeded.) That is what makes an update cost the size of what changed rather
    /// than the size of a server folder, and it is what makes a failed deploy safe to press again.
    /// </para>
    /// </remarks>
    public async Task<InstallResult> InstallFromFoldersAsync(
        IReadOnlyList<string> contentRoots,
        string tag,
        string serverPath,
        IProgress<InstallProgress>? progress = null,
        Func<Task>? beforeWriting = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(serverPath)) {
            return new InstallResult(false, tag, 0, 0, "No server folder is configured.");
        }

        var roots = contentRoots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .ToList();

        // A folder that is not a build must not be overlaid: copying it would leave the server folder a
        // mixture of two unrelated trees, and the release would look installed because the files were
        // written. Checked before the config is read, the target is created or anything is touched, so a
        // wrong folder costs nothing. This is also what catches a build that "succeeded" without
        // producing an executable.
        var executableRoot = roots.FirstOrDefault(
            root => File.Exists(Path.Combine(root, ImlightConfig.ServerExecutableName)));

        if (executableRoot is null) {
            var looked = roots.Count == 0 ? "no build folders were given" : string.Join(", ", roots);

            return new InstallResult(false, tag, 0, 0,
                $"{ImlightConfig.ServerExecutableName} was not found in any build folder ({looked}), so "
                + "nothing was installed.");
        }

        Directory.CreateDirectory(serverPath);

        var warning = default(string);

        try {
            // ---------------------------------------------------------- merge settings
            // Read the user's config *before* the overlay replaces it.
            var existingConfigPath = ImlightConfig.ConfigPathFor(serverPath);
            IniFile? userConfig = null;
            if (File.Exists(existingConfigPath)) {
                try {
                    userConfig = IniFile.Load(existingConfigPath);
                    File.Copy(existingConfigPath, existingConfigPath + ".bak", overwrite: true);
                }
                catch (Exception ex) {
                    warning = $"Existing Imlight.ini could not be read, so settings were not retained: {ex.Message}";
                }
            }

            // ---------------------------------------------------------- deploy (88 - 96%)
            // The last moment this can be done: every source folder has been read, so what is about to
            // happen is the write and nothing else, and a failure before here has disturbed nothing.
            if (beforeWriting is not null) {
                progress?.Report(new InstallProgress(0.88, "Install", "Making way for the new files…"));
                await beforeWriting().ConfigureAwait(false);
            }

            var (filesWritten, filesUnchanged) = await Task.Run(
                () => OverlayCopy(roots, serverPath, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            // ---------------------------------------------------------- restore (96 - 100%)
            var settingsRetained = 0;
            if (userConfig is not null && File.Exists(existingConfigPath)) {
                progress?.Report(new InstallProgress(0.96, "Settings", "Retaining your Imlight.ini settings…"));

                var merged = IniFile.Load(existingConfigPath);
                settingsRetained = merged.MergeFrom(userConfig);
                merged.Save(existingConfigPath);
            }

            // ---------------------------------------------------------- config repairs
            // Kicks in on updates too, not just fresh installs: Imlight ships the embedded database on
            // port 8080 and its patch server on localhost, and it cannot boot while either of those
            // points at something that is not there.
            var repair = ServerConfigRepair.Apply(existingConfigPath);

            // ---------------------------------------------------------- check the write
            // A copy that reports success but leaves the old assembly behind is the one failure that looks
            // exactly like a successful install: the card says the new build is in the folder, and the next
            // server start is still the old code. The executable alone is not enough evidence - dotnet's
            // apphost can be byte-identical between builds - so the managed assemblies are compared too.
            // Checked after the settings were put back, so a failed write still leaves the user's config
            // where the next attempt can retain it.
            var mismatch = DescribeCopyMismatch(roots, serverPath);
            if (mismatch is not null) {
                return new InstallResult(false, tag, filesWritten, settingsRetained, mismatch, warning,
                    repair.EmbeddedDatabasePort, repair.PatchServerUrl, filesUnchanged);
            }

            progress?.Report(new InstallProgress(1.0, "Done", "Install complete."));

            var message = userConfig is null
                ? $"Installed {tag}."
                : $"Installed {tag} and retained {settingsRetained} existing settings.";

            if (filesUnchanged > 0) {
                message += $" {filesUnchanged} file(s) were already current.";
            }

            return new InstallResult(
                true, tag, filesWritten, settingsRetained, message, warning,
                repair.EmbeddedDatabasePort, repair.PatchServerUrl, filesUnchanged);
        }
        catch (OperationCanceledException) {
            return new InstallResult(false, tag, 0, 0, "Install cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new InstallResult(false, tag, 0, 0, LockedFileMessage(ex));
        }
        catch (Exception ex) {
            return new InstallResult(false, tag, 0, 0, $"Install failed: {ex.Message}");
        }
    }

    private async Task DownloadAsync(
        Models.ImlightRelease release,
        string destination,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        progress?.Report(new InstallProgress(0, "Download", $"Connecting to GitHub… ({release.AssetSizeDisplay})"));

        var partial = destination + ".part";

        using var response = await _http
            .GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.AssetSize;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(partial)) {
            var buffer = new byte[128 * 1024];
            long received = 0;
            var stopwatch = Stopwatch.StartNew();

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                // Throttle so the UI is not flooded with progress callbacks.
                if (stopwatch.ElapsedMilliseconds >= 120) {
                    stopwatch.Restart();
                    progress?.Report(new InstallProgress(
                        total > 0 ? 0.70 * received / total : 0,
                        "Download",
                        $"Downloading {FormatBytes(received)} of {(total > 0 ? FormatBytes(total) : "?")}"));
                }
            }
        }

        // Only promote the partial file once it is complete, so a cancelled download is never reused.
        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>
    /// Extracts the archive into staging and returns the folder that actually holds the build, which
    /// is the staging root itself for current releases but may be a single subfolder for older ones.
    /// </summary>
    private static string ExtractAsync(
        string archivePath,
        string stagingRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        TryDeleteDirectory(stagingRoot);
        Directory.CreateDirectory(stagingRoot);

        progress?.Report(new InstallProgress(0.70, "Extract", "Unpacking server files…"));

        using var archive = ZipFile.OpenRead(archivePath);
        var stagingFull = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
        var total = archive.Entries.Count;
        var index = 0;

        foreach (var entry in archive.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var destination = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));

            // Defend against a crafted archive escaping the staging folder.
            if (!destination.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' points outside the extraction folder.");
            }

            if (entry.Name.Length == 0) {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            if (index % 50 == 0 || index == total) {
                progress?.Report(new InstallProgress(
                    0.70 + (0.18 * index / Math.Max(total, 1)),
                    "Extract",
                    $"Unpacked {index} of {total} files"));
            }
        }

        return ResolveContentRoot(stagingRoot);
    }

    private static string ResolveContentRoot(string stagingRoot) {
        var directories = Directory.GetDirectories(stagingRoot);
        var files = Directory.GetFiles(stagingRoot);

        // Older releases wrapped everything in a single top-level folder; unwrap it.
        if (files.Length == 0 && directories.Length == 1) {
            return directories[0];
        }

        return stagingRoot;
    }

    private static (int Written, int Unchanged) OverlayCopy(
        IReadOnlyList<string> sourceRoots,
        string targetRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        progress?.Report(new InstallProgress(0.88, "Install", "Writing server files…"));
        return CopyDirectories(sourceRoots, targetRoot, progress, cancellationToken);
    }

    /// <summary>
    /// Writes the files of every source folder into the target under the same relative path, skipping the
    /// ones the target already holds.
    /// </summary>
    /// <remarks>
    /// A source build's output lives in three folders - Director's runnable tree and the two library
    /// folders beside it - and together they are the server. Each is overlaid in turn, so a later folder
    /// wins on a shared relative path, and the result is one folder that runs.
    /// </remarks>
    private static (int Written, int Unchanged) CopyDirectories(
        IReadOnlyList<string> sources,
        string target,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        Directory.CreateDirectory(target);
        var written = 0;
        var unchanged = 0;
        var visited = 0;

        // One entry per relative path, taken from the last folder that holds it - which is exactly the
        // file the deploy order would leave in place. The same assembly sits in Director's runnable tree
        // and again in the library's own folder beside it; without this it would be compared and skipped
        // twice, and the whole tree would be read twice for no gain.
        var build = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources) {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
                cancellationToken.ThrowIfCancellationRequested();
                build[Path.GetRelativePath(source, file)] = file;
            }
        }

        foreach (var (relative, file) in build) {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Pressing deploy twice, or rebuilding without a source change, writes the same bytes to
            // the same places. Nothing is copied in that case, so the server folder is not made to
            // look different for no reason and the write is not risked on files nothing changed in.
            if (IsSameFile(file, destination)) {
                unchanged++;
            }
            else {
                CopyOver(file, destination);
                written++;
            }

            // Counted by files looked at rather than files written, so a deploy that has nothing to
            // write still shows movement rather than sitting at 88% while it reads the tree.
            visited++;

            if (visited % 50 == 0) {
                progress?.Report(new InstallProgress(
                    0.88 + (0.08 * Math.Min(visited, 600) / 600.0),
                    "Install",
                    $"Checked {visited} files, wrote {written}"));
            }
        }

        return (written, unchanged);
    }

    /// <summary>
    /// Whether the target already holds this exact file, so it can be left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Length is compared first because it is free and settles most of a server tree. Everything else is
    /// compared by content, and the timestamp is deliberately not consulted: two files of the same length
    /// written at the same moment are not necessarily the same file. An archive install that produced
    /// <c>exe:v1</c> and one that produced <c>exe:v2</c> have the same size and the same extracted
    /// timestamp, and treating that pair as unchanged leaves the old server in place while the install
    /// reports success - which is the exact failure this deploy exists to not have.
    /// </para>
    /// <para>
    /// The cost is a hash of the build tree, on a worker thread, once per deploy: a self-contained server
    /// is a few hundred megabytes and is read at disk speed. That is cheaper than being wrong.
    /// </para>
    /// </remarks>
    private static bool IsSameFile(string source, string destination) {
        if (!File.Exists(destination)) {
            return false;
        }

        if (new FileInfo(source).Length != new FileInfo(destination).Length) {
            return false;
        }

        try {
            return Hash(source) == Hash(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A file another process is holding cannot be read to be compared. Answer "not the same" so
            // the copy takes it on: the copy waits a hold out, and reports one that is still there.
            return false;
        }
    }

    /// <summary>
    /// The managed assemblies that decide what code the server folder runs.
    /// </summary>
    private static readonly string[] s_checkedAssemblies = [
        "Imlight.Director.dll",
        "Imlight.CoreLib.dll",
        "Imlight.Common.dll",
    ];

    /// <summary>
    /// Why the copied build does not match the sources, or <c>null</c> when it does.
    /// </summary>
    /// <remarks>
    /// This is the check that the previous workflow was missing: a deploy that reports success and leaves
    /// the old server folder untouched looks exactly like a successful one. The executable alone is not
    /// enough evidence - dotnet's apphost can be byte-identical between builds - so every assembly the
    /// build produced is compared by content.
    /// </remarks>
    private static string? DescribeCopyMismatch(IReadOnlyList<string> contentRoots, string serverPath) {
        var executableRoot = contentRoots.FirstOrDefault(
            root => File.Exists(Path.Combine(root, ImlightConfig.ServerExecutableName)));

        // Already reported by the caller before anything was written.
        if (executableRoot is null) {
            return null;
        }

        var stagedExecutable = Path.Combine(executableRoot, ImlightConfig.ServerExecutableName);
        var installedExecutable = Path.Combine(serverPath, ImlightConfig.ServerExecutableName);

        if (!File.Exists(installedExecutable)) {
            return $"{ImlightConfig.ServerExecutableName} is missing from {serverPath} after the copy.";
        }

        if (new FileInfo(stagedExecutable).Length != new FileInfo(installedExecutable).Length) {
            return $"{ImlightConfig.ServerExecutableName} in {serverPath} is not the one that was just "
                   + "built, so the server folder still holds the old executable.";
        }

        foreach (var assembly in s_checkedAssemblies) {
            var staged = contentRoots
                .Select(root => Path.Combine(root, assembly))
                .FirstOrDefault(File.Exists);

            // Nothing is claimed about an assembly the build did not contain.
            if (staged is null) {
                continue;
            }

            var installed = Path.Combine(serverPath, assembly);

            if (!File.Exists(installed)) {
                return $"{assembly} is missing from {serverPath} after the copy.";
            }

            if (Hash(staged) != Hash(installed)) {
                return $"{assembly} in {serverPath} is not the one that was just built, so the server "
                       + "folder still holds the old server code.";
            }
        }

        return null;
    }

    private static string Hash(string path) {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Writes one file over another, waiting out a file that is busy for a moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window this exists for is a real one, and it was measured rather than assumed: with a database running
    /// out of the folder, writing over one of its DLLs fails with "the process cannot access the file ... because
    /// it is being used by another process"; <c>EmbeddedRaven.StopAsync</c> then takes about 180 ms, and the file is
    /// not writable for about 110 ms more after that. The wait below is roughly ten times that, so the ordinary
    /// case never notices it.
    /// </para>
    /// <para>
    /// A hold that outlives the wait is a different thing - the server itself is running, or another launcher has
    /// the folder - and is reported as one by <see cref="LockedFileMessage"/> rather than retried for ever.
    /// </para>
    /// </remarks>
    private static void CopyOver(string source, string destination) {
        const int attempts = 8;

        for (var attempt = 1; ; attempt++) {
            try {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException) {
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>
    /// A file that would not be replaced, said in a way that names what to do about it.
    /// </summary>
    /// <remarks>
    /// The launcher stops its own databases before it writes, so what this is describing is something else: the
    /// server itself running, and holding the very binaries the update came to replace. Naming that is the whole
    /// point - "the process cannot access the file" sends a user looking at the file, and the answer is a button
    /// on the same screen.
    /// </remarks>
    internal static string LockedFileMessage(Exception ex)
        => "A file in the server folder is in use by another process, so the update could not replace it "
           + $"({ex.Message}). If Imlight is running, stop it and press the install button again.";

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) {
            // Staging leftovers are harmless; the next install clears them again.
        }
        catch (UnauthorizedAccessException) {
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };
}
