namespace ViewLight.Services;

/// <summary>
/// What a revision on the patch server's disk is for.
/// </summary>
/// <remarks>
/// One revision is in use at any moment and everything else is an older copy of the same game, which is
/// worth keeping only deliberately - each one is around 18 GB. <see cref="ServerRevision"/> is the revision
/// the server's own config asks the patch server for, which is the one a session cannot do without, and
/// <see cref="NewestRevision"/> is the newest the patch server has fetched. Those are the same folder while
/// the sync is on, which is the default: the launcher writes the patch server's revision into the server's
/// config, so the server asks for what the patch server holds. They are stated apart because they come
/// apart when the sync is off, and then both are still worth keeping - the server cannot serve its clients
/// without its own revision, and the patch server cannot be kept from fetching the newest one.
/// </remarks>
public enum PatchServerRevisionRole {

    /// <summary>The revision the server asks the patch server for, and the one worth having most.</summary>
    ServerRevision,

    /// <summary>
    /// The newest revision the patch server has fetched - the same folder as
    /// <see cref="ServerRevision"/> unless the sync is off.
    /// </summary>
    NewestRevision,

    /// <summary>
    /// Neither: an older copy of the game that nobody is asking for. The only kind automatic cleanup ever
    /// removes, because the newest one is what the patch server fetches and serves.
    /// </summary>
    Unused,
}

/// <summary>One revision folder on the patch server's disk.</summary>
/// <param name="Name">The folder's own name, as Aurorium names it: <c>V_r806919.Wizard_1_610</c>.</param>
/// <param name="Folder">The folder itself.</param>
/// <param name="GameRevision">The game revision the name stands for, or <c>null</c> when it is not one.</param>
/// <param name="Role">What it is for - see <see cref="PatchServerRevisionRole"/>.</param>
/// <param name="Bytes">How many bytes it holds.</param>
/// <param name="Files">How many files it holds.</param>
/// <param name="LastWrite">When anything under it was last written, or <c>null</c> when that cannot be read.</param>
/// <param name="HasADownloadInFlight">
/// Whether the patch server is part-way through a file in it. Four gigabytes of <c>.part</c> is a revision
/// being fetched right now, and nothing may touch it.
/// </param>
public sealed record PatchServerRevisionOnDisk(
    string Name,
    string Folder,
    string? GameRevision,
    PatchServerRevisionRole Role,
    long Bytes,
    int Files,
    DateTimeOffset? LastWrite,
    bool HasADownloadInFlight) {

    /// <summary>
    /// Whether the launcher may delete it: spare, and not being written to.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The role is the rule; the download in flight is the safety net under it, because
    /// a revision Aurorium has just started on is in its database before a single file of it is on disk -
    /// and its file list is written before anything is downloaded, which means a brand new revision looks
    /// exactly like an unused one for the first moments of its life.
    /// </remarks>
    public bool CanBeDeleted => Role == PatchServerRevisionRole.Unused && !HasADownloadInFlight;

    /// <summary>What it weighs, in words, for the list in Settings.</summary>
    public string SizeText => ServerInstallService.FormatBytes(Bytes);

    /// <summary>What it is for, in one phrase.</summary>
    /// <remarks>
    /// A download in flight is said first, whatever the role: a revision the patch server is writing into is
    /// one that is being fetched right now, which is more use to a reader than whether it is the newest one.
    /// It is guarded on the role as well, because an older revision left part-written by an interrupted run
    /// is one nothing may remove and one nothing is fetching either.
    /// </remarks>
    public string RoleText => HasADownloadInFlight
        ? "Being fetched right now — kept"
        : Role switch {
            PatchServerRevisionRole.ServerRevision => "The revision your server asks for — kept",
            PatchServerRevisionRole.NewestRevision => "The newest the patch server has — kept",
            _ => "An older revision, which nothing is asking for",
        };

    /// <summary>How many files, in words.</summary>
    public string FilesText => $"{Files:N0} files";
}

/// <summary>
/// What the patch server has fetched, and what may be done about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the launcher tidying up its own folder, not managing the patch server.</b> Aurorium fetches a
/// revision whenever Wizard101 publishes one, writes it beside the ones it already has, and never removes
/// anything - which is the correct behaviour for it to have and the reason a machine that has been running
/// a patch server for a year is holding a few hundred gigabytes. What is removed here is a revision
/// <i>nothing asks for</i>: not the one the server's own config asks the patch server for, and not the newest
/// one the patch server has - which, while the revision sync is on, are the same folder. The patch server is
/// never told anything, never restarted, and never has its database touched.
/// </para>
/// <para>
/// <b>Nothing here decides on its own that something may be deleted.</b> A revision is only ever removed
/// when the caller asked for that revision by name, and the two questions above - is it spare, is it being
/// written to - are answered for it first. Every removal is reported with what it was and why, so the
/// decision is visible after the fact as well as before it.
/// </para>
/// </remarks>
public static class PatchServerRevisionCatalogue {

    /// <summary>
    /// How recently a revision must have been written to for it to be treated as one in use.
    /// </summary>
    /// <remarks>
    /// A fetch writes into a revision folder continuously, so \"recently written to\" is what a fetch looks
    /// like from outside. The window is generous because the failure it guards against - deleting a fetch
    /// that has just begun, whose folder has no <c>.part</c> file in it yet - costs a whole revision's
    /// download, and the cost of the other direction is one revision that has to be cleaned up later.
    /// </remarks>
    public static readonly TimeSpan RecentlyWritten = TimeSpan.FromMinutes(20);

    /// <summary>Everything the patch server has fetched, newest revision first.</summary>
    /// <param name="patchServerFolder">The folder the patch server is installed in.</param>
    /// <param name="serverRevision">The revision the server asks for, or empty when there is no server.</param>
    /// <param name="now">The moment to judge \"recently written to\" against. Given so it can be tested.</param>
    public static IReadOnlyList<PatchServerRevisionOnDisk> List(
        string? patchServerFolder,
        string? serverRevision,
        DateTimeOffset? now = null) {

        var root = (patchServerFolder ?? string.Empty).Trim();
        if (root.Length == 0) {
            return [];
        }

        var dataFolder = Path.Combine(root, PatchServerConfig.TryReadSaveDirectory(root)
                                           ?? PatchServerPreload.DefaultDataFolder);

        if (!Directory.Exists(dataFolder)) {
            return [];
        }

        var at = now ?? DateTimeOffset.Now;
        var folders = new List<FolderOnDisk>();

        try {
            foreach (var folder in Directory.EnumerateDirectories(dataFolder)) {
                var name = Path.GetFileName(folder);

                // Only Aurorium's own revisions: a folder somebody keeps beside them is theirs, whatever it
                // holds. This is also what stops a stray folder in the data directory from ever being read
                // as something the launcher may remove.
                var revision = PatchServerConfig.GameRevisionFrom(name);
                if (revision is null) {
                    continue;
                }

                var (bytes, files, lastWrite) = Measure(folder);

                folders.Add(new FolderOnDisk(name, folder, revision, bytes, files, lastWrite));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A data folder that cannot be walked is one nothing can be claimed about, and a partial answer
            // would offer to delete revisions it has not seen.
            return [];
        }

        // Worked out from the folders themselves rather than asked of the patch server: what is on this disk
        // is what this is about, and the newest folder here is the one the patch server is on even when it
        // is not running to say so.
        var newest = NewestOf(folders);

        return [.. folders
            .Select(folder => new PatchServerRevisionOnDisk(
                folder.Name,
                folder.Folder,
                folder.Revision,
                RoleOf(folder.Revision, serverRevision, newest),
                folder.Bytes,
                folder.Files,
                folder.LastWrite,
                HasADownloadInFlight(folder.Folder) || IsRecent(folder.LastWrite, at)))
            .OrderByDescending(revision => RevisionNumber(revision.GameRevision))];
    }

    /// <summary>The newest of the folders found, or <c>null</c> when there are none.</summary>
    private static string? NewestOf(IReadOnlyList<FolderOnDisk> folders)
        => folders.Count == 0 ? null : folders.MaxBy(folder => RevisionNumber(folder.Revision))!.Revision;

    /// <summary>One revision folder as the walk found it, before its role is worked out.</summary>
    private sealed record FolderOnDisk(
        string Name, string Folder, string Revision, long Bytes, int Files, DateTimeOffset? LastWrite);

    /// <summary>What a revision is for, by comparing it with the two that are in use.</summary>
    /// <remarks>
    /// Stated as a rule of its own so the answer is testable without a disk: the server's revision wins when
    /// a revision is somehow both, because the server cannot serve its clients without it. The newest one is
    /// kept because it is what the patch server is fetching and serving, and removing it would be the
    /// launcher deleting a fetch that is the whole reason the folder exists - and there is nothing here that
    /// keeps a revision for the <i>client's</i> sake any more, because the client's own files are in the game
    /// folder and a session asks the patch server only for the revision the server names.
    /// </remarks>
    public static PatchServerRevisionRole RoleOf(
        string? revision, string? serverRevision, string? newestRevision) {

        if (ClientBuildReader.AreSame(revision, serverRevision)) {
            return PatchServerRevisionRole.ServerRevision;
        }

        return ClientBuildReader.AreSame(revision, newestRevision)
            ? PatchServerRevisionRole.NewestRevision
            : PatchServerRevisionRole.Unused;
    }

    /// <summary>
    /// The spare revisions in a list - what automatic cleanup would remove, before the caller has said
    /// anything about it.
    /// </summary>
    public static IReadOnlyList<PatchServerRevisionOnDisk> Spare(
        IEnumerable<PatchServerRevisionOnDisk>? revisions)
        => [.. (revisions ?? []).Where(revision => revision.CanBeDeleted)];

    /// <summary>
    /// Removes the revisions given, and reports each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every revision is checked immediately before it is removed rather than trusted from the list it came
    /// out of, and the check is repeated per revision rather than once for the batch: a fetch that starts
    /// between two deletions is a fetch the second deletion must not touch. A revision that is refused is
    /// reported as kept, with the reason, because a cleanup that silently does nothing is worse than one
    /// that says which folder it left alone and why.
    /// </para>
    /// <para>
    /// Off the UI thread when called from the view model: removing a revision is tens of gigabytes of file
    /// system work. It never throws - a folder that refuses to go is a line in the report, not a failed
    /// session.
    /// </para>
    /// </remarks>
    /// <param name="revisions">The revisions to remove, by the list they were read into.</param>
    /// <param name="onlySpare">
    /// Whether to refuse anything that is not spare. On for automatic cleanup, which is only ever allowed
    /// the two it may remove; off for the manual one, where the user's own list is the permission.
    /// </param>
    public static PatchServerCleanupResult Delete(
        IEnumerable<PatchServerRevisionOnDisk>? revisions,
        bool onlySpare = true,
        DateTimeOffset? now = null) {

        var removed = new List<PatchServerRevisionOnDisk>();
        var kept = new List<(PatchServerRevisionOnDisk Revision, string Why)>();
        var at = now ?? DateTimeOffset.Now;
        long freed = 0;
        var filesFreed = 0;

        foreach (var revision in revisions ?? []) {
            // Asked again here rather than trusted from the list it came out of: a fetch that starts between
            // two removals is one the second removal must not touch. The two callers differ only in what
            // they are allowed to remove - automatic cleanup may only take the spare ones, the manual list
            // is the user's own decision - and never in what is being written to.
            var refused = onlySpare ? Check(revision, at, out var why) ? null : why
                                    : BeingWrittenTo(revision, at);

            if (refused is not null) {
                kept.Add((revision, refused));
                continue;
            }

            try {
                Directory.Delete(revision.Folder, recursive: true);

                removed.Add(revision);
                freed += revision.Bytes;
                filesFreed += revision.Files;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                kept.Add((revision, $"it could not be removed ({ex.GetType().Name})"));
            }
        }

        return new PatchServerCleanupResult(removed, kept, freed, filesFreed);
    }

    /// <summary>
    /// Whether automatic cleanup may remove a revision, with the reason when it may not.
    /// </summary>
    private static bool Check(
        PatchServerRevisionOnDisk revision, DateTimeOffset now, out string why) {

        why = string.Empty;

        if (revision.Role != PatchServerRevisionRole.Unused) {
            why = revision.RoleText;
            return false;
        }

        why = BeingWrittenTo(revision, now) ?? string.Empty;

        return why.Length == 0;
    }

    /// <summary>
    /// Why a revision must not be touched whatever the caller asked for, or <c>null</c> when it may be.
    /// </summary>
    /// <remarks>
    /// The one rule the manual list does not get to overrule. A revision the patch server is part-way
    /// through is a revision nothing may remove, because the fetch behind it only ever advances while the
    /// files it has written stay where they are.
    /// </remarks>
    private static string? BeingWrittenTo(PatchServerRevisionOnDisk revision, DateTimeOffset now)
        => revision.HasADownloadInFlight || IsRecent(revision.LastWrite, now)
            ? "the patch server is writing into it"
            : null;

    private static bool IsRecent(DateTimeOffset? lastWrite, DateTimeOffset now)
        => lastWrite is { } written && now - written < RecentlyWritten;

    /// <summary>
    /// Whether a folder holds a download in flight, which is Aurorium's own <c>.part</c> name at its side.
    /// </summary>
    private static bool HasADownloadInFlight(string folder) {
        try {
            return Directory
                .EnumerateFiles(folder, "*" + PatchServerFetch.DownloadSuffix, SearchOption.AllDirectories)
                .Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A folder that cannot be walked is treated as one being written to: refusing to delete is the
            // recoverable direction, and this is not a state a revision sits in for long.
            return true;
        }
    }

    /// <summary>How many files it holds, how much they weigh, and when the newest of them was written.</summary>
    private static (long Bytes, int Files, DateTimeOffset? LastWrite) Measure(string folder) {
        long bytes = 0;
        var files = 0;
        DateTime? newest = null;

        try {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) {
                try {
                    var info = new FileInfo(path);
                    bytes += info.Length;
                    files++;

                    if (newest is null || info.LastWriteTimeUtc > newest.Value) {
                        newest = info.LastWriteTimeUtc;
                    }
                }
                catch (Exception) {
                    // A file that cannot be measured still counts as one: the figure is a size for the list,
                    // not a promise about the folder.
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // What was counted before it gave up stands: a folder the walk could not finish is still one
            // with files in it, and reporting zero would be a worse answer than a short one.
        }

        return (bytes, files, newest is { } written ? new DateTimeOffset(written, TimeSpan.Zero) : null);
    }

    /// <summary>The number in a revision, for ordering the list newest first.</summary>
    private static long RevisionNumber(string? revision) {
        if (string.IsNullOrWhiteSpace(revision)) {
            return 0;
        }

        foreach (var part in revision.Split('.', '-', '_')) {
            var digits = new string([.. part.Where(char.IsDigit)]);

            if (digits.Length >= 4 && long.TryParse(digits, out var number)) {
                return number;
            }
        }

        return 0;
    }
}

/// <summary>What a cleanup did: what went, what stayed, and why.</summary>
/// <param name="Removed">The revisions that were removed.</param>
/// <param name="Kept">The ones that were refused, each with the reason in words.</param>
/// <param name="BytesFreed">How much disk the removals gave back.</param>
/// <param name="FilesFreed">How many files that was.</param>
public sealed record PatchServerCleanupResult(
    IReadOnlyList<PatchServerRevisionOnDisk> Removed,
    IReadOnlyList<(PatchServerRevisionOnDisk Revision, string Why)> Kept,
    long BytesFreed,
    int FilesFreed) {

    /// <summary>Whether anything was removed at all.</summary>
    public bool RemovedAnything => Removed.Count > 0;

    /// <summary>The whole cleanup in one sentence, for the Log and for the card.</summary>
    public string Describe() {
        var head = RemovedAnything
            ? $"Removed {Removed.Count} unused revision{(Removed.Count == 1 ? string.Empty : "s")} "
              + $"({ServerInstallService.FormatBytes(BytesFreed)} back, {FilesFreed:N0} files)"
            : "Nothing was removed";

        if (Kept.Count == 0) {
            return $"{head}.";
        }

        var names = Kept.Take(4).Select(kept => $"{kept.Revision.Name} ({kept.Why})");

        return $"{head}. Kept {Kept.Count} ({string.Join("; ", names)}"
               + (Kept.Count > 4 ? "; and more" : string.Empty)
               + ").";
    }
}
