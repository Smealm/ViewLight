using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The top-left notification area is one list rather than one card per feature, so two things happening
/// at once stack instead of covering one another.
/// </summary>
public class NotificationAreaTests {

    private static MainWindowViewModel Launcher(TempWorkspace workspace) {
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = workspace.CreateDirectory("game");
        store.Settings.ServerPath = workspace.CreateDirectory("server");
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }

    [Fact]
    public void SyncAndTheProfilesDatabaseShareOneList() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        Assert.Empty(launcher.Notifications);
        Assert.False(launcher.HasNotifications);

        launcher.IsLauncherDatabaseStarting = true;

        var profiles = Assert.Single(launcher.Notifications);
        Assert.Equal("profiles-database", profiles.Key);
        Assert.True(profiles.IsWorking);
        Assert.True(launcher.HasNotifications);

        launcher.IsCloudSyncRunning = true;

        Assert.Equal(2, launcher.Notifications.Count);
        Assert.Equal(new[] { "profiles-database", "cloud-sync" },
            launcher.Notifications.Select(notification => notification.Key));

        // Saying the same thing again replaces the row rather than adding a second one.
        launcher.IsCloudSyncRunning = false;
        launcher.IsCloudSyncRunning = true;
        Assert.Equal(2, launcher.Notifications.Count);

        launcher.IsLauncherDatabaseStarting = false;

        var sync = Assert.Single(launcher.Notifications);
        Assert.Equal("cloud-sync", sync.Key);

        launcher.IsCloudSyncRunning = false;

        Assert.Empty(launcher.Notifications);
        Assert.False(launcher.HasNotifications);
    }

    [Fact]
    public void OneArrowFoldsTheMessagesAwayAndOpensThemAgain() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.IsLauncherDatabaseStarting = true;
        launcher.IsCloudSyncRunning = true;

        // A new notice arrives open, because it is the thing the person needs to see.
        Assert.False(launcher.IsNotificationTrayCollapsed);
        Assert.True(launcher.IsNotificationListVisible);
        Assert.Equal("2 notifications", launcher.NotificationTrayText);
        Assert.Equal("▴", launcher.NotificationTrayGlyph);

        launcher.ToggleNotificationTrayCommand.Execute(null);

        Assert.True(launcher.IsNotificationTrayCollapsed);
        Assert.False(launcher.IsNotificationListVisible);
        Assert.Equal("▾", launcher.NotificationTrayGlyph);
        Assert.Equal("Show notifications", launcher.NotificationTrayHint);
        Assert.True(launcher.HasNotifications);

        launcher.ToggleNotificationTrayCommand.Execute(null);

        Assert.False(launcher.IsNotificationTrayCollapsed);
        Assert.True(launcher.IsNotificationListVisible);

        // When there is nothing to say, the tray goes with the rows rather than sitting there empty.
        launcher.IsLauncherDatabaseStarting = false;
        launcher.IsCloudSyncRunning = false;

        Assert.False(launcher.HasNotifications);
        Assert.False(launcher.IsNotificationListVisible);
    }

    [Fact]
    public async Task TheTrayFoldsItselfAwayAfterADelay() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.NotificationTrayDelay = TimeSpan.FromMilliseconds(60);
        launcher.IsLauncherDatabaseStarting = true;

        for (var attempt = 0; attempt < 100 && !launcher.IsNotificationTrayCollapsed; attempt++) {
            await Task.Delay(30);
        }

        Assert.True(launcher.IsNotificationTrayCollapsed,
            "The notice stayed open for ever instead of folding into the tray.");
        Assert.False(launcher.IsNotificationListVisible);
    }

    [Fact]
    public async Task ATrayThePersonOpenedStaysOpen() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.NotificationTrayDelay = TimeSpan.FromMilliseconds(60);
        launcher.IsLauncherDatabaseStarting = true;

        launcher.ToggleNotificationTrayCommand.Execute(null);
        Assert.True(launcher.IsNotificationTrayCollapsed);

        launcher.ToggleNotificationTrayCommand.Execute(null);
        Assert.False(launcher.IsNotificationTrayCollapsed);

        await Task.Delay(300);

        Assert.False(launcher.IsNotificationTrayCollapsed);
    }
}
