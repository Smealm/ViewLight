using System.Diagnostics;
using System.Security.Cryptography;

namespace ViewLight.Services;

/// <summary>How the game in the launcher's own folder is brought up to the build installed elsewhere.</summary>
public enum ClientUpdateMethod {

    /// <summary>
    /// Every file written again: the copy is emptied and the newer build put in whole.
    /// </summary>
    /// <remarks>
    /// The simplest of the two and the one with least to get wrong - there is no comparison to be clever about,
    /// and the result is a copy of the source rather than a copy that has been patched into agreeing with it.
    /// What it costs is the writes: several gigabytes, however little of the game actually changed.
    /// </remarks>
    Replace,

    /// <summary>
    /// Only files that are not already the same are written, and files the newer build does not have are
    /// removed.
    /// </summary>
    /// <remarks>
    /// Reads both folders to work that out, which is the trade being made: a patch that changes a few hundred
    /// megabytes out of eighteen means reading all of both trees and writing only the part that differs. It is
    /// the method to pick on a drive that is slow to write, or when the copy is on a medium with a write budget.
    /// </remarks>
    Mirror,
}

/// <summary>Progress of an update, with <see cref="Fraction"/> 0..1.</summary>
public sealed record ClientUpdateProgress(double Fraction, string Stage, string Detail) {
    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);
}

/// <summary>What an update did, or why it did not.</summary>
public sealed record ClientUpdateResult(
    bool Success,
    int Written,
    int Removed,
    long BytesWritten,
    string Destination,
    string Message,
    string? Warning = null);

/// <summary>
/// Brings the game in the launcher's own folder up to the build installed on this machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other half of copying the game in.</b> Portable mode plays the game from the launcher's folder, and
/// that copy is made once - so a Steam update, or a reinstall from KingsIsle, leaves the launcher playing a
/// build the machine has already moved past. There are two ways to close that gap and they differ only in what
/// they spend: <see cref="ClientUpdateMethod.Replace"/> writes the whole game again,
/// <see cref="ClientUpdateMethod.Mirror"/> reads both copies and writes only what is different.
/// </para>
/// <para>
/// <b>Both end with the folder equal to the source.</b> The two methods are not "good" and "quick" - they are
/// the same result for a different price, which is why the choice is offered rather than made: the launcher
/// cannot know whether this machine would rather spend gigabytes of writing or minutes of reading.
/// </para>
/// <para>
/// <b>Never throws, and never destroys the copy before it can replace it.</b> Every failure comes back as an
/// unsuccessful <see cref="ClientUpdateResult"/>, the space is checked before anything is removed, and the
/// source install is only ever read - so a failure leaves either the old copy or a partly-new one, and the
/// source untouched to try again from.
/// </para>
/// </remarks>
public static class GameClientUpdateService {

    /// <summary>
    /// Updates <paramref name="destination"/> from <paramref name="source"/> by the chosen method.
    /// </summary>
    public static async Task<ClientUpdateResult> UpdateAsync(
        string? source,
        string? destination,
        ClientUpdateMethod method,
        IProgress<ClientUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) {
            return new ClientUpdateResult(false, 0, 0, 0, destination ?? string.Empty,
                "The game folder to update from is not there any more. Point the launcher at it again.");
        }

        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination)) {
            return new ClientUpdateResult(false, 0, 0, 0, destination ?? string.Empty,
                "There is no copy of the game in the launcher's folder to update.");
        }

        if (!GameFolderLocator.LooksLikeGameFolder(source)) {
            return new ClientUpdateResult(false, 0, 0, 0, destination,
                $"That folder does not look like a Wizard101 install: it has neither "
                + $"{GameFolderLocator.RelativeClientExecutable} nor {GameFolderLocator.RelativeRootWad}.");
        }

        var from = Path.GetFullPath(source);
        var to = Path.GetFullPath(destination);

        // The same two refusals the first copy makes, for the same reasons: a folder updated from itself has
        // nothing to do, and one updated from its own parent would read the files it is writing.
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) {
            return new ClientUpdateResult(true, 0, 0, 0, to,
                "That is the same folder, so there is nothing to update.");
        }

        if (GameFolderLocator.IsInsideOrSame(to, from) || GameFolderLocator.IsInsideOrSame(from, to)) {
            return new ClientUpdateResult(false, 0, 0, 0, to,
                "One of those folders is inside the other, so there is nothing sensible to update from.");
        }

        // A Steam source stays Steam's however the files are laid out, which is worth saying before an update
        // as well as before a copy.
        var warning = GameFolderLocator.IsSteamInstall(from) ? GameClientCopyService.SteamCaveat : null;

        try {
            progress?.Report(new ClientUpdateProgress(0, "Look",
                method == ClientUpdateMethod.Replace
                    ? "Reading the installed game…"
                    : "Comparing both copies…"));

            var plan = await Task.Run(() => Plan(from, to, method, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            if (plan.ToWrite.Count == 0 && plan.ToRemove.Count == 0) {
                progress?.Report(new ClientUpdateProgress(1, "Done", "Already the same build."));

                return new ClientUpdateResult(true, 0, 0, 0, to,
                    $"The game in the launcher's folder is already the build in {from}. Nothing was written.",
                    warning);
            }

            // Checked before anything is removed, so a copy that cannot fit is refused rather than halved.
            if (!GameClientCopyService.HasRoomFor(to, plan.BytesToWrite, out var free)) {
                return new ClientUpdateResult(false, 0, 0, 0, to,
                    $"There is not enough free space to update the game: it needs "
                    + $"{ServerInstallService.FormatBytes(plan.BytesToWrite)} and "
                    + $"{ServerInstallService.FormatBytes(free)} is free.", warning);
            }

            var written = 0;
            var removed = 0;
            long bytesWritten = 0;

            await Task.Run(() => {
                var steps = plan.ToRemove.Count + plan.ToWrite.Count;
                var done = 0;
                var stopwatch = Stopwatch.StartNew();

                void Report(string stage, string detail) {
                    if (steps == 0) {
                        return;
                    }

                    progress?.Report(new ClientUpdateProgress((double) done / steps, stage, detail));
                }

                // Removals first: what the newer build does not have is gone before the space it held is
                // needed, and a file that was replaced rather than kept is not written over while the old
                // copy of it is still the only one on disk.
                foreach (var file in plan.ToRemove) {
                    cancellationToken.ThrowIfCancellationRequested();

                    try {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        // A file it could not remove is reported at the end rather than stopping the update:
                        // the copy is still closer to the source than it was.
                        plan.Unremoved.Add(file);
                    }

                    done++;

                    if (stopwatch.ElapsedMilliseconds >= 150) {
                        stopwatch.Restart();
                        Report("Remove",
                            $"{done} of {steps} files — {removed} of {plan.ToRemove.Count} removed "
                            + $"({ServerInstallService.FormatBytes(plan.BytesToRemove)})");
                    }
                }

                foreach (var (file, relative, length) in plan.ToWrite) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var target = Path.Combine(to, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);

                    written++;
                    bytesWritten += length;
                    done++;

                    if (stopwatch.ElapsedMilliseconds < 150) {
                        continue;
                    }

                    stopwatch.Restart();
                    Report("Write",
                        $"{done} of {steps} files — {written} written "
                        + $"({ServerInstallService.FormatBytes(bytesWritten)} of "
                        + $"{ServerInstallService.FormatBytes(plan.BytesToWrite)})");
                }

                RemoveEmptyDirectories(to);
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ClientUpdateProgress(1, "Done", "Update complete."));

            var message = plan.ToWrite.Count == 0
                ? $"Removed {removed} file(s) the installed build does not have, and wrote nothing: the game "
                  + "in the launcher's folder was already up to date."
                : $"Updated {to}: {written} file(s) written ({ServerInstallService.FormatBytes(bytesWritten)})"
                  + (removed > 0 ? $", {removed} removed." : ".");

            if (plan.Unremoved.Count > 0) {
                warning = Join(warning,
                    $"{plan.Unremoved.Count} file(s) the installed build does not have could not be removed - "
                    + "close the game if it is running and try again.");
            }

            if (!GameFolderLocator.LooksLikeGameFolder(to)) {
                warning = Join(warning,
                    "The update finished, but the game in the launcher's folder does not look complete.");
            }

            return new ClientUpdateResult(true, written, removed, bytesWritten, to, message, warning);
        }
        catch (OperationCanceledException) {
            return new ClientUpdateResult(false, 0, 0, 0, to, "Update cancelled.", warning);
        }
        catch (Exception ex) {
            return new ClientUpdateResult(false, 0, 0, 0, to, $"Updating the game failed: {ex.Message}", warning);
        }
    }

    /// <summary>
    /// What the update would do, worked out before anything is changed.
    /// </summary>
    /// <remarks>
    /// Planning and applying are separate so the space can be checked and an update that has nothing to do can
    /// say so without having written a byte - and so the deciding can be read on its own, which is where the
    /// difference between the two methods actually lives.
    /// </remarks>
    private static ClientUpdatePlan Plan(
        string source, string destination, ClientUpdateMethod method, CancellationToken cancellationToken) {

        var plan = new ClientUpdatePlan();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var length = LengthOf(file);

            wanted.Add(relative);

            var existing = Path.Combine(destination, relative);

            // Everything is written again, or the comparison decides - which is the whole of the difference
            // between the two methods.
            var copy = method == ClientUpdateMethod.Replace
                       || !File.Exists(existing)
                       || LengthOf(existing) != length
                       || !SameContent(file, existing);

            if (copy) {
                plan.ToWrite.Add((file, relative, length));
                plan.BytesToWrite += length;
            }
        }

        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)) {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(destination, file);

            // A file the newer build does not have is not part of it any more, so it goes - including under
            // Replace, where the whole folder is what is being replaced.
            if (method == ClientUpdateMethod.Replace || !wanted.Contains(relative)) {
                plan.ToRemove.Add(file);
                plan.BytesToRemove += LengthOf(file);
            }
        }

        return plan;
    }

    private sealed class ClientUpdatePlan {

        public List<(string File, string Relative, long Length)> ToWrite { get; } = [];

        public List<string> ToRemove { get; } = [];

        /// <summary>Files that were meant to go and could not, reported rather than passed over.</summary>
        public List<string> Unremoved { get; } = [];

        public long BytesToWrite { get; set; }

        public long BytesToRemove { get; set; }
    }

    /// <summary>Whether two files hold the same bytes, which is what decides a copy under the reading method.</summary>
    private static bool SameContent(string left, string right) {
        try {
            using var first = File.OpenRead(left);
            using var second = File.OpenRead(right);

            return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A file that cannot be read is treated as different: writing it again is the safe direction,
            // and skipping it would leave the copy claiming to be the source when it is not.
            return false;
        }
    }

    private static long LengthOf(string file) {
        try {
            return new FileInfo(file).Length;
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>
    /// Deletes folders left empty under the destination, deepest first.
    /// </summary>
    /// <remarks>
    /// A build that drops a folder would otherwise leave its skeleton behind for ever, and the next comparison
    /// would walk it every time to find nothing in it.
    /// </remarks>
    private static void RemoveEmptyDirectories(string destination) {
        try {
            foreach (var directory in Directory
                         .EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length)) {
                try {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // A folder that will not go is not worth failing an update over.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Nothing left to tidy is not a problem.
        }
    }

    private static string Join(string? existing, string more)
        => string.IsNullOrWhiteSpace(existing) ? more : $"{existing} {more}";
}
