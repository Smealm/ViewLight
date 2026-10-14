using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The rule that decides whether Imlight is pointed at this machine's patch server, and what Home says
/// about one while it is part of the stack Play starts.
/// </summary>
public class PatchServerWiringTests {

    [Fact]
    public void ThisMachinesPatchServerIsTheAddressImlightIsGivenOrNothingIs() {
        // This machine's patch server is the one that was chosen, and it is up: the address and the port
        // the launcher was given, and that is the whole rule - nothing asks what is behind it.
        Assert.Equal("http://127.0.0.1:12369",
            MainWindowViewModel.PatchServerAddressFor(true, ServerState.Ready, 12369));

        // Anything else leaves Imlight's config exactly as it is. That is how the public patch server,
        // and a patch server somebody runs elsewhere, are reached: by not being touched.
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(true, ServerState.Starting, 12369));
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(true, ServerState.WarmingUp, 12369));
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(true, ServerState.Faulted, 12369));
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(true, ServerState.Stopped, 12369));

        // A remote patch server: the address in the config is somebody else's and is left alone, whatever
        // this machine has installed and however well it is answering.
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(false, ServerState.Ready, 12369));
        Assert.Null(MainWindowViewModel.PatchServerAddressFor(false, ServerState.Stopped, 12369));
    }

    [Fact]
    public void APortOfItsOwnIsTheAddressImlightIsGiven() {
        Assert.Equal("http://127.0.0.1:12500",
            MainWindowViewModel.PatchServerAddressFor(true, ServerState.Ready, 12500));
    }

    [Fact]
    public void AConfiguredPatchServerShowsARowOnHomeAndOneThatIsNotDoesNot() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        // Nothing configured: no folder, no executable, no row.
        Assert.False(launcher.PatchServerInstalled);
        Assert.False(launcher.ShowPatchServerRow);
        Assert.Equal("Not installed", launcher.PatchServerRowState);

        var folder = workspace.CreateDirectory("patchserver");
        File.WriteAllBytes(PatchServerRunner.ExecutablePathFor(folder), [0]);

        launcher.PatchServerPath = folder;

        Assert.True(launcher.PatchServerInstalled);
        Assert.True(launcher.ShowPatchServerRow);
        Assert.Equal("Patch server", launcher.PatchServerRowLabel);
        Assert.Equal("Stopped", launcher.PatchServerRowState);

        // A user who runs their own patch server - the launcher not starting or stopping it - is not shown
        // one either, because it is not part of what Play does.
        launcher.PatchServerManaged = false;
        Assert.False(launcher.ShowPatchServerRow);

        launcher.PatchServerManaged = true;
        Assert.True(launcher.ShowPatchServerRow);

        // And neither is one on a launcher pointed at somebody else's patch server, however installed it
        // is and whatever it is doing.
        launcher.UsesRemotePatchServer = true;
        Assert.False(launcher.PatchServerIsThisMachines);
        Assert.False(launcher.ShowPatchServerRow);

        launcher.UseLocalPatchServer = true;
        Assert.True(launcher.PatchServerIsThisMachines);
        Assert.True(launcher.ShowPatchServerRow);
    }

    [Fact]
    public void WhosePatchServerItIsIsAChoiceAndItIsKeptBetweenRuns() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        // A patch server on this machine is what the launcher is for, so that is the default; the copy the
        // setting above it controls is the one that asks, so doing it unasked is off.
        Assert.True(first.UseLocalPatchServer);
        Assert.False(first.UsesRemotePatchServer);
        Assert.True(first.PatchServerManaged);
        Assert.False(first.PatchServerPreloadAutomatically);

        // The two radio buttons are one setting, so writing either side of it settles both.
        first.UsesRemotePatchServer = true;
        Assert.False(first.UseLocalPatchServer);

        first.PatchServerPreloadAutomatically = true;

        var reopened = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        Assert.False(reopened.UseLocalPatchServer);
        Assert.True(reopened.UsesRemotePatchServer);
        Assert.True(reopened.PatchServerPreloadAutomatically);
    }

    [Fact]
    public void ThePortAndTheFolderAreKeptBetweenRuns() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);
        first.PatchServerPort = 12500;

        var folder = workspace.CreateDirectory("patchserver");
        first.PatchServerPath = folder;

        var reopened = new MainWindowViewModel(new LauncherSettingsStore(file), fetchArt: false);

        Assert.Equal(12500, reopened.PatchServerPort);
        Assert.Equal(folder, reopened.PatchServerPath);
        Assert.Equal("http://127.0.0.1:12500", reopened.LocalPatchServerUrl);
    }

    [Fact]
    public void TheRowSaysWhatTheProcessIsDoingAndNothingMore() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        var folder = workspace.CreateDirectory("patchserver");
        File.WriteAllBytes(PatchServerRunner.ExecutablePathFor(folder), [0]);
        launcher.PatchServerPath = folder;

        // The row is the process and nothing else: what the patch server holds is its own business, and a
        // half-finished fetch is not a second state for the row to report.
        Assert.Equal("Stopped", launcher.PatchServerRowState);

        launcher.PatchServerState = ServerState.Ready;
        Assert.Equal("Running", launcher.PatchServerRowState);

        launcher.PatchServerState = ServerState.WarmingUp;
        Assert.Equal("Warming up", launcher.PatchServerRowState);

        launcher.PatchServerState = ServerState.Stopped;
        Assert.Equal("Stopped", launcher.PatchServerRowState);
    }

    [Fact]
    public void ThePatchServerPortMustNotBeImlightsOwnPatchPort() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        var serverFolder = workspace.CreateDirectory("server");

        // The server's own config is what says which port Imlight's own listener takes, so the clash can
        // only be known once there is a server to read it from.
        workspace.WriteFile(
            Path.Combine("server", "Config", "Imlight.ini"),
            "[Patch Server]\r\nPatchServerInternalUrl = http://127.0.0.1:12369\r\nPatchServerPort = 12500\r\n");

        store.Settings.ServerPath = serverFolder;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        Assert.Equal(12500, launcher.ImlightPatchServerPort);
        Assert.False(launcher.PatchServerPortClashes);

        // Taking the port Imlight's own patch listener needs stops the server during boot, so the
        // launcher says so instead of finding out from a crash.
        launcher.PatchServerPort = 12500;
        Assert.True(launcher.PatchServerPortClashes);
        Assert.Contains("12500", launcher.PatchServerPortClashWarning);

        launcher.PatchServerPort = 12400;
        Assert.False(launcher.PatchServerPortClashes);
    }

    [Fact]
    public void TheStopButtonIsLiveWhileEitherOfTheTwoServersIsUp() {
        // The ordinary case: both stopped, nothing to stop.
        Assert.False(MainWindowViewModel.CanStopSomething(ServerState.Stopped, patchServerIsRunning: false));
        Assert.False(MainWindowViewModel.CanStopSomething(ServerState.Stopping, patchServerIsRunning: false));

        // The server alone, as before.
        Assert.True(MainWindowViewModel.CanStopSomething(ServerState.Ready, patchServerIsRunning: false));
        Assert.True(MainWindowViewModel.CanStopSomething(ServerState.WarmingUp, patchServerIsRunning: false));

        // And a patch server started on its own from Settings, which has no server behind it.
        Assert.True(MainWindowViewModel.CanStopSomething(ServerState.Stopped, patchServerIsRunning: true));
        Assert.True(MainWindowViewModel.CanStopSomething(ServerState.Stopping, patchServerIsRunning: true));
    }

    [Fact]
    public void ThePatchServerGoesDownWithTheServerUnlessItIsStillFetching() {
        // The ordinary case: both are processes this launcher started, so the one press stops both, and
        // neither is left holding its port after the other is gone.
        Assert.True(MainWindowViewModel.PatchServerStopsWithTheServer(
            patchServerIsRunning: true, patchServerCanServeItsRevision: true));

        // Still on its first fetch: stopping it is stopping the download, and a fetch only ever advances
        // while the process is alive - so the server stops and the fetching patch server does not.
        Assert.False(MainWindowViewModel.PatchServerStopsWithTheServer(
            patchServerIsRunning: true, patchServerCanServeItsRevision: false));

        // Nothing to stop either way.
        Assert.False(MainWindowViewModel.PatchServerStopsWithTheServer(
            patchServerIsRunning: false, patchServerCanServeItsRevision: true));
        Assert.False(MainWindowViewModel.PatchServerStopsWithTheServer(
            patchServerIsRunning: false, patchServerCanServeItsRevision: false));
    }

    [Fact]
    public void TheFetchReportIsOnlyAboutAnAddressThisSessionWillUse() {
        const string local = "http://127.0.0.1:12369";

        Assert.True(MainWindowViewModel.IsTheLocalPatchServerAddress(local, local));

        // A trailing slash, or a config that capitalised the host, is the same address.
        Assert.True(MainWindowViewModel.IsTheLocalPatchServerAddress($"{local}/", local));
        Assert.True(MainWindowViewModel.IsTheLocalPatchServerAddress(" HTTP://127.0.0.1:12369 ", local));

        // A session pointed at the public patch server, or at somebody else's Aurorium, is not one this
        // machine's patch server can say anything about.
        Assert.False(MainWindowViewModel.IsTheLocalPatchServerAddress("https://patcher.phill030.de", local));
        Assert.False(MainWindowViewModel.IsTheLocalPatchServerAddress("http://127.0.0.1:12370", local));
        Assert.False(MainWindowViewModel.IsTheLocalPatchServerAddress(string.Empty, local));
        Assert.False(MainWindowViewModel.IsTheLocalPatchServerAddress(null, local));
    }

    [Fact]
    public void ASenselessPortFallsBackToTheStockOne() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        launcher.PatchServerPort = 0;
        Assert.Equal(PatchServerConfig.DefaultPort, launcher.PatchServerPort);

        launcher.PatchServerPort = 70000;
        Assert.Equal(PatchServerConfig.DefaultPort, launcher.PatchServerPort);
    }
}
