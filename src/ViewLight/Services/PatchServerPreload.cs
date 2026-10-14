using System.Diagnostics;

namespace ViewLight.Services;

/// <summary>One file a preload would copy, and how big it is.</summary>
public sealed record PatchServerPreloadFile(string RelativePath, long Size);

/// <summary>
/// What copying a local Wizard101 install into a patch server's revision would do, and why it cannot be
/// done when it cannot.
/// </summary>
/// <param name="SourceFolder">The client build the files come from.</param>
/// <param name="RevisionFolder">The patch server's own folder for this revision - what is filled in.</param>
/// <param name="Revision">The game revision, e.g. <c>r806919.Wizard_1_610</c>.</param>
/// <param name="Files">The files the revision wants and the patch server does not have.</param>
/// <param name="RevisionFiles">How many files the revision is, all told - the denominator.</param>
/// <param name="AlreadyHeld">Files the patch server already has, at the size the revision says.</param>
/// <param name="HeldAtAnotherSize">Files the patch server has at some other size - left alone, not replaced.</param>
/// <param name="NotInTheBuild">Files the revision wants that this client build does not have at all.</param>
/// <param name="AtAnotherSizeInTheBuild">
/// Files the build has at some other size than the revision says. Not copied: a file of the right name
/// and the wrong length is a different build's file, and copying one in would put it beyond the patch
/// server's reach - it only ever downloads what is missing, never checks what is there.
/// </param>
/// <param name="Refusal">Why a preload cannot happen, or <c>null</c> when it can.</param>
public sealed record PatchServerPreloadPlan(
    string SourceFolder,
    string RevisionFolder,
    string Revision,
    IReadOnlyList<PatchServerPreloadFile> Files,
    int RevisionFiles,
    int AlreadyHeld,
    int HeldAtAnotherSize,
    int NotInTheBuild,
    int AtAnotherSizeInTheBuild,
    string? Refusal) {

    /// <summary>How many files would be copied.</summary>
    public int FilesToCopy => Files.Count;

    /// <summary>How many bytes would be copied.</summary>
    public long BytesToCopy => Files.Sum(file => file.Size);

    /// <summary>Whether there is a preload here at all.</summary>
    public bool IsPossible => Refusal is null;

    /// <summary>Whether there is anything to copy - the difference between offering and saying nothing.</summary>
    public bool IsWorthDoing => IsPossible && Files.Count > 0;
}

/// <summary>
/// The answer to "should the patch server be filled in from this install before this session's server is
/// started?"
/// </summary>
/// <param name="Preload">Whether to make the copy now.</param>
/// <param name="DontAskAgain">
/// Whether to stop asking: remembered as the revision that was turned down, so a later revision asks once
/// more rather than the question being silenced for good.
/// </param>
public readonly record struct PreloadOfferAnswer(bool Preload, bool DontAskAgain);

/// <summary>How far along a preload is, with <see cref="Fraction"/> 0..1.</summary>
public sealed record PatchServerPreloadProgress(double Fraction, string Detail) {
    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);
}

/// <summary>What a preload did. Never an exception: the caller is a progress bar and a sentence.</summary>
public sealed record PatchServerPreloadResult(
    bool Success,
    int FilesCopied,
    long BytesCopied,
    int FilesSkipped,
    int FilesFailed,
    string Message,
    string? Warning = null);

/// <summary>
/// Fills a patch server's revision from a Wizard101 install that already holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is exact rather than hopeful.</b> A patch server mirrors the revision Wizard101's own
/// patch server is serving, and a client build that was installed from the same channel <i>is</i> that
/// revision - measured, on a real Steam install against a real fetched revision: 2,700 files compared
/// byte for byte, none different. So when the two revisions name the same build, a file of the right
/// name and the right length is the same file, and copying it in cannot be copying in somebody else's
/// build. That is the whole of the safety argument, and it is why the build has to match: a file of the
/// right name and the wrong length is refused rather than copied.
/// </para>
/// <para>
/// <b>What it is for.</b> Aurorium fetches a revision whole - thousands of files, tens of gigabytes -
/// and it fetches a file only when it is missing, never checking one it already has. Two consequences,
/// both of which this answers. One: a first fetch is hours of downloading that a machine with a client
/// build already has on disk. Two: Aurorium gives up on a file that cannot be read inside its own
/// two-minute request limit, and the files that are large enough to run past it are exactly the ones a
/// fetch can never finish. Those are the files that need this most.
/// </para>
/// <para>
/// <b>Nothing is overwritten.</b> A file the patch server already has is left exactly as it is, however
/// it got there: what is already in the revision is what the patch server is prepared to serve, and a
/// launcher that quietly replaced it would be inventing its own revision. A file is written under a
/// temporary name and moved into place once it is complete, so a preload interrupted halfway is not
/// mistaken for a downloaded file - Aurorium skips whatever is present, name and all.
/// </para>
/// </remarks>
public sealed class PatchServerPreload {

    /// <summary>Where Aurorium keeps the revisions it has fetched, under its own folder.</summary>
    public const string DefaultDataFolder = PatchServerConfig.DefaultSaveDirectory;

    /// <summary>
    /// What a half-written copy is called.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>.part</c>, which is Aurorium's own name for a download in flight: a fetch and a
    /// preload running at the same time would otherwise write the same temporary file for the file they
    /// both want. Aurorium skips a file only when the name it was given is there, so a stray temporary
    /// file of either kind is invisible to it either way.
    /// </remarks>
    public const string PartSuffix = ".preloadpart";

    // ---------------------------------------------------------------- which build, which folder

    /// <summary>The revision a patch server names a folder after: <c>V_</c> and the game revision.</summary>
    public static string? RevisionNameFor(string? clientBuild) {
        var build = (clientBuild ?? string.Empty).Trim();

        if (build.Length == 0) {
            return null;
        }

        return build.StartsWith("V_", StringComparison.OrdinalIgnoreCase) ? build : $"V_{build}";
    }

    /// <summary>
    /// Whether a revision folder holds the build in <paramref name="clientBuild"/> - the condition the
    /// whole copy rests on.
    /// </summary>
    public static bool IsTheBuild(string? revisionFolderName, string? clientBuild)
        => ClientBuildReader.AreSame(GameRevisionIn(revisionFolderName), GameRevisionIn(clientBuild));

    /// <summary>
    /// The game revision a name stands for: a revision name with its <c>V_</c> taken off, or the name as it
    /// already is.
    /// </summary>
    private static string? GameRevisionIn(string? name)
        => PatchServerConfig.GameRevisionFrom(name) ?? name;

    /// <summary>
    /// The folder a patch server would keep <paramref name="clientBuild"/> in: its own configured data
    /// folder and the revision's name under it.
    /// </summary>
    /// <remarks>
    /// The data folder is read from the patch server's config rather than assumed, because that is where
    /// Aurorium will look: a config that keeps its revisions somewhere else would otherwise have terabytes
    /// copied to a folder nothing ever reads.
    /// </remarks>
    public static string RevisionFolderFor(string? patchServerFolder, string? clientBuild) {
        var root = (patchServerFolder ?? string.Empty).Trim();
        var name = RevisionNameFor(clientBuild);

        if (root.Length == 0 || name is null) {
            return string.Empty;
        }

        var dataFolder = PatchServerConfig.TryReadSaveDirectory(root) ?? DefaultDataFolder;

        return Path.Combine(root, dataFolder, name);
    }

    // ---------------------------------------------------------------- what a preload would do

    /// <summary>There is nothing to copy from.</summary>
    public const string NoSourceFolderMessage = "There is no Wizard101 install to copy from.";

    /// <summary>No patch server folder is set, so there is nowhere to copy to.</summary>
    public const string NoPatchServerFolderMessage = "No patch server folder is set.";

    /// <summary>A folder whose build cannot be read is a folder nothing can be claimed about.</summary>
    public static string UnknownBuildMessage(string sourceFolder)
        => $"The revision of {sourceFolder} could not be read, so there is no telling whether its files "
           + "are the ones this revision wants.";

    /// <summary>The patch server has not written this revision's file list yet, so there is no plan to make.</summary>
    public static string NotFetchedYetMessage(string revision)
        => $"The patch server has not fetched {revision} yet. It writes a revision's file list as soon as "
           + "it starts on one, and there is nothing to fill in until it has.";

    /// <summary>
    /// Works out what copying <paramref name="sourceFolder"/> into the patch server's revision would do.
    /// </summary>
    /// <remarks>
    /// Nothing here writes anything, which is what lets the launcher ask before it acts - and what lets it
    /// say how much it is about to copy, since a revision of this game is tens of gigabytes. A refusal is
    /// part of the answer rather than an exception: a machine with no client build to copy from, or a patch
    /// server that has not been started yet, are both ordinary states rather than mistakes.
    /// </remarks>
    public static PatchServerPreloadPlan Plan(
        string? sourceFolder, string? patchServerFolder, string? clientBuild) {

        var source = (sourceFolder ?? string.Empty).Trim();
        if (source.Length == 0 || !Directory.Exists(source)) {
            return Refused(source, string.Empty, string.Empty, NoSourceFolderMessage);
        }

        var build = (clientBuild ?? string.Empty).Trim();
        if (build.Length == 0 || !GameFolderLocator.LooksLikeGameFolder(source)) {
            return Refused(source, string.Empty, build, UnknownBuildMessage(source));
        }

        var revisionFolder = RevisionFolderFor(patchServerFolder, build);
        if (revisionFolder.Length == 0) {
            return Refused(source, revisionFolder, build, NoPatchServerFolderMessage);
        }

        var manifest = PatchServerManifest.TryRead(revisionFolder);
        if (manifest is null) {
            return Refused(source, revisionFolder, build, NotFetchedYetMessage(build));
        }

        var files = new List<PatchServerPreloadFile>();
        var alreadyHeld = 0;
        var heldAtAnotherSize = 0;
        var notInTheBuild = 0;
        var atAnotherSizeInTheBuild = 0;

        foreach (var entry in manifest) {
            var target = Full(revisionFolder, entry.RelativePath);

            if (File.Exists(target)) {
                // Whatever is there already is what the patch server is prepared to serve, so it is not
                // this method's to replace - only counted, and only against the size the revision says.
                if (LengthOf(target) == entry.Size) {
                    alreadyHeld++;
                }
                else {
                    heldAtAnotherSize++;
                }

                continue;
            }

            var origin = Full(source, entry.RelativePath);

            if (!File.Exists(origin)) {
                notInTheBuild++;
                continue;
            }

            // The right name at the wrong length is another build's file, and there is no repair for a file
            // copied in wrongly: the patch server only ever fetches what is missing. Left for it to fetch.
            if (LengthOf(origin) != entry.Size) {
                atAnotherSizeInTheBuild++;
                continue;
            }

            files.Add(new PatchServerPreloadFile(entry.RelativePath, entry.Size));
        }

        return new PatchServerPreloadPlan(source, revisionFolder, build, files, manifest.Count,
            alreadyHeld, heldAtAnotherSize, notInTheBuild, atAnotherSizeInTheBuild, null);
    }

    private static PatchServerPreloadPlan Refused(
        string source, string revisionFolder, string build, string reason)
        => new(source, revisionFolder, build, [], 0, 0, 0, 0, 0, reason);

    /// <summary>A file the revision names, under the folder it lives in.</summary>
    private static string Full(string folder, string relativePath)
        => Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A file's length, or -1 for one that cannot be measured.</summary>
    private static long LengthOf(string path) {
        try {
            return new FileInfo(path).Length;
        }
        catch (Exception) {
            // Unreadable is not the same as absent, but neither is this revision's file.
            return -1;
        }
    }

    // ---------------------------------------------------------------- doing it

    /// <summary>
    /// Copies the plan's files in, and reports what it did.
    /// </summary>
    /// <remarks>
    /// Never throws, and never leaves a half-written file where a whole one belongs. Cancelling is an
    /// answer rather than an error: what has been copied stays, because it is correct and the patch server
    /// already counts it as one of its own, and running again copies only what is still missing.
    /// </remarks>
    public async Task<PatchServerPreloadResult> RunAsync(
        PatchServerPreloadPlan plan,
        IProgress<PatchServerPreloadProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (plan.Refusal is { Length: > 0 } refusal) {
            return new PatchServerPreloadResult(false, 0, 0, 0, 0, refusal);
        }

        if (plan.Files.Count == 0) {
            progress?.Report(new PatchServerPreloadProgress(1, "Nothing to copy."));
            return new PatchServerPreloadResult(true, 0, 0, 0, 0,
                $"There is nothing to copy into {plan.Revision}: the patch server already has every file "
                + "of the revision that this Wizard101 install can offer it.");
        }

        return await Task.Run(() => Copy(plan, progress, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static PatchServerPreloadResult Copy(
        PatchServerPreloadPlan plan,
        IProgress<PatchServerPreloadProgress>? progress,
        CancellationToken cancellationToken) {

        var totalBytes = plan.BytesToCopy;
        var copied = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<string>();
        long copiedBytes = 0;
        var cancelled = false;
        var watch = Stopwatch.StartNew();

        foreach (var file in plan.Files) {
            if (cancellationToken.IsCancellationRequested) {
                cancelled = true;
                break;
            }

            var origin = Full(plan.SourceFolder, file.RelativePath);
            var target = Full(plan.RevisionFolder, file.RelativePath);

            if (File.Exists(target)) {
                skipped++;
                continue;
            }

            try {
                // Measured again rather than trusted from the plan: the plan can be minutes old, and a game
                // that patched itself while the launcher was asking is not the build it was planned from.
                if (LengthOf(origin) != file.Size) {
                    failed++;
                    Noted(failures, file.RelativePath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Written aside and moved into place, so an interrupted copy is not a file the patch server
                // would count as one it already has.
                var staging = target + PartSuffix;
                File.Copy(origin, staging, overwrite: true);
                File.Move(staging, target, overwrite: false);

                copied++;
                copiedBytes += file.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A file that arrived from somewhere else in the meantime is not a failure: it is the file.
                if (File.Exists(target)) {
                    skipped++;
                }
                else {
                    failed++;
                    Noted(failures, file.RelativePath);
                }

                Discard(target + PartSuffix);
            }

            // Throttled: a callback per file would flood the UI on a revision of thousands of files.
            if (watch.ElapsedMilliseconds >= 200) {
                watch.Restart();
                progress?.Report(new PatchServerPreloadProgress(
                    totalBytes > 0 ? (double)copiedBytes / totalBytes : 0,
                    Describe(copied, plan.Files.Count, copiedBytes, totalBytes)));
            }
        }

        progress?.Report(new PatchServerPreloadProgress(cancelled ? 0 : 1, cancelled ? "Stopped." : "Copied."));

        var message = cancelled
            ? $"Copying {plan.Revision} into the patch server was stopped after {copied} files "
              + $"({ServerInstallService.FormatBytes(copiedBytes)}). What was copied is in place, so "
              + "running this again copies only what is still missing."
            : copied == 0
                ? $"Nothing was copied into {plan.Revision}" + (skipped > 0
                    ? $": all {skipped} files were already there."
                    : ".")
                : $"Copied {copied} files ({ServerInstallService.FormatBytes(copiedBytes)}) into the patch "
                  + $"server's {plan.Revision} folder."
                  + (skipped > 0 ? $" {skipped} were already there." : string.Empty);

        var warning = failed == 0
            ? null
            : $"{failed} of {plan.Files.Count} files could not be copied ({string.Join(", ", failures)}). "
              + "The patch server fetches what it does not have, so those are left to it.";

        return new PatchServerPreloadResult(!cancelled, copied, copiedBytes, skipped, failed, message, warning);
    }

    private static string Describe(int copied, int total, long bytes, long totalBytes)
        => $"{copied} of {total} files — {ServerInstallService.FormatBytes(bytes)} of "
           + $"{ServerInstallService.FormatBytes(totalBytes)}";

    /// <summary>Keeps the first few names for the report, because a wall of paths is not a sentence.</summary>
    private static void Noted(List<string> failures, string relativePath) {
        if (failures.Count < 3) {
            failures.Add(relativePath);
        }
    }

    private static void Discard(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (Exception) {
            // A temporary file left behind is invisible to the patch server, which skips by name.
        }
    }
}
