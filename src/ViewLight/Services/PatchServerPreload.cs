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
/// <param name="Revision">
/// The game revision being filled in, e.g. <c>r806919.Wizard_1_610</c> - and it is the revision <b>the
/// server asks for</b>, not the one the client happens to be built at. The two are the same or nothing is
/// copied at all, which is what makes this a handout rather than the launcher inventing a revision.
/// </param>
/// <param name="Files">The files the revision wants and the patch server does not have.</param>
/// <param name="RevisionFiles">How many files the revision is, all told - the denominator.</param>
/// <param name="AlreadyHeld">Files the patch server already has, at the size the revision says.</param>
/// <param name="HeldAtAnotherSize">Files the patch server has at some other size - left alone, not replaced.</param>
/// <param name="NotInTheBuild">
/// Files the revision wants that this client build does not have at all. Not a refusal: they are simply not
/// this machine's to give, and the patch server fetches them.
/// </param>
/// <param name="AtAnotherSizeInTheBuild">
/// Files the build has at some other size than the revision says - and any one of them <b>refuses the whole
/// copy</b>. A file of the right name at the wrong length is a different build's file, and a machine holding
/// even one of those is not a machine holding this revision: handing over the rest of it would be filing
/// some other build's files under this revision's names, where the patch server would serve them forever,
/// because it only ever downloads what is missing and never checks what is already there.
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
/// Fills a patch server's revision from a Wizard101 install that already holds exactly that revision.
/// </summary>
/// <remarks>
/// <para>
/// <b>The patch server manages itself; this is the one thing the launcher may hand it.</b> Aurorium fetches
/// its revision its own way - it asks Wizard101's patch server for the file list, downloads what that list
/// names, and skips whatever is already on disk - and nothing here is a second opinion about how that should
/// be done. What it is is a source of <i>the same bytes</i> for a machine that already has them:
/// the launcher may put files where the patch server would have put them, under the names the patch
/// server's own file list gives, and Aurorium skips them by name exactly as it skips its own downloads.
/// </para>
/// <para>
/// <b>Why this is exact rather than hopeful.</b> Measured on a real Steam install against a real fetched
/// revision: every one of the 3,725 files the two had in common - the whole of <c>Data\</c> and
/// <c>Bin\</c> - matched in length exactly. So a file of the right name and the right length is the same
/// file, and copying it in cannot be copying in somebody else's build. Three conditions make it so, and
/// all three must hold: the patch server's file list is the one being filled, the client build is the
/// revision <b>the server asks for</b> rather than the one the client happens to name, and not one file
/// the list names is present in the build at another length - which is what a different build's copy
/// looks like. Any of those failing means nothing is copied, and the message says which one it was.
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
/// <b>Nothing is overwritten, and nothing is invented.</b> A file the patch server already has is left
/// exactly as it is, however it got there: what is already in the revision is what the patch server is
/// prepared to serve, and a launcher that quietly replaced it would be inventing its own revision. A file
/// is written under a temporary name and moved into place once it is complete, so a preload interrupted
/// halfway is not mistaken for a downloaded file - Aurorium skips whatever is present, name and all.
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
    /// The folder a patch server would keep <paramref name="revisionName"/> in: its own configured data
    /// folder and the revision's name under it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data folder is read from the patch server's config rather than assumed, because that is where
    /// Aurorium will look: a config that keeps its revisions somewhere else would otherwise have terabytes
    /// copied to a folder nothing ever reads.
    /// </para>
    /// <para>
    /// The parameter takes either the revision's own name (<c>V_r806919.Wizard_1_610</c>) or the game
    /// revision it stands for, because both spellings turn up: a revision folder is named after one, and the
    /// server's <c>GameRevision</c> is the other.
    /// </para>
    /// </remarks>
    public static string RevisionFolderFor(string? patchServerFolder, string? revisionName) {
        var root = (patchServerFolder ?? string.Empty).Trim();
        var name = RevisionNameFor(revisionName);

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

    /// <summary>Nothing says which revision the server wants, so there is nothing to fill in.</summary>
    public const string NoRevisionMessage =
        "There is no revision for the server to ask the patch server for yet, so there is nothing to fill "
        + "in. The revision is the one in Imlight's own config (GameRevision), under Settings → Network.";

    /// <summary>
    /// The client here is a different revision from the one the server asks for, so its files are not this
    /// revision's files and none of them may be handed over.
    /// </summary>
    public static string DifferentBuildMessage(string? clientBuild, string serverRevision)
        => $"This Wizard101 install is {clientLabel(clientBuild)}, and the server asks the patch server for "
           + $"{serverRevision}. Only the revision the server asks for is filled in, and only from a client "
           + "that is the same revision - anything else would be filing another build's files under this "
           + "revision's names.";

    /// <summary>
    /// One file of the client build is not the length the revision gives it, which is what a different build
    /// looks like - and that is the end of it, because the patch server never checks a file it already has.
    /// </summary>
    public static string NotThisBuildMessage(int files, string? example)
        => $"{files:N0} of the revision's files are here at a different length than the revision gives them"
           + (string.IsNullOrEmpty(example) ? string.Empty : $" ({example}, and others)")
           + ". That is another build's copy of them, and a file of the right name at the wrong length is "
           + "one the patch server would never repair - it only ever downloads what is missing. Nothing is "
           + "copied from it, and those files are left for the patch server to fetch.";

    private static string clientLabel(string? clientBuild)
        => string.IsNullOrWhiteSpace(clientBuild) ? "of an unreadable revision" : clientBuild;

    /// <summary>
    /// Works out what copying <paramref name="sourceFolder"/> into the patch server's revision would do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here writes anything, which is what lets the launcher ask before it acts - and what lets it
    /// say how much it is about to copy, since a revision of this game is tens of gigabytes. A refusal is
    /// part of the answer rather than an exception: a machine with no client build to copy from, or a patch
    /// server that has not been started yet, are both ordinary states rather than mistakes.
    /// </para>
    /// <para>
    /// <b>The revision filled in is the server's, not the client's.</b> <paramref name="serverRevision"/> is
    /// the revision the server asks the patch server for - <c>GameRevision</c> - and the client build has to
    /// be that same revision or nothing happens. Filling in the client's own revision instead would leave the
    /// server asking for a folder the patch server does not have, which is the 404 this whole arrangement
    /// exists to avoid.
    /// </para>
    /// </remarks>
    /// <param name="sourceFolder">The Wizard101 install the files would come from.</param>
    /// <param name="patchServerFolder">The folder the patch server is installed in.</param>
    /// <param name="serverRevision">The revision the server asks for, e.g. <c>r806919.Wizard_1_610</c>.</param>
    /// <param name="clientBuild">The revision of the install in <paramref name="sourceFolder"/>.</param>
    public static PatchServerPreloadPlan Plan(
        string? sourceFolder, string? patchServerFolder, string? serverRevision, string? clientBuild) {

        var revision = (serverRevision ?? string.Empty).Trim();
        var build = (clientBuild ?? string.Empty).Trim();

        // Nothing says which revision to fill in, so there is nothing this could be for. Checked before the
        // source folder, because it is a fact about the server rather than about this machine's game.
        if (revision.Length == 0) {
            return Refused(sourceFolder, string.Empty, string.Empty, NoRevisionMessage);
        }

        var source = (sourceFolder ?? string.Empty).Trim();
        if (source.Length == 0 || !Directory.Exists(source)) {
            return Refused(source, string.Empty, revision, NoSourceFolderMessage);
        }

        if (build.Length == 0 || !GameFolderLocator.LooksLikeGameFolder(source)) {
            return Refused(source, string.Empty, revision, UnknownBuildMessage(source));
        }

        // The one condition the whole handout rests on: this install is the revision the server asks for.
        // Not the newest, not a neighbouring one - the one the address Imlight is given resolves to.
        if (!ClientBuildReader.AreSame(build, revision)) {
            return Refused(source, string.Empty, revision, DifferentBuildMessage(build, revision));
        }

        var revisionFolder = RevisionFolderFor(patchServerFolder, revision);
        if (revisionFolder.Length == 0) {
            return Refused(source, revisionFolder, revision, NoPatchServerFolderMessage);
        }

        var manifest = PatchServerManifest.TryRead(revisionFolder);
        if (manifest is null) {
            return Refused(source, revisionFolder, revision, NotFetchedYetMessage(revision));
        }

        var files = new List<PatchServerPreloadFile>();
        var alreadyHeld = 0;
        var heldAtAnotherSize = 0;
        var notInTheBuild = 0;
        var atAnotherSizeInTheBuild = 0;
        string? firstMismatch = null;

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
                // Not this machine's to give. Left to the patch server, which fetches what it lacks.
                notInTheBuild++;
                continue;
            }

            // The right name at the wrong length is another build's file, and it takes the whole copy with
            // it. There is no repair for a file copied in wrongly - the patch server only ever fetches what
            // is missing - so a build that is not this revision is a build nothing may be taken from.
            if (LengthOf(origin) != entry.Size) {
                atAnotherSizeInTheBuild++;
                firstMismatch ??= entry.RelativePath;
                continue;
            }

            files.Add(new PatchServerPreloadFile(entry.RelativePath, entry.Size));
        }

        if (atAnotherSizeInTheBuild > 0) {
            return new PatchServerPreloadPlan(source, revisionFolder, revision, [], manifest.Count,
                alreadyHeld, heldAtAnotherSize, notInTheBuild, atAnotherSizeInTheBuild,
                NotThisBuildMessage(atAnotherSizeInTheBuild, firstMismatch));
        }

        return new PatchServerPreloadPlan(source, revisionFolder, revision, files, manifest.Count,
            alreadyHeld, heldAtAnotherSize, notInTheBuild, atAnotherSizeInTheBuild, null);
    }

    private static PatchServerPreloadPlan Refused(
        string? source, string revisionFolder, string revision, string reason)
        => new((source ?? string.Empty).Trim(), revisionFolder, revision, [], 0, 0, 0, 0, 0, reason);

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
