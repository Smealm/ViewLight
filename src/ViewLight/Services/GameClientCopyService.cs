using System.Diagnostics;

namespace ViewLight.Services;

/// <summary>Progress of copying the game into the launcher's folder, with <see cref="Fraction"/> 0..1.</summary>
public sealed record ClientCopyProgress(double Fraction, string Stage, string Detail) {
    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);
}

public sealed record ClientCopyResult(
    bool Success,
    int FilesCopied,
    long BytesCopied,
    string Destination,
    string Message,
    string? Warning = null);

/// <summary>
/// Copies a Wizard101 install into the launcher's own folder, which is what makes portable mode
/// self-contained rather than merely self-locating.
/// </summary>
/// <remarks>
/// <para>
/// This is a real copy - several gigabytes read and written - which is why it reports progress and takes
/// a cancellation token, and why it checks the destination's free space first. The source install is left
/// exactly as it was, so a failure leaves the user with a working game.
/// </para>
/// <para>
/// <b>A Steam copy may need Steam, and that is KingsIsle's to decide.</b> The build being served today
/// starts without the Steam client - it has been played that way - and it carries the game's own cached
/// copy of the patch server's files, which makes it a good thing to copy rather than a bad one. What it
/// also carries is <c>bin\steam_api.dll</c> and <c>bin\steam_appid.txt</c>, and what that library does is
/// KingsIsle's to change without telling anybody: a build that will not start until Steam is installed and
/// signed in is one they can publish at any time, and a copy of it in this folder would stop working the
/// day they do. So it is a possible dependency rather than a present one, and it is reported rather than
/// quietly worked around - a standalone install, which ships neither file, is the copy that cannot be
/// affected either way.
/// </para>
/// </remarks>
public sealed class GameClientCopyService {

    /// <summary>
    /// What a Steam source means, said once so the UI and the copy report agree.
    /// </summary>
    /// <remarks>
    /// Deliberately not "this will not work without Steam": it does work without it today, and telling
    /// somebody a game they have been playing is broken is worse than saying what could change. The file
    /// that could change it is named, so the warning can be checked against the folder it is about.
    /// </remarks>
    public const string SteamCaveat =
        "This is a Steam copy of Wizard101. It plays without the Steam client at the moment and it comes "
        + @"with the game's own cached patch files - but it carries bin\steam_api.dll, and KingsIsle decides "
        + "what that library does: a later build may require Steam to be installed and signed in, and this "
        + "copy would stop working the day it does. A standalone install from KingsIsle's website ships "
        + @"neither that library nor bin\steam_appid.txt, so it is the copy that stands on its own for "
        + "certain.";

    /// <summary>
    /// Copies <paramref name="sourceFolder"/> into <paramref name="destinationFolder"/>.
    /// </summary>
    /// <remarks>
    /// Never throws: every failure comes back as an unsuccessful <see cref="ClientCopyResult"/>, because
    /// the caller is a progress bar and a sentence, not an exception handler.
    /// </remarks>
    public async Task<ClientCopyResult> CopyAsync(
        string? sourceFolder,
        string? destinationFolder,
        IProgress<ClientCopyProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder)) {
            return new ClientCopyResult(false, 0, 0, destinationFolder ?? string.Empty,
                "Choose a Wizard101 game folder to copy.");
        }

        if (string.IsNullOrWhiteSpace(destinationFolder)) {
            return new ClientCopyResult(false, 0, 0, string.Empty,
                "There is nowhere to copy the game to.");
        }

        if (!GameFolderLocator.LooksLikeGameFolder(sourceFolder)) {
            return new ClientCopyResult(false, 0, 0, destinationFolder,
                $"That folder does not look like a Wizard101 install: it has neither "
                + $"{GameFolderLocator.RelativeClientExecutable} nor {GameFolderLocator.RelativeRootWad}.");
        }

        var source = Path.GetFullPath(sourceFolder);
        var destination = Path.GetFullPath(destinationFolder);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) {
            return GameFolderLocator.LooksLikeGameFolder(destination)
                ? new ClientCopyResult(true, 0, 0, destination, "The game is already in the launcher's folder.")
                : new ClientCopyResult(false, 0, 0, destination, "The game cannot be copied into itself.");
        }

        // Copying into itself would grow without end, and copying a folder into its own parent would
        // write over the very files being read.
        if (GameFolderLocator.IsInsideOrSame(destination, source)
            || GameFolderLocator.IsInsideOrSame(source, destination)) {
            return new ClientCopyResult(false, 0, 0, destination,
                "One of those folders is inside the other, so there is nothing sensible to copy.");
        }

        // A Steam source keeps needing Steam; said before the copy as well as after it, because it is
        // the difference between a portable folder that works anywhere and one that does not.
        var warning = GameFolderLocator.IsSteamInstall(source) ? SteamCaveat : null;

        var copiedFiles = 0;
        long copiedBytes = 0;

        try {
            progress?.Report(new ClientCopyProgress(0, "Look", "Reading the file list…"));

            var plan = await Task.Run(() => EnumerateFiles(source), cancellationToken).ConfigureAwait(false);
            if (plan.Count == 0) {
                return new ClientCopyResult(false, 0, 0, destination,
                    "There are no files in that game folder.", warning);
            }

            var totalBytes = plan.Sum(entry => entry.Length);

            if (!HasRoomFor(destination, totalBytes, out var free)) {
                return new ClientCopyResult(false, 0, 0, destination,
                    $"There is not enough free space to copy the game: it needs "
                    + $"{ServerInstallService.FormatBytes(totalBytes)} and "
                    + $"{ServerInstallService.FormatBytes(free)} is free.", warning);
            }

            progress?.Report(new ClientCopyProgress(0, "Copy",
                $"Copying {plan.Count} files ({ServerInstallService.FormatBytes(totalBytes)})…"));

            await Task.Run(() => {
                var stopwatch = Stopwatch.StartNew();

                foreach (var (file, length) in plan) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);

                    copiedFiles++;
                    copiedBytes += length;

                    // Throttled: a progress callback per file would flood the UI on a big install.
                    if (stopwatch.ElapsedMilliseconds < 150) {
                        continue;
                    }

                    stopwatch.Restart();
                    progress?.Report(new ClientCopyProgress(
                        totalBytes > 0 ? (double)copiedBytes / totalBytes : 0,
                        "Copy",
                        $"{copiedFiles} of {plan.Count} files — "
                        + $"{ServerInstallService.FormatBytes(copiedBytes)} of {ServerInstallService.FormatBytes(totalBytes)}"));
                }
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ClientCopyProgress(1, "Done", "Copy complete."));

            if (!GameFolderLocator.LooksLikeGameFolder(destination)) {
                warning = Join(warning, "The copy finished, but the game in the new folder does not look complete.");
            }

            return new ClientCopyResult(true, copiedFiles, copiedBytes, destination,
                $"Copied {copiedFiles} files ({ServerInstallService.FormatBytes(copiedBytes)}) into {destination}.",
                warning);
        }
        catch (OperationCanceledException) {
            return new ClientCopyResult(false, copiedFiles, copiedBytes, destination, "Copy cancelled.", warning);
        }
        catch (Exception ex) {
            return new ClientCopyResult(false, copiedFiles, copiedBytes, destination,
                $"Copying the game failed: {ex.Message}", warning);
        }
    }

    /// <summary>The files to copy, with their sizes, read once so the copy can report real progress.</summary>
    private static List<(string File, long Length)> EnumerateFiles(string source) {
        var files = new List<(string, long)>();

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            long length;
            try {
                length = new FileInfo(file).Length;
            }
            catch (Exception) {
                // A file that cannot be measured is still copied; it just contributes nothing to the bar.
                length = 0;
            }

            files.Add((file, length));
        }

        return files;
    }

    /// <summary>
    /// Whether the destination's drive has room, so a copy fails before it fills a disk.
    /// </summary>
    /// <remarks>
    /// Shared with the update, which writes the same kind of thing into the same kind of folder and has the
    /// same reason to look before it leaps - a check written twice is a check that will be fixed once.
    /// </remarks>
    public static bool HasRoomFor(string destination, long needed, out long free) {
        free = 0;

        try {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) {
                return true;
            }

            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) {
            // A drive that cannot be asked is not a reason to refuse; the copy will say so if it fails.
            return true;
        }

        // A little headroom, so a full disk is not hit mid-write.
        return free > needed + (64L * 1024 * 1024);
    }

    private static string Join(string? existing, string more)
        => string.IsNullOrWhiteSpace(existing) ? more : $"{existing} {more}";
}
