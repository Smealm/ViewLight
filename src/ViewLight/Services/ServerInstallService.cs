using System.Diagnostics;
using System.IO.Compression;

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
    string? PatchServerUrl = null);

/// <summary>
/// Downloads an Imlight release, unpacks it over an existing install, and puts the user's settings
/// back afterwards.
/// </summary>
/// <remarks>
/// The update strategy is deliberately "overlay, never wipe". Releases are flat self-contained
/// folders (verified against <c>Imlight-win-x64.zip</c> for <c>r806919.Wizard_1_610</c>: the archive
/// root <i>is</i> the install root), so extracting in place replaces binaries and leaves everything
/// the server generates at runtime - <c>logs/</c>, <c>cache/</c>, <c>spiraldb-cache/</c> and the
/// embedded RavenDB directory - untouched, because we simply never delete anything.
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

    public async Task<InstallResult> InstallAsync(
        Models.ImlightRelease release,
        string serverPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(serverPath)) {
            return new InstallResult(false, release.Tag, 0, 0, "No server folder is configured.");
        }

        Directory.CreateDirectory(serverPath);
        Directory.CreateDirectory(LauncherPaths.DownloadsDirectory);

        var archivePath = GetCachedArchivePath(release);
        var stagingRoot = Path.Combine(LauncherPaths.StagingDirectory, release.Tag);
        var warning = default(string);

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

            // ---------------------------------------------------------- overlay (88 - 96%)
            var filesWritten = await Task.Run(
                () => OverlayCopy(contentRoot, serverPath, progress, cancellationToken), cancellationToken)
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

            progress?.Report(new InstallProgress(1.0, "Done", "Install complete."));

            // A missing executable means the archive layout surprised us; say so instead of failing silently.
            if (!File.Exists(ImlightConfig.ExecutablePathFor(serverPath))) {
                warning = $"{ImlightConfig.ServerExecutableName} was not found in the extracted archive.";
            }

            var message = userConfig is null
                ? $"Installed {release.Tag}."
                : $"Installed {release.Tag} and retained {settingsRetained} existing settings.";

            return new InstallResult(
                true, release.Tag, filesWritten, settingsRetained, message, warning,
                repair.EmbeddedDatabasePort, repair.PatchServerUrl);
        }
        catch (OperationCanceledException) {
            return new InstallResult(false, release.Tag, 0, 0, "Install cancelled.");
        }
        catch (Exception ex) {
            return new InstallResult(false, release.Tag, 0, 0, $"Install failed: {ex.Message}");
        }
        finally {
            TryDeleteDirectory(stagingRoot);
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

    private static int OverlayCopy(
        string sourceRoot,
        string targetRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        progress?.Report(new InstallProgress(0.88, "Install", "Writing server files…"));
        return CopyDirectory(sourceRoot, targetRoot, progress, cancellationToken);
    }

    private static int CopyDirectory(
        string source,
        string target,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        Directory.CreateDirectory(target);
        var written = 0;

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            written++;

            if (written % 50 == 0) {
                progress?.Report(new InstallProgress(
                    0.88 + (0.08 * Math.Min(written, 500) / 500.0),
                    "Install",
                    $"Wrote {written} files"));
            }
        }

        return written;
    }

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
