using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Filling a patch server's revision from a Wizard101 install that already holds it: what the file list
/// is read as, what a preload would do, and what it does when it is run.
/// </summary>
/// <remarks>
/// The whole point of this is that it is exact - the files copied in are the revision's own files, because
/// the build they come from is the revision the patch server is holding. So the tests are mostly about the
/// two things that make it not exact: a file of the right name at the wrong length, and a build whose
/// revision does not match the folder it would be copied into.
/// </remarks>
public class PatchServerPreloadTests {

    private const string Revision = "r806919.Wizard_1_610";
    private const string RevisionFolderName = "V_r806919.Wizard_1_610";

    // ---------------------------------------------------------------- the file list

    [Fact]
    public void TheFileListIsReadAsPathsAndSizes() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("revision");
        File.WriteAllText(PatchServerManifest.PathFor(folder), FileList(
            ("Data/GameData/Root.wad", 295405277),
            ("Bin/WizardGraphicalClient.exe", 56433288)));

        var entries = PatchServerManifest.TryRead(folder);

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Data/GameData/Root.wad", entries[0].RelativePath);
        Assert.Equal(295405277, entries[0].Size);
        Assert.Equal("Bin/WizardGraphicalClient.exe", entries[1].RelativePath);
    }

    [Fact]
    public void TheTableOfNamesAtTheTopOfTheListIsNotFiles() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("revision");

        // The first table is the list's own index of names, and names a file only by name - no path, no
        // size - so it is not a file to be copied anywhere.
        File.WriteAllText(PatchServerManifest.PathFor(folder), """
            <?xml version="1.0" ?>
            <LatestFileList>
            <_TableList>
              <RECORD>
                <Name TYPE="STR">About</Name>
              </RECORD>
            </_TableList>
            <About>
              <RECORD>
                <SrcFileName TYPE="STR">Data/GameData/About.wad</SrcFileName>
                <FileType TYPE="UINT">3</FileType>
                <Size TYPE="UINT">1234</Size>
                <CRC TYPE="UINT">99</CRC>
              </RECORD>
            </About>
            </LatestFileList>
            """);

        var entries = PatchServerManifest.TryRead(folder);

        var only = Assert.Single(entries!);
        Assert.Equal("Data/GameData/About.wad", only.RelativePath);
        Assert.Equal(1234, only.Size);
    }

    [Fact]
    public void AFileListThatIsNotThereOrNotAFileListIsNoAnswer() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("revision");

        // Not fetched yet.
        Assert.Null(PatchServerManifest.TryRead(folder));
        Assert.Null(PatchServerManifest.TryRead(null));

        // Half-written, which is what reading one while the patch server writes it looks like.
        File.WriteAllText(PatchServerManifest.PathFor(folder), "<LatestFileList><About><RECORD>");
        Assert.Null(PatchServerManifest.TryRead(folder));
    }

    // ---------------------------------------------------------------- which folder, which build

    [Fact]
    public void ARevisionFolderIsTheDataFolderAndTheRevisionsOwnName() {
        using var workspace = new TempWorkspace();
        var patchServer = workspace.CreateDirectory("patchserver");

        // No config yet: the folder Aurorium would use by default.
        Assert.Equal(
            Path.Combine(patchServer, "data", RevisionFolderName),
            PatchServerPreload.RevisionFolderFor(patchServer, Revision));

        // A config that keeps its revisions somewhere else is the one that says where they are.
        File.WriteAllText(PatchServerConfig.PathFor(patchServer),
            "[fetcher]\nsave_directory = \"D:/Aurorium\"\n");
        Assert.Equal(
            Path.Combine("D:/Aurorium", RevisionFolderName),
            PatchServerPreload.RevisionFolderFor(patchServer, Revision));

        Assert.Equal(string.Empty, PatchServerPreload.RevisionFolderFor(patchServer, null));
        Assert.Equal(string.Empty, PatchServerPreload.RevisionFolderFor(null, Revision));
    }

    [Fact]
    public void ABuildIsTheOneTheFolderIsNamedAfter() {
        Assert.True(PatchServerPreload.IsTheBuild(RevisionFolderName, Revision));
        Assert.True(PatchServerPreload.IsTheBuild(RevisionFolderName, $"V_{Revision}"));
        Assert.True(PatchServerPreload.IsTheBuild("V_R806919.wizard_1_610", Revision));

        Assert.False(PatchServerPreload.IsTheBuild("V_r803238.Wizard_1_610", Revision));
        Assert.False(PatchServerPreload.IsTheBuild(RevisionFolderName, null));
        Assert.False(PatchServerPreload.IsTheBuild(null, Revision));
    }

    // ---------------------------------------------------------------- the plan

    [Fact]
    public void NothingToCopyFromIsRefusedRatherThanCopied() {
        using var workspace = new TempWorkspace();
        var patchServer = workspace.CreateDirectory("patchserver");

        var plan = PatchServerPreload.Plan(workspace.Path("nowhere"), patchServer, Revision, Revision);

        Assert.False(plan.IsPossible);
        Assert.Equal(PatchServerPreload.NoSourceFolderMessage, plan.Refusal);
        Assert.False(plan.IsWorthDoing);
    }

    [Fact]
    public void APatchServerThatHasNotFetchedTheRevisionHasNothingToFillIn() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/About.wad", 10));
        var patchServer = workspace.CreateDirectory("patchserver");

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        Assert.False(plan.IsPossible);
        Assert.Contains(Revision, plan.Refusal);
        Assert.Contains("has not fetched", plan.Refusal);
    }

    [Fact]
    public void EveryFileThePatchServerIsMissingAndTheBuildHasIsCopiedAndNothingElse() {
        using var workspace = new TempWorkspace();

        // The build: the two files the revision wants, and a log of its own that the revision does not name
        // at all - a real install is full of those.
        var source = GameBuild(workspace, "game",
            ("Data/GameData/Held.wad", 10),
            ("Data/GameData/Needed.wad", 20),
            ("bin/SomeClientsOwnLog.log", 30));

        var patchServer = workspace.CreateDirectory("patchserver");

        // The patch server: one of them already, one at another size, one missing from the build.
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);
        Directory.CreateDirectory(Path.Combine(revisionFolder, "Data", "GameData"));
        File.WriteAllBytes(Path.Combine(revisionFolder, "Data", "GameData", "Held.wad"), new byte[10]);
        // Present, but not at the length the revision says - the patch server's copy of something else.
        File.WriteAllBytes(Path.Combine(revisionFolder, "Data", "GameData", "There.wad"), new byte[5]);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(
            ("Data/GameData/Held.wad", 10),
            ("Data/GameData/Needed.wad", 20),
            ("Data/GameData/There.wad", 7),
            ("Data/GameData/Absent.wad", 40)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        Assert.True(plan.IsPossible);
        Assert.Equal(4, plan.RevisionFiles);
        Assert.Equal(1, plan.AlreadyHeld);
        Assert.Equal(1, plan.HeldAtAnotherSize);
        Assert.Equal(1, plan.NotInTheBuild);
        Assert.Equal(0, plan.AtAnotherSizeInTheBuild);

        // The one file that is both wanted and missing, at the length the revision says. The log the build
        // has and the revision does not name is not a file the patch server is asked for, so it is neither
        // copied nor counted - the build keeps it.
        Assert.Equal("Data/GameData/Needed.wad", Assert.Single(plan.Files).RelativePath);
        Assert.Equal(20, plan.BytesToCopy);
        Assert.True(plan.IsWorthDoing);
    }

    [Fact]
    public void OneFileAtTheWrongLengthTakesTheWholeCopyWithIt() {
        using var workspace = new TempWorkspace();

        // The build is nearly the revision - one file of it is a different length, which is what a game that
        // has patched itself past this revision looks like from here.
        var source = GameBuild(workspace, "game",
            ("Data/GameData/Needed.wad", 20),
            ("Data/GameData/Other.wad", 30));

        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(
            ("Data/GameData/Needed.wad", 20),
            ("Data/GameData/Other.wad", 99)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        // Nothing at all is copied, and the sentence says which kind of thing was found rather than leaving
        // the user to work it out from a count of zero.
        Assert.False(plan.IsPossible);
        Assert.False(plan.IsWorthDoing);
        Assert.Empty(plan.Files);
        Assert.Equal(1, plan.AtAnotherSizeInTheBuild);
        Assert.Contains("Data/GameData/Other.wad", plan.Refusal);
        Assert.Contains("different length", plan.Refusal);
        Assert.Contains("never repair", plan.Refusal);
    }

    [Fact]
    public void AClientThatIsAnotherRevisionIsNotHandedOverAtAll() {
        using var workspace = new TempWorkspace();

        // The server asks for the revision this test is about; the install here is the one before it, which
        // is the ordinary state of a machine whose game has not been patched up to this server yet.
        var source = GameBuild(workspace, "game", "r803238.Wizard_1_610", ("Data/GameData/Needed.wad", 20));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName),
            FileList(("Data/GameData/Needed.wad", 20)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, "r803238.Wizard_1_610");

        Assert.False(plan.IsPossible);
        Assert.Contains("r803238.Wizard_1_610", plan.Refusal);
        Assert.Contains(Revision, plan.Refusal);
        Assert.Contains("same revision", plan.Refusal);
    }

    [Fact]
    public void AServerWithNoRevisionToAskForHasNothingToFillIn() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/Needed.wad", 20));

        // Nothing says which revision the server wants - a launcher with no server config - so there is no
        // folder this could ever be about, whatever is on this machine.
        var plan = PatchServerPreload.Plan(source, workspace.Path("patchserver"), string.Empty, Revision);

        Assert.False(plan.IsPossible);
        Assert.Equal(PatchServerPreload.NoRevisionMessage, plan.Refusal);
    }

    [Fact]
    public void APatchServerWithTheWholeRevisionHasNothingWorthDoing() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/Held.wad", 10));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        Directory.CreateDirectory(Path.Combine(revisionFolder, "Data", "GameData"));
        File.WriteAllBytes(Path.Combine(revisionFolder, "Data", "GameData", "Held.wad"), new byte[10]);
        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(("Data/GameData/Held.wad", 10)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        Assert.True(plan.IsPossible);
        Assert.Empty(plan.Files);
        Assert.False(plan.IsWorthDoing);
    }

    // ---------------------------------------------------------------- doing it

    [Fact]
    public async Task PreloadingCopiesTheFilesInUnderTheirOwnNames() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game",
            ("Data/GameData/Needed.wad", 20),
            ("Bin/SomeClientFile.exe", 5));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(
            ("Data/GameData/Needed.wad", 20),
            ("Bin/SomeClientFile.exe", 5)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);
        var result = await new PatchServerPreload().RunAsync(plan);

        Assert.True(result.Success);
        Assert.Equal(2, result.FilesCopied);
        Assert.Equal(25, result.BytesCopied);
        Assert.Null(result.Warning);

        Assert.Equal(20, new FileInfo(Path.Combine(revisionFolder, "Data", "GameData", "Needed.wad")).Length);
        Assert.Equal(5, new FileInfo(Path.Combine(revisionFolder, "Bin", "SomeClientFile.exe")).Length);

        // Nothing half-written is left where a whole file belongs.
        Assert.Empty(Directory.GetFiles(revisionFolder, "*" + PatchServerPreload.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreloadingAgainCopiesWhatIsLeftRatherThanEverything() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/Needed.wad", 20));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(("Data/GameData/Needed.wad", 20)));

        var preload = new PatchServerPreload();
        var first = await preload.RunAsync(PatchServerPreload.Plan(source, patchServer, Revision, Revision));
        Assert.Equal(1, first.FilesCopied);

        // The second time there is nothing wrong with the folder, so the plan is empty and it says so.
        var second = PatchServerPreload.Plan(source, patchServer, Revision, Revision);
        Assert.Empty(second.Files);

        var again = await preload.RunAsync(second);
        Assert.True(again.Success);
        Assert.Equal(0, again.FilesCopied);
        Assert.Contains("nothing to copy", again.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFileThatChangedSinceThePlanWasMadeIsLeftToThePatchServer() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/Needed.wad", 20));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(("Data/GameData/Needed.wad", 20)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        // The game patched itself between the plan and the copy: the file is no longer this revision's.
        File.WriteAllBytes(Path.Combine(source, "Data", "GameData", "Needed.wad"), new byte[21]);

        var result = await new PatchServerPreload().RunAsync(plan);

        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(1, result.FilesFailed);
        Assert.Contains("fetch", result.Warning);
        Assert.False(File.Exists(Path.Combine(revisionFolder, "Data", "GameData", "Needed.wad")));
    }

    [Fact]
    public async Task StoppingAPreloadKeepsWhatWasCopiedAndSaysWhereItGotTo() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game",
            ("Data/GameData/One.wad", 10),
            ("Data/GameData/Two.wad", 10));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(
            ("Data/GameData/One.wad", 10),
            ("Data/GameData/Two.wad", 10)));

        var plan = PatchServerPreload.Plan(source, patchServer, Revision, Revision);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await new PatchServerPreload().RunAsync(plan, cancellationToken: cancelled.Token);

        Assert.False(result.Success);
        Assert.Contains("stopped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still missing", result.Message);
        Assert.Empty(Directory.GetFiles(revisionFolder, "*.wad", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreloadingARefusedPlanCopiesNothingAndDoesNotThrow() {
        var result = await new PatchServerPreload().RunAsync(
            PatchServerPreload.Plan(null, null, null, null));

        Assert.False(result.Success);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(PatchServerPreload.NoRevisionMessage, result.Message);
    }

    // ---------------------------------------------------------------- what the launcher says about it

    [Fact]
    public void TheLineAboutTheCacheCountsWhatIsThereAndWhatCouldBeCopied() {
        using var workspace = new TempWorkspace();
        var plan = Plan(workspace);

        var line = MainWindowViewModel.DescribePreload(plan);

        // One of the two files is already held; one can be copied; the revision's third is nowhere here.
        Assert.Contains($"holds 1 of 3 files of {Revision}", line);
        Assert.Contains("1 of the rest", line);
        Assert.Contains("The other 1 are not in this install", line);
        Assert.Equal("", MainWindowViewModel.DescribePreload(null));
        Assert.Equal(PatchServerPreload.NoRevisionMessage, MainWindowViewModel.DescribePreload(
            PatchServerPreload.Plan(null, null, null, null)));

        // A patch server holding everything there is to hold says so rather than offering nothing.
        Assert.Contains("Nothing is missing from it", MainWindowViewModel.DescribePreload(
            plan with { Files = [], AlreadyHeld = 2, NotInTheBuild = 1 }));
    }

    [Fact]
    public void TheInstallToCopyFromIsTheOneThatIsTheRevisionsBuild() {
        using var workspace = new TempWorkspace();

        // Fills the workspace in: a patch server holding the revision, and an install that is its build.
        Plan(workspace);

        var patchServer = workspace.Path("patchserver");
        var ownGame = workspace.Path("game");

        // The launcher's own game folder when it is the revision the server asks for.
        var (resolved, source) = MainWindowViewModel.PlanPreload(patchServer, Revision, null, ownGame, []);
        Assert.True(resolved!.IsPossible);
        Assert.Equal(ownGame, source);

        // An install that was chosen is the one used.
        Assert.Equal(ownGame,
            MainWindowViewModel.PlanPreload(patchServer, Revision, ownGame, null, []).Source);

        // None at all: a refusal to say out loud rather than a crash.
        var (none, noSource) = MainWindowViewModel.PlanPreload(patchServer, Revision, null, null, []);
        Assert.Equal(PatchServerPreload.NoSourceFolderMessage, none!.Refusal);
        Assert.Equal("", noSource);

        // A build of another revision is not a source, however good an install it is - and the reason said
        // out loud is the one that matters: it is another build, not a patch server that has fetched nothing.
        var other = GameBuild(workspace, "other", "r803238.Wizard_1_610", ("Data/GameData/Needed.wad", 20));
        var elsewhere = MainWindowViewModel.PlanPreload(patchServer, Revision, other, null, []);
        Assert.Contains("same revision", elsewhere.Plan!.Refusal);
    }

    [Fact]
    public async Task TheLauncherSaysWhatIsMissingAndCopiesItInWhenAsked() {
        using var workspace = new TempWorkspace();
        var source = GameBuild(workspace, "game", ("Data/GameData/Needed.wad", 20));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName),
            FileList(("Data/GameData/Needed.wad", 20)));

        // The server, because the revision the copy is aimed at is the server's own: this is what says which
        // revision the patch server is being asked for.
        var server = workspace.CreateDirectory("server");
        workspace.WriteFile(Path.Combine("server", "Config", "Imlight.ini"),
            $"[Global Settings]\r\nGameRevision = {Revision}\r\n");

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = source;
        store.Settings.ServerPath = server;
        store.Settings.PatchServerPath = patchServer;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        // The install it would copy from, and what the patch server is missing - the two things the card
        // shows before anything is pressed.
        await launcher.RefreshPreloadPlanAsync();

        Assert.Equal(source, launcher.PreloadSourceInUse);
        Assert.Contains($"holds 0 of 1 files of {Revision}", launcher.PreloadStatus);
        Assert.True(launcher.CanPreloadPatchServerCache);

        await launcher.PreloadPatchServerCacheCommand.ExecuteAsync(null);

        Assert.Equal(20,
            new FileInfo(Path.Combine(revisionFolder, "Data", "GameData", "Needed.wad")).Length);

        // And the card says so afterwards, with nothing left to do.
        Assert.False(launcher.CanPreloadPatchServerCache);
        Assert.Contains("Nothing is missing", launcher.PreloadStatus);

        // With no patch server folder set there is no line at all, rather than one naming a folder that
        // does not exist beside the box that already says the same thing.
        var bare = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("bare.json")), fetchArt: false);

        await bare.RefreshPreloadPlanAsync();

        Assert.Equal(string.Empty, bare.PreloadStatus);
        Assert.False(bare.CanPreloadPatchServerCache);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A patch server holding a revision with two files, one of them already in place, and an install of
    /// the same build holding one of the two it is missing.
    /// </summary>
    private static PatchServerPreloadPlan Plan(TempWorkspace workspace) {
        var source = GameBuild(workspace, "game", ("Data/GameData/Needed.wad", 20));
        var patchServer = workspace.CreateDirectory("patchserver");
        var revisionFolder = PatchServerPreload.RevisionFolderFor(patchServer, Revision);

        Directory.CreateDirectory(Path.Combine(revisionFolder, "Data", "GameData"));
        File.WriteAllBytes(Path.Combine(revisionFolder, "Data", "GameData", "Held.wad"), new byte[10]);

        workspace.WriteFile(Path.Combine(revisionFolder, PatchServerManifest.FileName), FileList(
            ("Data/GameData/Held.wad", 10),
            ("Data/GameData/Needed.wad", 20),
            ("Data/GameData/Absent.wad", 40)));

        return PatchServerPreload.Plan(source, patchServer, Revision, Revision);
    }


    /// <summary>
    /// A game folder holding a client build and the files given, with the revision this test is about. The
    /// root archive is there because that is what makes a folder look like a Wizard101 install.
    /// </summary>
    private static string GameBuild(TempWorkspace workspace, string relative, params (string Path, int Size)[] files)
        => GameBuild(workspace, relative, Revision, files);

    private static string GameBuild(
        TempWorkspace workspace, string relative, string build, params (string Path, int Size)[] files) {
        var folder = workspace.CreateDirectory(relative);

        workspace.WriteFile(Path.Combine(relative, "Bin", "revision.dat"), build + "\n");
        workspace.WriteFile(Path.Combine(relative, "Data", "GameData", "Root.wad"), "root");

        foreach (var (path, size) in files) {
            workspace.WriteFile(Path.Combine(relative, path.Replace('/', Path.DirectorySeparatorChar)), new byte[size]);
        }

        return folder;
    }

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
            <_TableList>
              <RECORD>
                <Name TYPE="STR">About</Name>
              </RECORD>
            </_TableList>
            {string.Join("\n", records)}
            </LatestFileList>
            """;
    }
}
