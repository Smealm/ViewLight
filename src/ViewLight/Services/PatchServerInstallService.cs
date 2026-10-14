using System.Diagnostics;

namespace ViewLight.Services;

/// <summary>What an install of the patch server did.</summary>
public sealed record PatchServerInstallResult(bool Success, string Tag, string Message, string? Warning = null);

/// <summary>
/// Downloads <c>aurorium.exe</c> from the Revive101 release page and writes the config it runs with.
/// </summary>
/// <remarks>
/// One file rather than an archive, so there is no unpacking and nothing to overlay: a new build
/// replaces the executable and leaves the fetched assets, the database and the logs beside it alone.
/// The config is only written when it does not already name the endpoint the launcher wants, so a
/// config someone has grown by hand is not flattened by pressing update.
/// </remarks>
public sealed class PatchServerInstallService {

    private readonly HttpClient _http;

    public PatchServerInstallService(HttpClient? http = null) => _http = http ?? Http.Client;

    /// <summary>Where the downloaded executable is cached between attempts.</summary>
    public string GetCachedArchivePath(Models.PatchServerRelease release) {
        var tag = string.Concat(release.Tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(LauncherPaths.DownloadsDirectory, $"{tag}-{release.AssetName}");
    }

    /// <summary>True when a complete copy of this release is already on disk.</summary>
    public bool HasCachedArchive(Models.PatchServerRelease release) {
        var path = GetCachedArchivePath(release);
        if (!File.Exists(path)) {
            return false;
        }

        return release.AssetSize <= 0 || new FileInfo(path).Length == release.AssetSize;
    }

    public async Task<PatchServerInstallResult> InstallAsync(
        Models.PatchServerRelease release,
        string patchServerFolder,
        int port,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(patchServerFolder)) {
            return new PatchServerInstallResult(false, release.Tag, "No patch server folder is configured.");
        }

        Directory.CreateDirectory(patchServerFolder);
        Directory.CreateDirectory(LauncherPaths.DownloadsDirectory);

        var cached = GetCachedArchivePath(release);

        try {
            if (HasCachedArchive(release)) {
                progress?.Report(new InstallProgress(0.85, "Download", "Using the cached download."));
            }
            else {
                await DownloadAsync(release, cached, progress, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new InstallProgress(0.9, "Install", "Writing the patch server…"));

            var target = PatchServerRunner.ExecutablePathFor(patchServerFolder);
            File.Copy(cached, target, overwrite: true);

            // Written after the executable so a folder that refuses the copy is not left with a config
            // and nothing to read it.
            PatchServerConfig.EnsureConfig(patchServerFolder, port);

            progress?.Report(new InstallProgress(1.0, "Done", "Patch server installed."));

            return new PatchServerInstallResult(
                true, release.Tag, $"Installed the patch server ({release.Tag}).");
        }
        catch (OperationCanceledException) {
            return new PatchServerInstallResult(false, release.Tag, "Patch server download cancelled.");
        }
        catch (Exception ex) {
            // A running patch server holds its own executable open, which is the one failure worth
            // explaining rather than reporting as a bare IO error.
            var hint = !OperatingSystem.IsWindows()
                ? string.Empty
                : " If the patch server is running, stop it first: Windows will not replace a running executable.";

            return new PatchServerInstallResult(
                false, release.Tag, $"Could not install the patch server: {ex.Message}{hint}");
        }
    }

    private async Task DownloadAsync(
        Models.PatchServerRelease release,
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

                if (stopwatch.ElapsedMilliseconds >= 120) {
                    stopwatch.Restart();
                    progress?.Report(new InstallProgress(
                        total > 0 ? 0.85 * received / total : 0,
                        "Download",
                        $"Downloading {ServerInstallService.FormatBytes(received)} of "
                        + $"{(total > 0 ? ServerInstallService.FormatBytes(total) : "?")}"));
                }
            }
        }

        // Only promoted once complete, so a cancelled download is never taken for a good one.
        File.Move(partial, destination, overwrite: true);
    }
}
