using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// When the automatic cloud backup is allowed to start, which is the one thing about it the launcher cannot get
/// wrong twice: the sync closes and reopens both databases, so a sync that ran while something else was using one
/// of them would stop a server that is starting, or record a database as it was mid-write. The rule is pinned
/// down here rather than left inside the view model, the same way the phase rules are.
/// </summary>
public class CloudSyncGateTests {

    [Fact]
    public void TheServerHoldingTheDatabaseStopsTheAutomaticSync() {
        // Everything else is in order: the feature is on, no sync is running, no Play is on its way, the
        // launcher's own database is up, and the player database has settled. The server alone is what this
        // test is about.
        Assert.False(MayStart(ServerState.Starting));
        Assert.False(MayStart(ServerState.WarmingUp));
        Assert.False(MayStart(ServerState.Ready));
        Assert.False(MayStart(ServerState.Stopping));

        // And a server that is not running holds nothing, so the launcher has both databases to itself. One that
        // died holds nothing either - the launcher takes the database back from it rather than waiting for it.
        Assert.True(MayStart(ServerState.Stopped));
        Assert.True(MayStart(ServerState.Faulted));
    }

    [Fact]
    public void NothingStartsWhilePlayIsOnItsWayToTheServer() {
        // Play is the handover: it releases the player database and starts Imlight with it, and the server does
        // not report itself as starting until after that. A sync started in that window would take the database
        // back for a copy - and start it again afterwards - underneath the server's own boot.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: true, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: false));
    }

    [Fact]
    public void NothingAutomaticStartsWhileTheDatabasesAreBeingHandedOver() {
        // Play asks for its own sync before it lets go of anything, and that sync closes both databases. A second
        // one started by the automatic path in the same window - from the launcher's own database coming back, for
        // instance - would take the player database back and start it again, underneath the server's own boot.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: true));
    }

    [Fact]
    public void AnOpenThatIsStillRunningCountsAsHoldingTheDatabase() {
        // The launcher's own RavenDB takes tens of seconds to come up, and Play can arrive in the middle of that
        // - in which case there is very much something to hand over, and it has to be stopped before the server
        // can be given the folder. Reading it as "nothing is holding it" is how a server started from under a
        // warming-up launcher dies on a lock it cannot see.
        Assert.True(MainWindowViewModel.SomethingHoldsThePlayerDatabase(
            opening: true, isUp: false, isRunning: false));

        Assert.True(MainWindowViewModel.SomethingHoldsThePlayerDatabase(
            opening: false, isUp: true, isRunning: true));

        // And a database nobody has: no open in flight, nothing held, no process of ours running.
        Assert.False(MainWindowViewModel.SomethingHoldsThePlayerDatabase(
            opening: false, isUp: false, isRunning: false));
    }

    [Fact]
    public void NothingStartsBeforeTheLaunchersOwnDatabaseIsUp() {
        // The sign-in is a document in it, so a sync started before it is up has no token to read and no database
        // of its own to close for the copy - which is why the automatic path is asked for from the moment that
        // database comes up, and not before.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: false,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: false));
    }

    [Fact]
    public void NothingStartsWhileThePlayerDatabaseIsStillOpening() {
        // The server's own RavenDB is being started, and its files are being written as the sync would be reading
        // them: a hash taken then would be recorded as the truth about a database that was never in that state.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: false, handingOverToServer: false));
    }

    [Fact]
    public void NothingStartsWhileTheFeatureIsOffOrOneIsAlreadyRunning() {
        // Off is off: nothing is uploaded or restored by itself until the box is ticked under Settings.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: false, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: false));

        // And a sync in flight is not joined by a second one: the two would close and reopen the same databases
        // over each other.
        Assert.False(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: true, playInFlight: false, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: false));
    }

    [Fact]
    public void TheIdleLauncherIsTheOneMomentItMayStart() {
        Assert.True(MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: true,
            ServerState.Stopped, playerDatabaseIsSettled: true, handingOverToServer: false));
    }

    [Fact]
    public void TurningItOnWithNothingSetUpStartsNothing() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        launcher.BackUpToCloud = true;

        // No install, so no database of either kind and nothing signed in: the choice is remembered, and nothing
        // is reported about a sync that was never started.
        Assert.True(launcher.BackUpToCloud);
        Assert.Equal(string.Empty, launcher.CloudBackupStatus);
        Assert.False(launcher.IsCloudBackupSignedIn);
    }

    /// <summary>The gate with everything but the server in order.</summary>
    private static bool MayStart(ServerState server)
        => MainWindowViewModel.CloudSyncMayStart(
            backUpToCloud: true, syncInFlight: false, playInFlight: false, launcherDatabaseIsUp: true, server,
            playerDatabaseIsSettled: true, handingOverToServer: false);
}
