using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The revision the server accepts and the revision the patch server actually holds disagree the moment
/// the game has been patched past the server's own build: Imlight asks for
/// <c>V_{GameRevision}</c>, gets a 404 during boot, and with the revision check on it turns the same
/// client away at login. The toggle that keeps the two in step is pinned down here - what it reads out of a
/// patch server, what it writes, and what it puts back when it is turned off.
/// </summary>
/// <remarks>
/// The direction is the whole point of it: a patch server fetches whatever revision Wizard101 is serving
/// and cannot be asked for another one, so the value that moves is the <i>server's</i> - which is why this
/// is on by default. Off is the arrangement for a server build deliberately held at one revision.
/// </remarks>
public class PatchServerRevisionSyncTests {

    /// <summary>The revision the installed server build was published with.</summary>
    private const string ServerRevision = "r803238.Wizard_1_610";

    /// <summary>The one Wizard101's own patch server is offering, and therefore the one Aurorium holds.</summary>
    private const string HeldRevision = "r806919.Wizard_1_610";

    [Fact]
    public void ARevisionNameIsTheGameRevisionItStandsFor() {
        Assert.Equal(HeldRevision, PatchServerConfig.GameRevisionFrom($"V_{HeldRevision}"));
        Assert.Equal(HeldRevision, PatchServerConfig.GameRevisionFrom($"  v_{HeldRevision}  "));

        // A name that is not one of Aurorium's is not a game revision, however revision-like it looks.
        Assert.Null(PatchServerConfig.GameRevisionFrom(HeldRevision));
        Assert.Null(PatchServerConfig.GameRevisionFrom("V_"));
        Assert.Null(PatchServerConfig.GameRevisionFrom(""));
        Assert.Null(PatchServerConfig.GameRevisionFrom(null));
    }

    [Fact]
    public void TheNewestRevisionIsTheOneWithTheHighestNumber() {
        // The order the server lists them in is not the answer: this one was handed them oldest first.
        Assert.Equal(HeldRevision,
            PatchServerConfig.NewestGameRevision([$"V_{ServerRevision}", $"V_{HeldRevision}"]));

        Assert.Equal(HeldRevision, PatchServerConfig.NewestGameRevision([$"V_{HeldRevision}"]));

        // A name with no revision number this can read is kept, and then any later name is compared
        // against it rather than thrown away - so a server with only one such revision still answers.
        Assert.Equal("handwritten", PatchServerConfig.NewestGameRevision(["V_handwritten"]));
        Assert.Equal("handwritten", PatchServerConfig.NewestGameRevision(["notes.txt", "V_handwritten"]));
        Assert.Equal("handwritten",
            PatchServerConfig.NewestGameRevision(["V_handwritten", $"V_{HeldRevision}"]));
    }

    [Fact]
    public void APatchServerHoldingNothingHasNoRevisionToRead() {
        Assert.Null(PatchServerConfig.NewestGameRevision([]));
        Assert.Null(PatchServerConfig.NewestGameRevision(null));
        Assert.Null(PatchServerConfig.NewestGameRevision(["notes.txt"]));
    }

    [Fact]
    public void TheMirrorWritesTheRevisionThePatchServerHolds() {
        // The case it exists for: the server is pinned to the build it was published with, and the patch
        // server has the one Wizard101 currently offers.
        Assert.Equal(HeldRevision, MainWindowViewModel.RevisionToMirror(ServerRevision, [$"V_{HeldRevision}"]));

        // ... and the newest wins when it is not the first one listed.
        Assert.Equal(HeldRevision, MainWindowViewModel.RevisionToMirror(ServerRevision,
            [$"V_{ServerRevision}", $"V_{HeldRevision}"]));

        // Already matching, either way round the casing and spacing: nothing is written.
        Assert.Null(MainWindowViewModel.RevisionToMirror(HeldRevision, [$"V_{HeldRevision}"]));
        Assert.Null(MainWindowViewModel.RevisionToMirror(HeldRevision, [$"v_{HeldRevision}"]));

        // Nothing to match with: a patch server that holds nothing, or names nothing this understands.
        Assert.Null(MainWindowViewModel.RevisionToMirror(ServerRevision, []));
        Assert.Null(MainWindowViewModel.RevisionToMirror(ServerRevision, null));
        Assert.Null(MainWindowViewModel.RevisionToMirror(ServerRevision, ["notes.txt"]));
    }

    [Fact]
    public void TurningItOffPutsBackOnlyWhatTheLauncherWrote() {
        // The launcher's own value is still in the config: the release's value goes back.
        Assert.Equal(ServerRevision,
            MainWindowViewModel.RevisionToRestore(HeldRevision, ServerRevision, HeldRevision));

        // Something else is in there now - a newer server build, or an edit by hand - and a value that is
        // not the launcher's is not the launcher's to undo.
        Assert.Null(MainWindowViewModel.RevisionToRestore(HeldRevision, ServerRevision, "r900000.Wizard_1_700"));
        Assert.Null(MainWindowViewModel.RevisionToRestore(HeldRevision, ServerRevision, null));

        // Nothing was ever written, or there was nothing to put back.
        Assert.Null(MainWindowViewModel.RevisionToRestore(null, ServerRevision, HeldRevision));
        Assert.Null(MainWindowViewModel.RevisionToRestore(HeldRevision, null, HeldRevision));

        // And turning it off when nothing moved is not a write at all.
        Assert.Null(MainWindowViewModel.RevisionToRestore(HeldRevision, HeldRevision, HeldRevision));
    }

    [Fact]
    public void TheLauncherKeepsTheTwoRevisionsInStepUntilItIsToldNotTo() {
        using var workspace = new TempWorkspace();
        var (launcher, store, serverFolder) = Launcher(workspace);

        // The default: the server's accepted revision is kept in step with the patch server's, so the box in
        // Network is not the user's to type in - and with no patch server running the line says where the
        // value comes from rather than pretending one has been read.
        Assert.True(launcher.PatchServerRevisionSynced);
        Assert.False(launcher.CanEditGameRevision);
        Assert.Equal(ServerRevision, launcher.GameRevision);
        Assert.Equal(
            "Waiting for the patch server: the revision is set from it as soon as one is running.",
            launcher.PatchServerRevisionStatus);

        // What it does when a patch server answers: the config holds the patch server's revision, and the box
        // follows it.
        var config = ImlightConfig.TryLoad(serverFolder)!;
        config.GameRevision = HeldRevision;
        config.Save(serverFolder);

        // What the launcher keeps when it writes one: the value it wrote, and the value the config had
        // first - which is what turning the sync off puts back.
        store.Settings.MirroredRevision = HeldRevision;
        store.Settings.RevisionBeforeMirror = ServerRevision;
        store.Save();

        launcher.ReloadSettingsCommand.Execute(null);

        Assert.Equal(HeldRevision, launcher.GameRevision);
        Assert.Equal($"Set from the patch server: {HeldRevision}.", launcher.PatchServerRevisionStatus);

        launcher.PatchServerRevisionSynced = false;

        // Off: the box is the user's again, and the launcher's own writing is undone - on disk as well as on
        // screen.
        Assert.True(launcher.CanEditGameRevision);
        Assert.Equal(ServerRevision, launcher.GameRevision);
        Assert.Equal(ServerRevision, ImlightConfig.TryLoad(serverFolder)!.GameRevision);
        Assert.Null(store.Settings.MirroredRevision);
        Assert.False(launcher.HasPatchServerRevisionStatus);
    }

    [Fact]
    public void TurningTheSyncOffLeavesAValueItDidNotWriteAlone() {
        using var workspace = new TempWorkspace();
        var (launcher, store, serverFolder) = Launcher(workspace);

        // A newer server build was installed, so the config holds its own revision rather than one the
        // launcher wrote. Putting the remembered value back would undo that.
        var config = ImlightConfig.TryLoad(serverFolder)!;
        config.GameRevision = "r900000.Wizard_1_700";
        config.Save(serverFolder);

        store.Settings.MirroredRevision = HeldRevision;
        store.Settings.RevisionBeforeMirror = ServerRevision;
        store.Save();

        launcher.PatchServerRevisionSynced = false;

        Assert.Equal("r900000.Wizard_1_700", ImlightConfig.TryLoad(serverFolder)!.GameRevision);
    }

    [Fact]
    public void TheSettingIsRememberedBetweenRunsAndDefaultsToSynced() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);
        first.PatchServerRevisionSynced = false;

        var reopened = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        Assert.False(reopened.PatchServerRevisionSynced);
        Assert.True(reopened.CanEditGameRevision);

        // A launcher that never chose keeps the two in step, which is what it defaults to.
        Assert.True(new LauncherSettings().PatchServerRevisionSynced);
    }

    [Fact]
    public void ARevisionTheOldArrangementLeftIsKeptRatherThanUndone() {
        using var workspace = new TempWorkspace();
        var (_, store, serverFolder) = Launcher(workspace);

        // What an older launcher left behind: GameRevision rewritten to the revision the patch server holds,
        // with the handle it kept for undoing that still on the settings file.
        var config = ImlightConfig.TryLoad(serverFolder)!;
        config.GameRevision = HeldRevision;
        config.Save(serverFolder);

        store.Settings.MirroredRevision = HeldRevision;
        store.Settings.RevisionBeforeMirror = ServerRevision;
        store.Save();

        var reopened = new MainWindowViewModel(store, fetchArt: false);

        // Kept, because this build wants exactly that: the value an older launcher put there is the patch
        // server's revision, and putting the server back on its published one would be undoing the very
        // arrangement the launcher is here to keep. Nothing is written until the sync is turned off.
        Assert.Equal(HeldRevision, reopened.GameRevision);
        Assert.Equal(HeldRevision, ImlightConfig.TryLoad(serverFolder)!.GameRevision);
        Assert.Equal(HeldRevision, store.Settings.MirroredRevision);
        Assert.True(reopened.PatchServerRevisionSynced);

        // And the remembered value is still there for the one thing that undoes it.
        reopened.PatchServerRevisionSynced = false;

        Assert.Equal(ServerRevision, ImlightConfig.TryLoad(serverFolder)!.GameRevision);
    }

    /// <summary>Settings pointed at an Imlight install on <see cref="ServerRevision"/>.</summary>
    private static (MainWindowViewModel Launcher, LauncherSettingsStore Store, string ServerFolder) Launcher(
        TempWorkspace workspace) {

        var serverFolder = workspace.CreateDirectory("server");
        workspace.WriteFile("server/Config/Imlight.ini",
            "[Global Settings]\r\n"
            + $"GameRevision = {ServerRevision}\r\n"
            + "EnforceRevision = false\r\n\r\n"
            + "[Patch Server]\r\n"
            + "PatchServerInternalUrl = http://127.0.0.1:12369\r\n"
            + "PatchServerPort = 12500\r\n");

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ServerPath = serverFolder;
        store.Save();

        return (new MainWindowViewModel(store, fetchArt: false), store, serverFolder);
    }
}
