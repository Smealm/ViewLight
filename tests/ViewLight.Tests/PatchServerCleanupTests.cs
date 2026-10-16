using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The revisions the patch server has fetched, and what the launcher is allowed to do about them.
/// </summary>
/// <remarks>
/// A revision of this game is around 18 GB, so this is the one part of the launcher that removes something
/// large and cannot put it back: a revision Wizard101 no longer serves cannot be fetched again. The tests
/// are therefore mostly about what is <i>not</i> removed - the revision the server asks for, the newest one
/// the patch server has, and anything it is still writing into.
/// </remarks>
public class PatchServerCleanupTests {

    private const string ServerRevision = "r803238.Wizard_1_610";
    private const string ClientRevision = "r806919.Wizard_1_610";
    private const string OldRevision = "r790000.Wizard_1_600";

    // ---------------------------------------------------------------- what a revision is for

    [Fact]
    public void ARevisionIsInUseWhenTheServerAsksForItOrItIsTheNewest() {
        Assert.Equal(PatchServerRevisionRole.ServerRevision,
            PatchServerRevisionCatalogue.RoleOf(ServerRevision, ServerRevision, ClientRevision));

        Assert.Equal(PatchServerRevisionRole.NewestRevision,
            PatchServerRevisionCatalogue.RoleOf(ClientRevision, ServerRevision, ClientRevision));

        // The server's own revision wins when a machine somehow has both on one folder: a server that cannot
        // find the revision it asks for is a server that cannot serve anybody.
        Assert.Equal(PatchServerRevisionRole.ServerRevision,
            PatchServerRevisionCatalogue.RoleOf(ClientRevision, ClientRevision, ClientRevision));

        Assert.Equal(PatchServerRevisionRole.Unused,
            PatchServerRevisionCatalogue.RoleOf(OldRevision, ServerRevision, ClientRevision));

        // Nothing to compare against: a machine with no server installed has no revision in use, and a
        // machine with one folder has that folder as its newest.
        Assert.Equal(PatchServerRevisionRole.Unused,
            PatchServerRevisionCatalogue.RoleOf(OldRevision, null, null));
        Assert.Equal(PatchServerRevisionRole.NewestRevision,
            PatchServerRevisionCatalogue.RoleOf(ClientRevision, null, ClientRevision));

        // Case and spacing are the same revision, the way they are everywhere else the two are compared.
        Assert.Equal(PatchServerRevisionRole.ServerRevision,
            PatchServerRevisionCatalogue.RoleOf(ServerRevision.ToUpperInvariant(), $" {ServerRevision} ", null));
    }

    [Fact]
    public void AnOlderRevisionTheServerAsksForIsKeptAndAnOlderClientRevisionIsNot() {
        // The sync off: the server is pinned to a build the patch server does not hold and was handed its
        // files by the hand-off, while the patch server went on and fetched the newest one. The server's
        // revision is the one nothing could replace - it cannot be fetched again - so it stays.
        Assert.Equal(PatchServerRevisionRole.ServerRevision,
            PatchServerRevisionCatalogue.RoleOf(ServerRevision, ServerRevision, ClientRevision));

        // A game that has not been patched up to the newest revision is not a reason to keep that revision's
        // folder: the client reads its own files out of its own folder, and what a session asks the patch
        // server for is the revision the server names. This is the rule that changed - the client's revision
        // used to be kept, and keeping it meant a machine holding every build it had ever played on.
        Assert.Equal(PatchServerRevisionRole.Unused,
            PatchServerRevisionCatalogue.RoleOf(ServerRevision, ClientRevision, ClientRevision));
    }

    // ---------------------------------------------------------------- what is on the disk

    [Fact]
    public void TheRevisionsOnDiskAreReadWithTheirSizesAndTheirRoles() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/GameData/Root.wad", 100),
            ("Bin/client.exe", 20));
        Revision(workspace, patchServer, ServerRevision, ("Data/GameData/Root.wad", 50));
        Revision(workspace, patchServer, OldRevision, ("Data/GameData/Old.wad", 7));

        AgeEverything(patchServer);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        Assert.Equal(3, revisions.Count);

        // Newest revision first, whatever order the file system hands them over in - and the newest is the
        // one that is kept, which on this machine is also the one the game is.
        Assert.Equal(ClientRevision, revisions[0].GameRevision);
        Assert.Equal(ServerRevision, revisions[1].GameRevision);
        Assert.Equal(OldRevision, revisions[2].GameRevision);

        Assert.Equal(120, revisions[0].Bytes);
        Assert.Equal(2, revisions[0].Files);
        Assert.Equal(PatchServerRevisionRole.NewestRevision, revisions[0].Role);
        Assert.Equal("The newest the patch server has — kept", revisions[0].RoleText);

        Assert.Equal(PatchServerRevisionRole.ServerRevision, revisions[1].Role);
        Assert.Equal(PatchServerRevisionRole.Unused, revisions[2].Role);
        Assert.Equal("An older revision, which nothing is asking for", revisions[2].RoleText);

        // Only the spare one may be removed.
        var spare = Assert.Single(PatchServerRevisionCatalogue.Spare(revisions));
        Assert.Equal(OldRevision, spare.GameRevision);
        Assert.True(spare.CanBeDeleted);

        // The list says what the three of them add up to, and what is only taking up room.
        var line = MainWindowViewModel.DescribeRevisions(revisions);
        Assert.Contains("holds 3 revisions", line);
        Assert.Contains("1 of them", line);
        Assert.Contains("is not in use", line);

        Assert.Equal(string.Empty, MainWindowViewModel.DescribeRevisions([]));
    }

    [Fact]
    public void OnlyAuroriumsOwnRevisionFoldersAreEverConsidered() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));

        // A folder somebody keeps beside the revisions is theirs, whatever it holds - and a cleanup that
        // walked into it would be deleting something the patch server has nothing to do with.
        workspace.WriteFile("patchserver/data/somebody-elses-folder/notes.txt", "mine");
        workspace.WriteFile("patchserver/data/backups/notes.txt", "mine");

        AgeEverything(patchServer);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        Assert.Equal(OldRevision, Assert.Single(revisions).GameRevision);
    }

    [Fact]
    public void ARevisionBeingFetchedRightNowIsNotSpare() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, OldRevision, ("Data/GameData/Root.wad", 100));

        // What a fetch looks like from outside: a file still arriving under Aurorium's own name.
        workspace.WriteFile("patchserver/data/V_" + OldRevision + "/Data/GameData/Root.wad.part", new byte[40]);

        var revision = Assert.Single(
            PatchServerRevisionCatalogue.List(patchServer, ServerRevision));

        Assert.True(revision.HasADownloadInFlight);
        Assert.False(revision.CanBeDeleted);
        Assert.Equal("Being fetched right now — kept", revision.RoleText);
        Assert.Empty(PatchServerRevisionCatalogue.Spare([revision]));
    }

    [Fact]
    public void ARevisionWrittenToMomentsAgoIsTreatedAsOneInUse() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        // A newer revision is already there, so this one is genuinely an older copy of the game rather than
        // the newest thing on the disk - which is what the recency rule is being tested apart from.
        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));

        // A revision Aurorium has just started on: its file list is written before a single asset is
        // fetched, so it looks exactly like an unused revision for the first moments of its life.
        var folder = Revision(workspace, patchServer, OldRevision, ("LatestFileList.xml", 30));
        File.SetLastWriteTimeUtc(
            System.IO.Path.Combine(folder, "LatestFileList.xml"), DateTime.UtcNow);
        AgeEverything(patchServer);
        File.SetLastWriteTimeUtc(
            System.IO.Path.Combine(folder, "LatestFileList.xml"), DateTime.UtcNow);

        var revision = Assert.Single(
            PatchServerRevisionCatalogue.List(patchServer, ServerRevision),
            found => found.GameRevision == OldRevision);

        Assert.False(revision.CanBeDeleted);

        // ... and the same revision, an hour later, is spare.
        File.SetLastWriteTimeUtc(
            System.IO.Path.Combine(folder, "LatestFileList.xml"), DateTime.UtcNow.AddHours(-1));

        var later = Assert.Single(
            PatchServerRevisionCatalogue.List(patchServer, ServerRevision),
            found => found.GameRevision == OldRevision);

        Assert.True(later.CanBeDeleted);
    }

    [Fact]
    public void APatchServerWithNoFolderOrNoDataHasNothingToSay() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Assert.Empty(PatchServerRevisionCatalogue.List(null, ServerRevision));
        Assert.Empty(PatchServerRevisionCatalogue.List(string.Empty, ServerRevision));
        Assert.Empty(PatchServerRevisionCatalogue.List(patchServer, ServerRevision));
    }

    [Fact]
    public void TheDataFolderIsTheOneThePatchServersOwnConfigNames() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        // Somewhere else entirely, which is where the revisions are and therefore the only place anything
        // may be removed from. Relative to the patch server's folder, the way Aurorium reads it.
        File.WriteAllText(PatchServerConfig.PathFor(patchServer), "[fetcher]\nsave_directory = \"elsewhere\"\n");
        var dataElsewhere = workspace.Path("patchserver/elsewhere");
        Directory.CreateDirectory(System.IO.Path.Combine(dataElsewhere, "V_" + OldRevision));
        File.WriteAllBytes(
            System.IO.Path.Combine(dataElsewhere, "V_" + OldRevision, "Old.wad"), new byte[9]);

        // The default data folder is not read at all while the config names another one.
        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        var only = Assert.Single(revisions);
        Assert.Equal(OldRevision, only.GameRevision);
        Assert.Equal(9, only.Bytes);
    }

    // ---------------------------------------------------------------- removing them

    [Fact]
    public void AutomaticCleanupRemovesTheSpareRevisionAndNothingElse() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        Revision(workspace, patchServer, ServerRevision, ("Data/Root.wad", 50));
        var old = Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));

        AgeEverything(patchServer);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);
        var result = PatchServerRevisionCatalogue.Delete(PatchServerRevisionCatalogue.Spare(revisions));

        Assert.True(result.RemovedAnything);
        Assert.Equal(7, result.BytesFreed);
        Assert.Equal(1, result.FilesFreed);
        Assert.Equal(OldRevision, Assert.Single(result.Removed).GameRevision);
        Assert.Empty(result.Kept);

        Assert.False(Directory.Exists(old));
        Assert.Equal(2, PatchServerRevisionCatalogue.List(patchServer, ServerRevision).Count);

        Assert.Contains("Removed 1 unused revision", result.Describe());
        Assert.Contains("7 B", result.Describe());
    }

    [Fact]
    public void AutomaticCleanupRefusesAnythingThatIsInUse() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        Revision(workspace, patchServer, ServerRevision, ("Data/Root.wad", 50));

        AgeEverything(patchServer);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        // Handed the two that are in use anyway - which is what "nothing is allowed to be wrong about this"
        // means: the rule is applied to what it is given rather than to what it was told.
        var result = PatchServerRevisionCatalogue.Delete(revisions);

        Assert.False(result.RemovedAnything);
        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(2, PatchServerRevisionCatalogue.List(patchServer, ServerRevision).Count);

        Assert.Contains("Nothing was removed", result.Describe());
        Assert.Contains("The newest the patch server has — kept", result.Describe());
    }

    [Fact]
    public void AutomaticCleanupRefusesARevisionThePatchServerIsWritingInto() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        // A newer revision is already there, so the one below is an older copy of the game rather than the
        // newest thing on the disk - which is the only way it could be spare in the first place.
        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));

        var old = Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));
        workspace.WriteFile($"patchserver/data/V_{OldRevision}/Data/Old.wad.part", new byte[3]);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        // Handed it anyway - which is the case the guard is for: a fetch that starts between the look and the
        // removal is not something the caller can know about.
        var result = PatchServerRevisionCatalogue.Delete(revisions);

        Assert.False(result.RemovedAnything);
        Assert.True(Directory.Exists(old));
        Assert.Contains("writing into it", result.Describe());
    }

    [Fact]
    public void TheManualCleanupRemovesWhatTheUserMarkedAndStillRefusesARevisionBeingWrittenInto() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        var server = Revision(workspace, patchServer, ServerRevision, ("Data/Root.wad", 50));
        var old = Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));
        var fetching = Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        workspace.WriteFile($"patchserver/data/V_{ClientRevision}/Data/Root.wad.part", new byte[3]);

        AgeEverything(patchServer);
        // The one being fetched, written to now rather than aged with everything else.
        File.SetLastWriteTimeUtc(
            System.IO.Path.Combine(fetching, "Data", "Root.wad.part"), DateTime.UtcNow);

        var revisions = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);

        // The user's own decision is taken as given, so the revision the server asks for goes if they say so
        // - and the one the patch server is downloading right now does not, whatever they say.
        var result = PatchServerRevisionCatalogue.Delete(revisions, onlySpare: false);

        Assert.Equal(2, result.Removed.Count);
        Assert.Contains(result.Removed, removed => removed.GameRevision == ServerRevision);
        Assert.Contains(result.Removed, removed => removed.GameRevision == OldRevision);
        Assert.Single(result.Kept);
        Assert.Equal(ClientRevision, result.Kept[0].Revision.GameRevision);
        Assert.Contains("writing into it", result.Kept[0].Why);

        Assert.False(Directory.Exists(server));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fetching));
    }

    // ---------------------------------------------------------------- the card in Settings

    [Fact]
    public void TheModeDefaultsToAutomaticAndRemembersWhatItIsSetTo() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        Assert.True(first.CleansUpRevisionsAutomatically);
        Assert.False(first.ManagesRevisionsByHand);
        Assert.Equal(PatchServerCleanupChoices.All, first.PatchServerCleanupModes);
        Assert.Contains("18 GB", MainWindowViewModel.PatchServerCleanupRecommendation);

        first.PatchServerRevisionCleanup = PatchServerCleanupChoices.Manual;

        Assert.False(first.CleansUpRevisionsAutomatically);
        Assert.True(first.ManagesRevisionsByHand);

        var reopened = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);
        Assert.True(reopened.ManagesRevisionsByHand);

        // A value this build does not recognise - one written by a later version - falls back to the
        // recommended answer rather than leaving the launcher with no mode at all.
        first.PatchServerRevisionCleanup = "SomethingElse";
        Assert.True(first.CleansUpRevisionsAutomatically);
    }

    [Fact]
    public async Task TurningAutomaticCleanupOnRemovesTheSpareRevisionThereAndThen() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));
        AgeEverything(patchServer);

        var server = workspace.CreateDirectory("server");
        workspace.WriteFile("server/Config/Imlight.ini",
            $"[Global Settings]\r\nGameRevision = {ServerRevision}\r\n");
        workspace.WriteFile("server/ImLightServer.exe", Array.Empty<byte>());

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ServerPath = server;
        store.Settings.PatchServerPath = patchServer;
        store.Settings.ClientPath = GameFolder(workspace, ClientRevision);
        store.Settings.PatchServerRevisionCleanup = PatchServerCleanupChoices.Manual;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        // Manual means manual: opening the launcher leaves all three where they are.
        Assert.Equal(2, PatchServerRevisionCatalogue
            .List(patchServer, ServerRevision).Count);

        launcher.PatchServerRevisionCleanup = PatchServerCleanupChoices.Automatic;

        // The cleanup is a background pass, so the removal is waited for rather than assumed.
        await WaitFor(() => !Directory.Exists(
            System.IO.Path.Combine(patchServer, "data", "V_" + OldRevision)));

        var left = PatchServerRevisionCatalogue.List(patchServer, ServerRevision);
        Assert.Equal(ClientRevision, Assert.Single(left).GameRevision);
    }

    [Fact]
    public async Task TheListShowsEveryRevisionAndTicksTheOnesInUse() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        Revision(workspace, patchServer, ServerRevision, ("Data/Root.wad", 50));
        Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));
        AgeEverything(patchServer);

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.PatchServerPath = patchServer;
        store.Settings.ServerPath = workspace.CreateDirectory("server");
        workspace.WriteFile("server/Config/Imlight.ini",
            $"[Global Settings]\r\nGameRevision = {ServerRevision}\r\n");
        store.Settings.ClientPath = GameFolder(workspace, ClientRevision);

        // Manual, so the list is the thing being tested: automatic mode would have removed the spare one on
        // the way in, which is a different test.
        store.Settings.PatchServerRevisionCleanup = PatchServerCleanupChoices.Manual;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);
        await launcher.RefreshPatchServerRevisionsCommand.ExecuteAsync(null);

        Assert.False(launcher.CleansUpRevisionsAutomatically);
        Assert.Equal(3, launcher.PatchServerRevisions.Count);
        Assert.True(launcher.HasPatchServerRevisions);

        // The empty state is what the list would say if it were empty, and it is hidden while the list is
        // not - so it names the reason a patch server has nothing yet rather than being blank.
        Assert.Contains("has not fetched", launcher.PatchServerRevisionsEmptyText);

        var rows = launcher.PatchServerRevisions.ToDictionary(row => row.GameRevision);

        // The two in use open ticked and the spare one does not, so the list starts on the answer the
        // automatic mode would have reached by itself.
        Assert.True(rows[ServerRevision].Keep);
        Assert.True(rows[ClientRevision].Keep);
        Assert.False(rows[OldRevision].Keep);
        Assert.True(rows[OldRevision].CanBeDeleted);

        Assert.True(launcher.CanDeleteUnkeptRevisions);
        Assert.Contains("holds 3 revisions", launcher.PatchServerRevisionsStatus);

        // Deleting what is not ticked takes the spare revision and leaves the two in use alone.
        await launcher.DeleteUnkeptRevisionsCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(System.IO.Path.Combine(patchServer, "data", "V_" + OldRevision)));
        Assert.Equal(2, launcher.PatchServerRevisions.Count);
        Assert.False(launcher.CanDeleteUnkeptRevisions);
        Assert.Contains("Removed 1 unused revision", launcher.PatchServerRevisionsStatus);
    }

    [Fact]
    public async Task AskingForOneRevisionSaysWhichOfTheTwoThingsItIs() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        Revision(workspace, patchServer, ClientRevision, ("Data/Root.wad", 100));
        Revision(workspace, patchServer, OldRevision, ("Data/Old.wad", 7));
        AgeEverything(patchServer);

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.PatchServerPath = patchServer;
        // Manual, so both revisions are still there to be asked about.
        store.Settings.PatchServerRevisionCleanup = PatchServerCleanupChoices.Manual;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);
        await launcher.RefreshPatchServerRevisionsCommand.ExecuteAsync(null);

        // The patch server is not running, so nothing can be said about which revision it holds and the
        // answer is the honest one: a named revision cannot be asked for.
        var row = launcher.PatchServerRevisions.First(r => r.GameRevision == OldRevision);
        await row.RequestDownloadCommand!.ExecuteAsync(null);

        Assert.Contains("cannot be asked for", launcher.PatchServerRevisionsStatus);
        Assert.Contains("not which revision", launcher.PatchServerRevisionsStatus);
    }

    [Fact]
    public async Task APatchServerWithNothingFetchedHasAnEmptyListAndSaysWhy() {
        using var workspace = new TempWorkspace();
        var patchServer = PatchServer(workspace);

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.PatchServerPath = patchServer;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);
        await launcher.RefreshPatchServerRevisionsCommand.ExecuteAsync(null);

        Assert.Empty(launcher.PatchServerRevisions);
        Assert.False(launcher.HasPatchServerRevisions);
        Assert.Contains("has not fetched", launcher.PatchServerRevisionsEmptyText);

        // With no folder at all, the card says that instead.
        var bare = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("bare.json")), fetchArt: false);

        await bare.RefreshPatchServerRevisionsCommand.ExecuteAsync(null);

        Assert.Equal("No patch server folder is set.", bare.PatchServerRevisionsEmptyText);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A patch server folder with the config the launcher would have written.</summary>
    private static string PatchServer(TempWorkspace workspace) {
        var folder = workspace.CreateDirectory("patchserver");
        File.WriteAllText(PatchServerConfig.PathFor(folder),
            "[fetcher]\nsave_directory = \"data\"\n");
        return folder;
    }

    /// <summary>A revision folder with the files given, under the patch server's data directory.</summary>
    private static string Revision(
        TempWorkspace workspace, string patchServer, string revision,
        params (string Path, int Size)[] files) {

        foreach (var (path, size) in files) {
            workspace.WriteFile(
                System.IO.Path.Combine("patchserver", "data", "V_" + revision,
                    path.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                new byte[size]);
        }

        return System.IO.Path.Combine(patchServer, "data", "V_" + revision);
    }

    /// <summary>
    /// Pushes everything written under a patch server back an hour, so a revision the test has just written
    /// is judged as one nothing is writing to any more rather than as a fetch in progress.
    /// </summary>
    private static void AgeEverything(string patchServer) {
        var older = DateTime.UtcNow.AddHours(-1);

        foreach (var path in Directory.EnumerateFiles(patchServer, "*", SearchOption.AllDirectories)) {
            File.SetLastWriteTimeUtc(path, older);
        }
    }

    /// <summary>A game folder whose build is <paramref name="revision"/>.</summary>
    private static string GameFolder(TempWorkspace workspace, string revision) {
        var folder = workspace.CreateDirectory("game");
        workspace.WriteFile(System.IO.Path.Combine("game", "Bin", "revision.dat"), revision + "\n");
        return folder;
    }

    /// <summary>Waits for a background pass to have happened, or gives up and lets the assertion fail.</summary>
    private static async Task WaitFor(Func<bool> done) {
        for (var attempt = 0; attempt < 100 && !done(); attempt++) {
            await Task.Delay(50);
        }
    }
}
