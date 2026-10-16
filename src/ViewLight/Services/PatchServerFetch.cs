namespace ViewLight.Services;

/// <summary>
/// One file the patch server is part-way through: what it will be, how much of it has arrived, and how
/// big it says it is.
/// </summary>
/// <param name="RelativePath">
/// Where the file belongs in the revision - <c>Data/GameData/Root.wad</c> - which is what the revision's
/// own file list names it by, and so what it can be looked up in that list as.
/// </param>
/// <param name="BytesSoFar">How much of it is on disk, from the patch server's own <c>.part</c> file.</param>
/// <param name="TotalBytes">
/// How big the file is according to the revision's file list, or <c>0</c> for one the list does not name
/// at all - in which case there is a size to report as unknown rather than a number to make up.
/// </param>
/// <remarks>
/// This is what makes the fetch a thing being watched rather than a percentage: a revision is thousands of
/// files and one of them can be a gigabyte, so "which file, and how far into it" is the difference between
/// a number that moves and a number that moves for a reason. Nothing here is asked of the patch server: the
/// file names come from the <c>.part</c> files Aurorium writes while a download is in flight and its own
/// file list, read off the disk.
/// </remarks>
public sealed record PatchServerDownloadInFlight(string RelativePath, long BytesSoFar, long TotalBytes) {

    /// <summary>The file's own name, without the folders in front of it.</summary>
    public string FileName {
        get {
            var slash = RelativePath.LastIndexOf('/');

            return slash < 0 ? RelativePath : RelativePath[(slash + 1)..];
        }
    }

    /// <summary>How much of it has arrived, 0..1.</summary>
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesSoFar / TotalBytes, 0, 1);

    /// <summary>Whether the revision's own file list names this file, which is what gives it a size.</summary>
    public bool HasASize => TotalBytes > 0;

    /// <summary>
    /// The file and how far into it, for the line on screen: <c>Root.wad (12.3 MB of 281.4 MB)</c>.
    /// </summary>
    public string Describe() => HasASize
        ? $"{FileName} ({ServerInstallService.FormatBytes(BytesSoFar)} of "
          + $"{ServerInstallService.FormatBytes(TotalBytes)})"
        : $"{FileName} ({ServerInstallService.FormatBytes(BytesSoFar)} so far)";
}

/// <summary>
/// How much of a revision a patch server has fetched, counted out of the revision's own file list.
/// </summary>
/// <param name="Revision">The revision's name, as the patch server names its folder: <c>V_r806919.Wizard_1_610</c>.</param>
/// <param name="RevisionFolder">The folder this was counted in.</param>
/// <param name="TotalFiles">How many files the revision is, all told - the denominator.</param>
/// <param name="TotalBytes">How many bytes the revision is, all told.</param>
/// <param name="HeldFiles">Files that are there, at the size the file list gives for them.</param>
/// <param name="HeldBytes">How much those weigh.</param>
/// <param name="PartialBytes">
/// Bytes of files the patch server is part-way through, written under its own <c>.part</c> name. Counted
/// because they are what makes the figure move while a single large file is being written, rather than
/// only when one finishes.
/// </param>
/// <param name="InFlight">
/// Those files themselves, newest first, so the line can say which one it is on and how far into it it is.
/// </param>
/// <remarks>
/// This is a measurement of the disk, not a claim about the patch server's own progress: the patch server
/// keeps that to itself, on its console. What it does leave behind is what it will serve - the revision's
/// file list, and the files it has written under the folder that list names - and that is enough to say how
/// much of a revision is here and how much is not.
/// </remarks>
public sealed record PatchServerFetchState(
    string Revision,
    string RevisionFolder,
    int TotalFiles,
    long TotalBytes,
    int HeldFiles,
    long HeldBytes,
    long PartialBytes,
    IReadOnlyList<PatchServerDownloadInFlight> InFlight) {

    /// <summary>Bytes of this revision that are on disk, complete or in flight.</summary>
    public long FetchedBytes => HeldBytes + PartialBytes;

    /// <summary>How many files the patch server is writing into right now.</summary>
    public int FilesInFlight => InFlight.Count;

    /// <summary>
    /// The file it is on now: the one most recently written to, or <c>null</c> when none is being written.
    /// </summary>
    /// <remarks>
    /// Aurorium downloads several files at once and writes each of them as its own <c>.part</c> file, so
    /// there is no single file to name - there is a set, and the one that has been written to most recently
    /// is the best answer this method can give. Which is why it is reported along with how many are in
    /// flight rather than on its own: one name presented as "the" file would be a guess dressed up as a fact.
    /// </remarks>
    public PatchServerDownloadInFlight? CurrentFile => InFlight.Count == 0 ? null : InFlight[0];

    /// <summary>
    /// Bytes still to arrive, which is what an estimate is an estimate of.
    /// </summary>
    /// <remarks>
    /// Held <i>and</i> in flight are both taken off, because both are already on disk: a file being written
    /// now has most of its bytes here, and counting it as outstanding would make the estimate jump at the
    /// end of every large file.
    /// </remarks>
    public long MissingBytes => Math.Max(0, TotalBytes - FetchedBytes);

    /// <summary>Files the patch server does not have yet, at the size the revision gives them.</summary>
    public int MissingFiles => Math.Max(0, TotalFiles - HeldFiles);

    /// <summary>Whether every file of the revision is here.</summary>
    public bool IsComplete => TotalFiles > 0 && MissingFiles == 0;

    /// <summary>How far along it is, 0..1, by bytes.</summary>
    public double Fraction => TotalBytes <= 0
        ? (IsComplete ? 1 : 0)
        : Math.Clamp((double)FetchedBytes / TotalBytes, 0, 1);

    /// <summary>The same as a percentage, for a progress bar.</summary>
    public double Percent => Math.Round(Fraction * 100, 1);
}

/// <summary>
/// What a run of measurements says about how fast a fetch is going, and when it will be done.
/// </summary>
/// <param name="MissingBytes">What is left now.</param>
/// <param name="BytesPerSecond">
/// The rate worked out from the measurements so far, or <c>null</c> while there are too few of them to
/// measure one.
/// </param>
/// <param name="Remaining">How long it should take at that rate, or <c>null</c> when there is no rate to say.</param>
/// <param name="FinishesAt">The wall-clock moment that lands on, or <c>null</c> for the same reason.</param>
/// <param name="Stalled">
/// Nothing has arrived since the last measurement. Said rather than smoothed over: a first fetch that has
/// given up on a file and is waiting out its retry interval looks exactly like this, and an estimate that
/// went on counting down through it would be a lie the user can watch not come true.
/// </param>
public sealed record PatchServerFetchEstimate(
    long MissingBytes,
    double? BytesPerSecond,
    TimeSpan? Remaining,
    DateTimeOffset? FinishesAt,
    bool Stalled);

/// <summary>
/// Turns a series of <see cref="PatchServerFetchState"/> readings into a rate and a finish time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not assumed.</b> The patch server does not say how fast it is downloading or how much is
/// left, so the launcher times the only thing it can see: how many bytes of the revision have landed. A
/// rate is only taken from a window of at least <see cref="MinimumWindow"/>, so a single slow sample cannot
/// turn into an absurd estimate, and each one is blended into the last
/// (<see cref="Smoothing"/>) rather than replacing it - the figure on screen then settles instead of
/// flickering.
/// </para>
/// <para>
/// <b>A fetch that pauses is reported as paused.</b> Aurorium sleeps <c>fetch_interval</c> seconds between
/// passes over its assets, and it gives up on a file that runs past its own two-minute request limit, so
/// "nothing has arrived in the last few minutes" is an ordinary state rather than a failure. It is said
/// plainly, and the estimate goes away with it, because the honest answer is that there is nothing to
/// predict from.
/// </para>
/// </remarks>
public sealed class PatchServerFetchPace {

    /// <summary>
    /// The shortest window a rate is measured over. Below this a sample is a blip: the figure would move
    /// severalfold between two readings.
    /// </summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(15);

    /// <summary>How much of each new window's rate is taken, 0..1. Low enough to settle, high enough to follow.</summary>
    public const double Smoothing = 0.4;

    private DateTimeOffset? _lastAt;
    private long? _lastBytes;
    private double? _rate;
    private bool _stalled;
    private string? _revision;

    /// <summary>The rate it has settled on, or <c>null</c> while it has not measured one yet.</summary>
    public double? BytesPerSecond => _rate;

    /// <summary>Whether the last completed window carried nothing.</summary>
    public bool IsStalled => _stalled;

    /// <summary>
    /// Takes one measurement and says what it makes of the run so far.
    /// </summary>
    /// <param name="state">What the disk says now.</param>
    /// <param name="at">When it was read, which is what the rate is measured against.</param>
    public PatchServerFetchEstimate Observe(PatchServerFetchState state, DateTimeOffset at) {
        // A different revision is a different fetch, and a rate measured over the old one has nothing to do
        // with it. Compared by name rather than by folder so a changed data folder counts as a new fetch too.
        if (_revision is not null && !string.Equals(_revision, state.Revision, StringComparison.OrdinalIgnoreCase)) {
            Reset();
        }

        _revision = state.Revision;

        var bytes = state.FetchedBytes;

        if (_lastAt is { } lastAt && _lastBytes is { } lastBytes) {
            var elapsed = at - lastAt;
            var moved = bytes - lastBytes;

            if (moved > 0) {
                // Bytes have arrived since the last reading, so whatever it was waiting for is over.
                _stalled = false;
            }

            if (elapsed >= MinimumWindow) {
                if (moved > 0) {
                    var window = moved / elapsed.TotalSeconds;
                    _rate = _rate is { } rate ? rate + ((window - rate) * Smoothing) : window;
                }
                else {
                    // Nothing in a whole window: not a slow fetch but a stopped one, for now.
                    _stalled = true;
                }

                _lastAt = at;
                _lastBytes = bytes;
            }
        }
        else {
            _lastAt = at;
            _lastBytes = bytes;
        }

        if (state.IsComplete) {
            _rate = null;
            _stalled = false;
            return new PatchServerFetchEstimate(0, null, TimeSpan.Zero, at, false);
        }

        if (_stalled) {
            return new PatchServerFetchEstimate(state.MissingBytes, _rate, null, null, true);
        }

        var remaining = _rate is { } settled && settled > 0
            ? TimeSpan.FromSeconds(state.MissingBytes / settled)
            : (TimeSpan?)null;

        return new PatchServerFetchEstimate(
            state.MissingBytes,
            _rate,
            remaining,
            remaining is { } left ? at + left : null,
            false);
    }

    /// <summary>Forgets everything, for a fetch that is over or a revision that has changed.</summary>
    public void Reset() {
        _lastAt = null;
        _lastBytes = null;
        _rate = null;
        _stalled = false;
        _revision = null;
    }
}

/// <summary>
/// How much of a revision a patch server has fetched, read off the disk the patch server is writing to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here asks the patch server anything.</b> Aurorium's HTTP API exposes the revisions it holds
/// and the files themselves, and nothing about a fetch in progress - its progress bars are drawn on its own
/// console. What it does have is the revision's own <c>LatestFileList.xml</c>, written before the first
/// asset is fetched, and a folder of the files it has managed to write. Counting one against the other is
/// the whole method, and it is a read of files rather than a conversation, so a launcher watching a fetch
/// cannot perturb it.
/// </para>
/// <para>
/// <b>A file is held when its length is the length the revision gives it.</b> Aurorium skips a file by name
/// alone (<c>if save_path.exists()</c>), so a name is enough for it, but a name at the wrong length is
/// another build's file and could not be repaired later - which makes the size the only honest test of
/// whether a file here is <i>this</i> revision's file.
/// </para>
/// <para>
/// <b>Files are also searched for in the patch server's other revisions.</b> Aurorium deduplicates by
/// name, CRC and size: an unchanged asset between two revisions is served out of whichever revision first
/// introduced it, and is never written into the newer one's folder. Counting only the newest folder would
/// therefore report a revision as missing thousands of files it has been serving all along. The other
/// folders are read only when something is missing from the revision's own folder, so a revision that is
/// complete costs one directory walk and no more.
/// </para>
/// <para>
/// <b>It is metadata only, and it is off the UI thread.</b> Thousands of file lengths, no file read, and
/// never on the caller's thread when called from the view model - which is what makes it safe to do while
/// the patch server is mid-download.
/// </para>
/// </remarks>
public static class PatchServerFetch {

    /// <summary>
    /// What Aurorium names a download that is still in flight: the file's own name and this.
    /// </summary>
    /// <remarks>
    /// Its bytes are counted as progress but the file is not counted as held, which is the difference
    /// between "how far along is it" and "what can it serve". A preload's own staging name
    /// (<see cref="PatchServerPreload.PartSuffix"/>) is neither: it is a local copy being written, and it is
    /// already counted, or about to be, by the file it is on its way to becoming.
    /// </remarks>
    public const string DownloadSuffix = ".part";

    /// <summary>
    /// The folder a patch server keeps a revision in, or empty when either half of that is missing.
    /// </summary>
    /// <remarks>
    /// The same folder a copy into the patch server is planned against, and read the same way - from the
    /// patch server's own config, so a server that keeps its revisions somewhere else is measured where it
    /// actually keeps them rather than where Aurorium's default says.
    /// </remarks>
    public static string FolderFor(string? patchServerFolder, string? revisionName)
        => PatchServerPreload.RevisionFolderFor(patchServerFolder, revisionName);

    /// <summary>
    /// Counts a revision against what is on disk, or returns <c>null</c> when there is nothing to count.
    /// </summary>
    /// <remarks>
    /// Never throws, and never guesses. A patch server that has not started has no revision folder, and one
    /// that is mid-write leaves half an XML document behind; both come back as <c>null</c>, which the caller
    /// reads as "no measurement" rather than as "nothing fetched" - the two are not the same thing, and only
    /// one of them is worth putting on screen.
    /// </remarks>
    /// <param name="patchServerFolder">The folder the patch server is installed in.</param>
    /// <param name="revisionName">The revision's own name, e.g. <c>V_r806919.Wizard_1_610</c>.</param>
    public static PatchServerFetchState? Measure(string? patchServerFolder, string? revisionName) {
        var revision = (revisionName ?? string.Empty).Trim();
        var folder = FolderFor(patchServerFolder, revision);

        if (folder.Length == 0) {
            return null;
        }

        var entries = PatchServerManifest.TryRead(folder);

        if (entries is null || entries.Count == 0) {
            return null;
        }

        var own = Index(folder);
        Dictionary<string, HashSet<long>>? elsewhere = null;

        var totalBytes = 0L;
        var heldBytes = 0L;
        var heldFiles = 0;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries) {
            totalBytes += entry.Size;
            sizes[entry.RelativePath] = entry.Size;

            if (IsHeld(own, entry)) {
                heldFiles++;
                heldBytes += entry.Size;
                continue;
            }

            // Nothing of this name in the revision's own folder, so the file may be a duplicate of one an
            // earlier revision introduced - which is where the patch server serves it from.
            elsewhere ??= IndexSiblings(Path.GetDirectoryName(folder) ?? string.Empty, folder);

            if (IsHeld(elsewhere, entry)) {
                heldFiles++;
                heldBytes += entry.Size;
            }
        }

        // Named against the file list that was just read, so what it is downloading can be reported with the
        // size the revision gives that file rather than with the size of whatever has arrived so far.
        var inFlight = DownloadsInFlight(folder, sizes);
        var partialBytes = inFlight.Sum(file => file.BytesSoFar);

        return new PatchServerFetchState(
            revision, folder, entries.Count, totalBytes, heldFiles, heldBytes, partialBytes, inFlight);
    }

    /// <summary>
    /// What the fetch is doing, in one sentence, for the line under the patch server's own state.
    /// </summary>
    /// <param name="state">What the disk says.</param>
    /// <param name="estimate">What the pace makes of it, or <c>null</c> for a measurement with no history yet.</param>
    /// <param name="serverRevision">
    /// The revision the server asks the patch server for, or <c>null</c> when there is nothing to compare
    /// against. Given rather than read here, because it is a fact about the server's config and this method
    /// is about the disk.
    /// </param>
    public static string Describe(
        PatchServerFetchState state, PatchServerFetchEstimate? estimate = null, string? serverRevision = null) {

        var size = ServerInstallService.FormatBytes(state.TotalBytes);
        var mismatch = DescribeRevisionMismatch(state, serverRevision);

        if (state.IsComplete) {
            return $"{state.Revision} is fully fetched: {state.TotalFiles:N0} files, {size}. The patch "
                   + "server has nothing left to download."
                   + (mismatch.Length > 0 ? $" {mismatch}" : string.Empty);
        }

        var head = $"Fetching {state.Revision}: {state.HeldFiles:N0} of {state.TotalFiles:N0} files — "
                   + $"{ServerInstallService.FormatBytes(state.FetchedBytes)} of {size}.";

        var current = DescribeCurrentFile(state);

        // The revision first when it is the wrong one: what is about to be reported as a countdown is then
        // a download of files Imlight has not asked for, which is the one thing on this line worth acting on.
        var clauses = new List<string>(3);

        if (mismatch.Length > 0) {
            clauses.Add(mismatch);
        }

        if (current.Length > 0) {
            clauses.Add(current);
        }

        // A fetch that has stopped moving is said as what it is rather than as a countdown nobody should
        // believe: the launcher can see that nothing has landed, and cannot see why.
        clauses.Add(estimate is { Stalled: true } ? StalledMessage : DescribeRemaining(state, estimate));

        return $"{head} {string.Join(" ", clauses)}";
    }

    /// <summary>
    /// The file the patch server is writing into right now, and how far into it it is, or empty when none
    /// is in flight.
    /// </summary>
    /// <remarks>
    /// The answer to the one question a percentage cannot answer: a revision is thousands of files and one
    /// of them can be a gigabyte, so "which file, and how big is it" is what makes a figure that is not
    /// moving tell somebody whether to wait. Several files are in flight at once, so the count comes with
    /// it - the leading name is the one being written to now, not the only one being downloaded.
    /// </remarks>
    public static string DescribeCurrentFile(PatchServerFetchState state) {
        if (state.CurrentFile is not { } file) {
            return string.Empty;
        }

        var alongside = state.FilesInFlight > 1
            ? $" ({state.FilesInFlight} files in flight)"
            : string.Empty;

        return $"Downloading {file.Describe()} now{alongside}.";
    }

    /// <summary>
    /// The sentence said when the revision the patch server is fetching is not the one the server asks for,
    /// or empty when the two agree or there is nothing to compare.
    /// </summary>
    /// <remarks>
    /// This is the mismatch that used to be discovered instead of reported: Imlight asks the patch server
    /// for <c>V_{GameRevision}</c>, the patch server holds whatever revision Wizard101 was serving when it
    /// last looked, and a server pinned to a build in between gets a <c>404</c> for its own <c>Root.wad</c>
    /// and then sits at "warming up" forever with nothing on screen to say why. Both revisions are named,
    /// because which one is behind is the whole of what to do about it.
    /// </remarks>
    public static string DescribeRevisionMismatch(PatchServerFetchState state, string? serverRevision) {
        var wanted = (serverRevision ?? string.Empty).Trim();

        // Compared the way a revision folder is compared with a game revision everywhere else, so the
        // folder's own V_ prefix and the config's spelling of the same value cannot make this look like a
        // mismatch when it is not one.
        if (wanted.Length == 0 || PatchServerPreload.IsTheBuild(state.Revision, wanted)) {
            return string.Empty;
        }

        return $"The patch server is fetching {state.Revision} and your server asks it for {wanted}, so "
               + "Imlight gets a 404 for its own files until the two agree: the launcher sets GameRevision to "
               + "the patch server's revision while the sync is on, and a patch server can only ever fetch "
               + "what Wizard101 is serving.";
    }

    /// <summary>What is left, and when it should be done - the half of the sentence that is a prediction.</summary>
    public static string DescribeRemaining(
        PatchServerFetchState state, PatchServerFetchEstimate? estimate = null) {

        var missing = state.MissingBytes > 0
            ? $"{ServerInstallService.FormatBytes(state.MissingBytes)} to go"
            : $"{state.MissingFiles:N0} files to go";

        if (estimate is null || estimate.Remaining is not { } remaining) {
            return state.MissingBytes <= 0
                ? $"{missing}."
                : $"{missing}, with no estimate of how long that will take yet.";
        }

        if (remaining <= TimeSpan.Zero) {
            return $"{missing}.";
        }

        var when = estimate.FinishesAt is { } finishesAt
            ? $" (about {finishesAt.LocalDateTime:t} at this rate)"
            : string.Empty;

        return $"{missing}, about {DescribeDuration(remaining)} at the rate it is going{when}.";
    }

    /// <summary>
    /// What the fetching looks like when it has stopped making progress, said plainly rather than as a
    /// countdown that never arrives.
    /// </summary>
    public const string StalledMessage =
        "Nothing has arrived in the last few minutes: the patch server retries what it could not get, and "
        + "that can take a while between attempts.";

    /// <summary>A span of time in words, rounded the way somebody would say it out loud.</summary>
    /// <remarks>
    /// Written as comparisons rather than as a switch, because a pattern needs constants and these are
    /// spans: an estimate of a first fetch is hours, and one that has been going for a while is minutes, so
    /// the units change with it rather than the figure growing to 1,283 minutes.
    /// </remarks>
    public static string DescribeDuration(TimeSpan span) {
        if (span < TimeSpan.FromMinutes(1)) {
            return "less than a minute";
        }

        if (span < TimeSpan.FromMinutes(90)) {
            return $"{Math.Max(1, Math.Round(span.TotalMinutes))} minutes";
        }

        return span < TimeSpan.FromHours(36)
            ? $"{Math.Max(1, Math.Round(span.TotalHours))} hours"
            : $"{Math.Max(1, Math.Round(span.TotalDays))} days";
    }

    // ---------------------------------------------------------------- reading the disk

    /// <summary>
    /// Every file under a folder, by its path relative to that folder, with the lengths seen for it.
    /// </summary>
    /// <remarks>
    /// A set of lengths rather than one, because the same relative path can exist in two revision folders at
    /// two different sizes, and the question being asked - "is there a file of this name at this length" -
    /// is answered by one of them being the length the revision asks for.
    /// </remarks>
    private static Dictionary<string, HashSet<long>> Index(string folder) {
        var index = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

        if (folder.Length == 0 || !Directory.Exists(folder)) {
            return index;
        }

        try {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) {
                var name = Path.GetFileName(path);

                // A file the patch server is still writing is progress, not something it can serve, so it is
                // not indexed as held - and a preload's staging file is nothing at all until it is moved in.
                if (name.EndsWith(DownloadSuffix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (LengthOf(path) is not { } length) {
                    continue;
                }

                var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');

                if (!index.TryGetValue(relative, out var lengths)) {
                    lengths = [];
                    index[relative] = lengths;
                }

                lengths.Add(length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A folder that cannot be walked is one nothing can be claimed about, and a partial index would
            // be worse than none: what is missing from it would read as missing from the patch server.
            return [];
        }

        return index;
    }

    /// <summary>
    /// Every file in the revision folders beside this one, each under the path it has <i>inside its own
    /// revision</i> - which is the only path either of them is asked for by.
    /// </summary>
    /// <remarks>
    /// Folder by folder rather than one walk of the data folder, because a revision's own name is the first
    /// segment of a path taken from the data folder and is not part of the name a file is served under.
    /// </remarks>
    private static Dictionary<string, HashSet<long>> IndexSiblings(string dataFolder, string excluding) {
        var index = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

        if (dataFolder.Length == 0 || !Directory.Exists(dataFolder)) {
            return index;
        }

        try {
            foreach (var folder in Directory.EnumerateDirectories(dataFolder)) {
                if (string.Equals(folder, excluding, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                foreach (var (path, lengths) in Index(folder)) {
                    if (!index.TryGetValue(path, out var seen)) {
                        seen = [];
                        index[path] = seen;
                    }

                    seen.UnionWith(lengths);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }

        return index;
    }

    /// <summary>Whether the file list's entry for a file matches one that is on disk.</summary>
    private static bool IsHeld(
        Dictionary<string, HashSet<long>> index, PatchServerManifestEntry entry)
        => index.TryGetValue(entry.RelativePath, out var lengths) && lengths.Contains(entry.Size);

    /// <summary>
    /// The files the patch server is part-way through, newest first, from the <c>.part</c> files it leaves
    /// while one is being written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second walk of the revision's own folder rather than a figure taken out of the index above, which
    /// deliberately skips these: a half-written file is a fact about how far along the fetch is and no fact
    /// at all about what it can serve, and the two are counted apart for exactly that reason.
    /// </para>
    /// <para>
    /// Newest first, because that is the one being written now: Aurorium streams a file as it arrives, so
    /// the <c>.part</c> touched most recently is the one its next chunk goes into. The order is the answer to
    /// "what is it downloading" and never a claim that only one thing is - a machine with four downloads at
    /// once has four of these here.
    /// </para>
    /// </remarks>
    /// <param name="revisionFolder">The folder the revision's files are written into.</param>
    /// <param name="sizes">
    /// What the revision's own file list says each file weighs, by the path it names them with, so a file's
    /// size is the revision's answer rather than a guess. A name that is not in it - a file list that changed
    /// under the walk, or something else writing a <c>.part</c> file - is reported with no size.
    /// </param>
    private static IReadOnlyList<PatchServerDownloadInFlight> DownloadsInFlight(
        string revisionFolder, Dictionary<string, long> sizes) {

        if (revisionFolder.Length == 0 || !Directory.Exists(revisionFolder)) {
            return [];
        }

        var inFlight = new List<(PatchServerDownloadInFlight File, DateTime Written)>();

        try {
            foreach (var path in Directory.EnumerateFiles(revisionFolder, "*", SearchOption.AllDirectories)) {
                if (!Path.GetFileName(path).EndsWith(DownloadSuffix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // The file it will become: the <c>.part</c> name with Aurorium's suffix taken off, spelled the
                // way the file list spells it.
                var relative = Path.GetRelativePath(revisionFolder, path)
                    .Replace('\\', '/')
                    [..^DownloadSuffix.Length];

                var written = WrittenAt(path);
                var bytes = LengthOf(path) ?? 0;

                inFlight.Add((new PatchServerDownloadInFlight(
                    relative, bytes, sizes.GetValueOrDefault(relative)), written));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // What was found before the walk gave up is still true, and a fetch's own folder is not one it
            // refuses to read for long.
        }

        return [.. inFlight
            .OrderByDescending(file => file.Written)
            .Select(file => file.File)];
    }

    /// <summary>When a file was last written, or the smallest moment there is for one that cannot be read.</summary>
    private static DateTime WrittenAt(string path) {
        try {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception) {
            // Unreadable is not "written at the beginning of time" as a fact, but it is the answer that puts
            // it last rather than first - and a file whose age cannot be read is not the one being written.
            return DateTime.MinValue;
        }
    }

    /// <summary>A file's length, or <c>null</c> for one that cannot be measured.</summary>
    private static long? LengthOf(string path) {
        try {
            return new FileInfo(path).Length;
        }
        catch (Exception) {
            // Unreadable is not the same as absent, but neither is it a file this revision has.
            return null;
        }
    }
}
