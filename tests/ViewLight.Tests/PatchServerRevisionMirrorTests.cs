using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The revision the server accepts and the revision the patch server actually holds disagree the moment
/// the game has been patched past the server's own build: Imlight asks for
/// <c>V_{GameRevision}</c>, gets a 404 during boot, and with the revision check on it turns the same
/// client away at login. The toggle that makes the two match is pinned down here - what it reads out of a
/// patch server, what it writes, and what it puts back when it is turned off.
/// </summary>
public class PatchServerRevisionMirrorTests {

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
    public void TheToggleGreyedTheBoxAndGivesTheValueBackWhenItIsTurnedOff() {
        using var workspace = new TempWorkspace();

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

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        // Off to begin with, which is the whole point: nothing is set from anything until it is asked for.
        Assert.False(launcher.MirrorPatchServerRevision);
        Assert.Equal(ServerRevision, launcher.GameRevision);
        Assert.True(launcher.CanEditGameRevision);
        Assert.False(launcher.HasPatchServerRevisionStatus);

        launcher.MirrorPatchServerRevision = true;

        // The box stops taking edits, the value it had is remembered for putting back, and with no patch
        // server running the line says so rather than pretending a revision has been read.
        Assert.False(launcher.CanEditGameRevision);
        Assert.Equal(ServerRevision, store.Settings.RevisionBeforeMirror);
        Assert.Null(store.Settings.MirroredRevision);
        Assert.Equal($"Waiting for the patch server: the revision is set from it as soon as one is running.",
            launcher.PatchServerRevisionStatus);

        // What the mirror does when a patch server answers: the config is written, and the box - greyed
        // out, so this is the only thing that can move it - follows it.
        var config = ImlightConfig.TryLoad(serverFolder)!;
        config.GameRevision = HeldRevision;
        config.Save(serverFolder);
        store.Settings.MirroredRevision = HeldRevision;
        store.Save();

        launcher.ReloadSettingsCommand.Execute(null);

        Assert.Equal(HeldRevision, launcher.GameRevision);
        Assert.Equal($"Set from the patch server: {HeldRevision}.", launcher.PatchServerRevisionStatus);

        launcher.MirrorPatchServerRevision = false;

        // Turned off, the server is back on its own build's revision - on disk as well as on screen - and
        // the box is editable again.
        Assert.Equal(ServerRevision, launcher.GameRevision);
        Assert.Equal(ServerRevision, ImlightConfig.TryLoad(serverFolder)!.GameRevision);

        Assert.Null(store.Settings.MirroredRevision);
        Assert.True(launcher.CanEditGameRevision);
        Assert.False(launcher.HasPatchServerRevisionStatus);
    }

    [Fact]
    public void TurningItOffLeavesAValueItDidNotWriteAlone() {
        using var workspace = new TempWorkspace();

        var serverFolder = workspace.CreateDirectory("server");
        workspace.WriteFile("server/Config/Imlight.ini",
            "[Global Settings]\r\n"
            + $"GameRevision = {ServerRevision}\r\n\r\n"
            + "[Patch Server]\r\n"
            + "PatchServerInternalUrl = http://127.0.0.1:12369\r\n");

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ServerPath = serverFolder;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);
        launcher.MirrorPatchServerRevision = true;

        // A newer server build was installed while the mirror was on, so the config holds its own revision
        // rather than the one the launcher wrote. Returning to the release's value would undo that.
        var config = ImlightConfig.TryLoad(serverFolder)!;
        config.GameRevision = "r900000.Wizard_1_700";
        config.Save(serverFolder);

        launcher.MirrorPatchServerRevision = false;

        Assert.Equal("r900000.Wizard_1_700", ImlightConfig.TryLoad(serverFolder)!.GameRevision);
    }

    [Fact]
    public void TheToggleIsRememberedBetweenRuns() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);
        first.MirrorPatchServerRevision = true;

        var reopened = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        Assert.True(reopened.MirrorPatchServerRevision);
        Assert.False(reopened.CanEditGameRevision);

        // A launcher that never chose is off, which is what the setting defaults to.
        Assert.False(new LauncherSettings().MirrorPatchServerRevision);
    }
}
