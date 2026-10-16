using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Watching the patch server fetch: how much of a revision is on disk, how fast the rest is arriving, and
/// what the launcher says about it.
/// </summary>
/// <remarks>
/// The whole of this is read off the disk rather than asked of the patch server, because the patch server
/// does not expose a fetch in progress - its progress bars are drawn on its own console, and its HTTP API
/// serves revisions and files and nothing about a download. So the tests are about the two things that make
/// reading a folder the same question as "how much is left": a file is this revision's only at the length
/// the revision gives it, and a file can be served out of an older revision's folder without ever being
/// written into this one's.
/// </remarks>
public class PatchServerFetchTests {

    private const string Revision = "r806919.Wizard_1_610";
    private const string RevisionFolderName = "V_r806919.Wizard_1_610";

    // ---------------------------------------------------------------- what is on disk

    [Fact]
    public void ARevisionIsCountedAgainstTheFilesThatAreThere() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace,
            ("Data/GameData/Held.wad", 10),
            ("Data/GameData/TooSmall.wad", 20),
            ("Data/GameData/Absent.wad", 40));

        InTheRevision(workspace, "Data/GameData/Held.wad", 10);
        InTheRevision(workspace, "Data/GameData/TooSmall.wad", 7);

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.Equal(RevisionFolderName, state.Revision);
        Assert.Equal(3, state.TotalFiles);
        Assert.Equal(70, state.TotalBytes);

        // One file is there at the right length. The other is there at the wrong one, which is another
        // build's file: the patch server would fetch it, and so it is not counted as held.
        Assert.Equal(1, state.HeldFiles);
        Assert.Equal(10, state.HeldBytes);
        Assert.Equal(2, state.MissingFiles);
        Assert.Equal(60, state.MissingBytes);
        Assert.False(state.IsComplete);
        Assert.Equal(10 / 70.0, state.Fraction, 3);
    }

    [Fact]
    public void AFileInAnotherRevisionIsHeldBecauseThatIsWhereItIsServedFrom() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace, ("Data/GameData/Unchanged.wad", 30));

        // Aurorium deduplicates: an asset that did not change between two revisions is served out of the
        // revision that introduced it and is never written into the newer one's folder. Counting only the
        // newer folder would call a revision thousands of files short that it has been serving all along.
        InTheRevision(workspace, "Data/GameData/Unchanged.wad", 30, "V_r800000.Wizard_1_600");

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.True(state.IsComplete);
        Assert.Equal(1, state.HeldFiles);
        Assert.Equal(0, state.MissingBytes);
    }

    [Fact]
    public void AFileInAnotherRevisionAtTheWrongLengthIsStillMissing() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace, ("Data/GameData/Unchanged.wad", 30));

        InTheRevision(workspace, "Data/GameData/Unchanged.wad", 29, "V_r800000.Wizard_1_600");

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.False(state.IsComplete);
        Assert.Equal(1, state.MissingFiles);
    }

    [Fact]
    public void AFileBeingDownloadedIsProgressButNotSomethingTheServerCanServe() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace, ("Data/GameData/Root.wad", 100));

        // What is there while Aurorium is writing a file: the file's own name and its own suffix, holding
        // as much as has arrived so far.
        InTheRevision(workspace, "Data/GameData/Root.wad" + PatchServerFetch.DownloadSuffix, 40);

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.Equal(0, state.HeldFiles);
        Assert.False(state.IsComplete);

        // Four tenths of the way along, by bytes - which is what makes the figure move while one large file
        // is being written, rather than only when it finishes.
        Assert.Equal(40, state.PartialBytes);
        Assert.Equal(40, state.FetchedBytes);
        Assert.Equal(60, state.MissingBytes);
        Assert.Equal(0.4, state.Percent / 100);
    }

    [Fact]
    public void AFileBeingDownloadedIsNamedAndMeasuredAgainstTheRevision() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace,
            ("Data/GameData/Root.wad", 100),
            ("Data/GameData/Zones.wad", 250));

        // Two downloads at once, which is what Aurorium does: each is its own .part file. The one touched
        // most recently is the one being written into now, which is what the line on screen names.
        InTheRevision(workspace, "Data/GameData/Root.wad" + PatchServerFetch.DownloadSuffix, 40);
        InTheRevision(workspace, "Data/GameData/Zones.wad" + PatchServerFetch.DownloadSuffix, 10);

        var zones = Path.Combine(patchServer, "data", RevisionFolderName,
            "Data", "GameData", "Zones.wad" + PatchServerFetch.DownloadSuffix);
        File.SetLastWriteTimeUtc(zones, DateTime.UtcNow.AddMinutes(1));

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.Equal(2, state.FilesInFlight);
        Assert.Equal(50, state.PartialBytes);

        // Newest first, and the sizes are the revision's own rather than how far each has got: 10 of 250 is
        // a different thing from 10 bytes of something nobody can size.
        var current = state.CurrentFile!;
        Assert.Equal("Data/GameData/Zones.wad", current.RelativePath);
        Assert.Equal("Zones.wad", current.FileName);
        Assert.Equal(10, current.BytesSoFar);
        Assert.Equal(250, current.TotalBytes);
        Assert.True(current.HasASize);
        Assert.Contains("Zones.wad", current.Describe());
        Assert.Contains("of", current.Describe());

        // The other one is in flight too, and is not the file the line names.
        Assert.Equal("Data/GameData/Root.wad", state.InFlight[1].RelativePath);
    }

    [Fact]
    public void APreloadsHalfWrittenFileIsNotProgressAtAll() {
        using var workspace = new TempWorkspace();
        var patchServer = RevisionHolding(workspace, ("Data/GameData/Root.wad", 100));

        // The launcher's own staging name: a local copy on its way in, not a download in flight. It is
        // counted when it is moved into place, and it is not counted before then - the file the revision
        // names is still one the patch server does not have.
        InTheRevision(workspace, "Data/GameData/Root.wad" + PatchServerPreload.PartSuffix, 90);

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.Equal(0, state.HeldFiles);
        Assert.Equal(0, state.PartialBytes);
        Assert.Equal(100, state.MissingBytes);
    }

    [Fact]
    public void NothingIsMeasuredUntilTheRevisionHasAFileListToMeasureAgainst() {
        using var workspace = new TempWorkspace();
        var patchServer = workspace.CreateDirectory("patchserver");

        // No folder, no list, and a list that is half-written as Aurorium writes it: all three are "nothing
        // to say" rather than "nothing fetched", and the two are not the same thing on screen.
        Assert.Null(PatchServerFetch.Measure(patchServer, RevisionFolderName));
        Assert.Null(PatchServerFetch.Measure(null, RevisionFolderName));
        Assert.Null(PatchServerFetch.Measure(patchServer, null));

        workspace.CreateDirectory(Path.Combine("patchserver", "data", RevisionFolderName));
        Assert.Null(PatchServerFetch.Measure(patchServer, RevisionFolderName));

        workspace.WriteFile(
            Path.Combine("patchserver", "data", RevisionFolderName, PatchServerManifest.FileName),
            "<LatestFileList><About><RECORD>");

        Assert.Null(PatchServerFetch.Measure(patchServer, RevisionFolderName));
    }

    [Fact]
    public void TheRevisionIsCountedWhereThePatchServersOwnConfigKeepsIt() {
        using var workspace = new TempWorkspace();
        var patchServer = workspace.CreateDirectory("patchserver");

        // A patch server that keeps its revisions somewhere else is measured there, because that is where
        // it writes them - the same reason a copy into one is planned against that folder.
        workspace.WriteFile("patchserver/config.toml", """
            [server]
            endpoint = "127.0.0.1:12369"

            [fetcher]
            concurrent_downloads = 4
            save_directory = "assets"
            fetch_interval = 300

            [patch]
            host = "patch.us.wizard101.com"
            port = "12500"

            [database]
            path = "aurorium.db"
            """);

        workspace.WriteFile(Path.Combine("patchserver", "assets", RevisionFolderName, PatchServerManifest.FileName),
            FileList(("Data/GameData/Root.wad", 100)));
        workspace.WriteFile(Path.Combine("patchserver", "assets", RevisionFolderName, "Data", "GameData", "Root.wad"),
            new byte[100]);

        var state = PatchServerFetch.Measure(patchServer, RevisionFolderName);

        Assert.NotNull(state);
        Assert.True(state.IsComplete);
    }

    // ---------------------------------------------------------------- how fast it is going

    [Fact]
    public void ARateNeedsAWindowLongEnoughToBeOne() {
        var pace = new PatchServerFetchPace();
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        var first = pace.Observe(Fetched(500), start);

        Assert.Null(first.BytesPerSecond);
        Assert.Null(first.Remaining);
        Assert.False(first.Stalled);

        // Five seconds later, and halfway through the patch server's own minimum: still not a rate. A window
        // this short would turn one slow sample into a wildly wrong estimate.
        var tooSoon = pace.Observe(Fetched(600), start.AddSeconds(5));

        Assert.Null(tooSoon.BytesPerSecond);
        Assert.Null(tooSoon.Remaining);
    }

    [Fact]
    public void TheRateAndTheFinishComeFromWhatHasLandedSinceTheLastLook() {
        var pace = new PatchServerFetchPace();
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        pace.Observe(Fetched(500), start);

        // 100 bytes in 20 seconds is 5 B/s, and 400 are left: 80 seconds, at twenty past.
        var estimate = pace.Observe(Fetched(600), start.AddSeconds(20));

        Assert.Equal(5, estimate.BytesPerSecond!.Value, 3);
        Assert.Equal(TimeSpan.FromSeconds(80), estimate.Remaining!.Value);
        Assert.Equal(400, estimate.MissingBytes);
        Assert.Equal(start.AddSeconds(100), estimate.FinishesAt);
        Assert.False(estimate.Stalled);
    }

    [Fact]
    public void AFetchThatStoppedMovingIsSaidRatherThanCountedDown() {
        var pace = new PatchServerFetchPace();
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        pace.Observe(Fetched(500), start);
        var going = pace.Observe(Fetched(600), start.AddSeconds(20));
        Assert.NotNull(going.Remaining);

        // A whole window with nothing in it. Aurorium gives up on a file that runs past its own two-minute
        // limit and then sleeps a retry interval, so this is an ordinary state - and an estimate that went on
        // counting down through it would be a lie the user could watch not come true.
        var stalled = pace.Observe(Fetched(600), start.AddSeconds(40));

        Assert.True(stalled.Stalled);
        Assert.Null(stalled.Remaining);
        Assert.Null(stalled.FinishesAt);

        // And it comes back the moment something lands, without waiting for another window to close.
        var resumed = pace.Observe(Fetched(700), start.AddSeconds(45));

        Assert.False(resumed.Stalled);
        Assert.NotNull(resumed.Remaining);
    }

    [Fact]
    public void AFinishedRevisionHasNothingLeftToEstimate() {
        var pace = new PatchServerFetchPace();
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        pace.Observe(Fetched(500), start);

        var done = pace.Observe(Fetched(1000, heldFiles: 10), start.AddSeconds(20));

        Assert.Equal(TimeSpan.Zero, done.Remaining);
        Assert.Equal(0, done.MissingBytes);
        Assert.False(done.Stalled);
    }

    [Fact]
    public void ANewRevisionStartsTheEstimateOverRatherThanInheritingOne() {
        var pace = new PatchServerFetchPace();
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        pace.Observe(Fetched(500), start);

        var otherRevision = pace.Observe(
            Fetched(600) with { Revision = "V_r810000.Wizard_1_620" }, start.AddSeconds(20));

        // Nothing is known about a fetch that has only just been seen, whatever was known about the last one.
        Assert.Null(otherRevision.BytesPerSecond);
        Assert.Null(otherRevision.Remaining);
    }

    // ---------------------------------------------------------------- what it says

    [Fact]
    public void ARevisionThatIsHereSaysSoAndSaysThereIsNothingLeftToDownload() {
        var line = PatchServerFetch.Describe(Fetched(1000, heldFiles: 10), new PatchServerFetchEstimate(
            0, null, TimeSpan.Zero, DateTimeOffset.Now, false));

        Assert.Contains(RevisionFolderName, line);
        Assert.Contains("fully fetched", line);
        Assert.Contains("nothing left to download", line);
    }

    [Fact]
    public void AFetchInProgressSaysHowMuchAndHowLong() {
        var start = DateTimeOffset.Parse("2026-10-16T12:00:00+00:00");

        var line = PatchServerFetch.Describe(
            Fetched(600), new PatchServerFetchEstimate(400, 5, TimeSpan.FromMinutes(20), start, false));

        Assert.Contains($"Fetching {RevisionFolderName}", line);
        Assert.Contains("6 of 10 files", line);
        Assert.Contains("to go", line);
        Assert.Contains("20 minutes", line);
        Assert.Contains("at this rate", line);
    }

    [Fact]
    public void AFetchWithNothingToMeasureARateFromSaysExactlyThat() {
        var line = PatchServerFetch.Describe(Fetched(600), new PatchServerFetchEstimate(
            400, null, null, null, false));

        Assert.Contains("to go", line);
        Assert.Contains("no estimate", line);
        Assert.DoesNotContain("at this rate", line);
    }

    [Fact]
    public void AFetchInProgressNamesTheFileItIsOnAndHowBigItIs() {
        var file = new PatchServerDownloadInFlight("Data/GameData/Equipment-WorldData.wad", 12_300_000, 281_000_000);
        var alongside = new PatchServerDownloadInFlight("Data/GameData/Root.wad", 1_000, 281_000_000);

        var line = PatchServerFetch.Describe(
            Fetched(600, inFlight: [file, alongside]),
            new PatchServerFetchEstimate(400, 5, TimeSpan.FromMinutes(20), DateTimeOffset.Now, false),
            Revision);

        Assert.Contains("Fetching V_" + Revision, line);
        Assert.Contains("6 of 10 files", line);

        // Which file, and how big it is - the two things a percentage leaves out.
        Assert.Contains("Equipment-WorldData.wad", line);
        Assert.Contains("of", line);
        Assert.Contains("2 files in flight", line);

        // And where it is up to overall is still said.
        Assert.Contains("to go", line);
        Assert.Contains("20 minutes", line);
    }

    [Fact]
    public void AFetchOfAnotherRevisionThanTheServerWantsSaysWhichTwoDisagree() {
        const string serverWants = "r803238.Wizard_1_610";

        var line = PatchServerFetch.Describe(Fetched(600), null, serverWants);

        // Both revisions are named, because which of the two is behind is the whole of what to do about it.
        Assert.Contains("V_" + Revision, line);
        Assert.Contains(serverWants, line);
        Assert.Contains("404", line);
        Assert.Contains("GameRevision", line);

        // A patch server fetching the revision the server asks for says none of that - and the same value
        // spelled the other way round, or with the folder's own prefix, is not a mismatch.
        Assert.DoesNotContain("404", PatchServerFetch.Describe(Fetched(600), null, Revision));
        Assert.DoesNotContain("404", PatchServerFetch.Describe(Fetched(600), null, "V_" + Revision));
        Assert.Equal(string.Empty, PatchServerFetch.DescribeRevisionMismatch(Fetched(600), Revision));

        // Nothing to compare against is not a mismatch either: a launcher with no server installed has no
        // revision to have got wrong.
        Assert.Equal(string.Empty, PatchServerFetch.DescribeRevisionMismatch(Fetched(600), null));
        Assert.Equal(string.Empty, PatchServerFetch.DescribeRevisionMismatch(Fetched(600), "  "));
    }

    [Fact]
    public void AFetchThatIsNotMovingSaysThatInsteadOfHowLongItWillTake() {
        var line = PatchServerFetch.Describe(Fetched(600), new PatchServerFetchEstimate(
            400, 5, null, null, true));

        Assert.Contains(PatchServerFetch.StalledMessage, line);
        Assert.DoesNotContain("at this rate", line);
    }

    [Fact]
    public void ARevisionThatIsHereSaysSoEvenWhenItIsNotTheOneTheServerWants() {
        // A complete revision is still the wrong revision if the server asks for another one: that is the
        // state a server sits at "warming up" forever in, so the two are said in the same sentence.
        var line = PatchServerFetch.Describe(
            Fetched(1000, heldFiles: 10), new PatchServerFetchEstimate(
                0, null, TimeSpan.Zero, DateTimeOffset.Now, false), "r803238.Wizard_1_610");

        Assert.Contains("fully fetched", line);
        Assert.Contains("r803238.Wizard_1_610", line);
        Assert.Contains("404", line);
    }

    [Theory]
    [InlineData(30, "less than a minute")]
    [InlineData(20 * 60, "20 minutes")]
    [InlineData(3 * 60 * 60, "3 hours")]
    [InlineData(50 * 60 * 60, "2 days")]
    public void ASpanOfTimeIsSaidTheWaySomebodyWouldSayIt(int seconds, string expected)
        => Assert.Equal(expected, PatchServerFetch.DescribeDuration(TimeSpan.FromSeconds(seconds)));

    // ---------------------------------------------------------------- the launcher's own state

    [Fact]
    public void ALauncherWatchingNothingHasNoFetchLineAtAll() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        var folder = workspace.CreateDirectory("patchserver");
        File.WriteAllBytes(PatchServerRunner.ExecutablePathFor(folder), [0]);
        launcher.PatchServerPath = folder;

        // Installed and stopped: there is nothing to watch, because a patch server only fetches while it is
        // running. The row on Home is the process and nothing more, and this line exists to say the one
        // thing the row cannot - so with nothing running there is no line, rather than an empty one.
        Assert.True(launcher.PatchServerInstalled);
        Assert.False(launcher.PatchServerIsRunning);
        Assert.Equal(string.Empty, launcher.FetchStatus);
        Assert.False(launcher.HasFetchStatus);
        Assert.False(launcher.IsFetchingGameFiles);
        Assert.Equal(0, launcher.FetchProgress);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A patch server holding a revision that names the given files, with nothing of it on disk yet.
    /// </summary>
    private static string RevisionHolding(TempWorkspace workspace, params (string Path, int Size)[] files) {
        var patchServer = workspace.CreateDirectory("patchserver");
        workspace.WriteFile(Path.Combine("patchserver", "data", RevisionFolderName, PatchServerManifest.FileName),
            FileList(files));

        return patchServer;
    }

    /// <summary>
    /// Writes a file into a revision folder of the patch server this test set up, at the size given - which
    /// is all a file of a revision ever is to the launcher: a path and a length.
    /// </summary>
    private static void InTheRevision(
        TempWorkspace workspace, string path, int size, string revisionFolder = RevisionFolderName)
        => workspace.WriteFile(
            Path.Combine("patchserver", "data", revisionFolder, path.Replace('/', Path.DirectorySeparatorChar)),
            new byte[size]);

    /// <summary>A measurement with 1,000 bytes of revision in it and the files given as held.</summary>
    private static PatchServerFetchState Fetched(
        long held, int heldFiles = 6, IReadOnlyList<PatchServerDownloadInFlight>? inFlight = null)
        => new("V_" + Revision, "folder", 10, 1000, heldFiles, held, 0, inFlight ?? []);

    /// <summary>A file list in KingsIsle's shape, with the fields that are read and one that is not.</summary>
    private static string FileList(params (string Path, int Size)[] files) {
        var records = files.Select(file => $"""
              <RECORD>
                <SrcFileName TYPE="STR">{file.Path}</SrcFileName>
                <TarFileName TYPE="STR"></TarFileName>
                <FileType TYPE="UINT">3</FileType>
                <Size TYPE="UINT">{file.Size}</Size>
                <CRC TYPE="UINT">0</CRC>
              </RECORD>
            """);

        return $"""
            <?xml version="1.0" ?>
            <LatestFileList>
            {string.Join("\n", records)}
            </LatestFileList>
            """;
    }
}
