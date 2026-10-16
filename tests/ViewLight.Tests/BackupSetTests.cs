using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Which two directories a backup is made of, and that the cloud settings survive being written and read.
/// The set is resolved the same way the launcher resolves them to open them, so a backup copies what a
/// running launcher would have written rather than a second guess at it.
/// </summary>
public class BackupSetTests {

    [Fact]
    public void WithNoServerOnlyTheLaunchersOwnNotesAreBackedUp() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        var directories = BackupSet.Directories(root, config: null, serverPath: null);

        Assert.Single(directories);
        Assert.Equal(LauncherDatabase.DataDirectoryFor(root), directories[BackupDatabases.Launcher]);

        // No server folder, so no server database - and that is a set of one rather than a failure.
        Assert.False(directories.ContainsKey(BackupDatabases.Server));
    }

    [Fact]
    public void WithAServerBothDatabasesAreInTheSet() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;

        var directories = BackupSet.Directories(root, config, server);

        Assert.Equal(2, directories.Count);
        Assert.Equal(LauncherDatabase.DataDirectoryFor(root), directories[BackupDatabases.Launcher]);

        // The server's own RavenDB data directory, resolved the way the launcher resolves it to read it -
        // never the launcher's folder, which would be a database locking the wrong door.
        Assert.Equal(config.ResolveEmbeddedDatabaseDirectory(server), directories[BackupDatabases.Server]);
        Assert.NotEqual(directories[BackupDatabases.Launcher], directories[BackupDatabases.Server]);
    }

    [Fact]
    public void TheEngineCopyIsNotPartOfABackup() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;

        // The launcher now keeps its own copy of the database engine in its own root. It is machinery rather
        // than notes - made from the installed server, at no cost to download - so it is not in a backup: what
        // is backed up is the two data directories, named explicitly, and nothing is swept up from the root.
        Assert.True(LauncherRavenEngine.MakeFresh(config, server, root).Copied);
        Assert.True(LauncherRavenEngine.IsComplete(root));

        var directories = BackupSet.Directories(root, config, server);

        Assert.Equal(2, directories.Count);
        Assert.DoesNotContain(LauncherRavenEngine.DirectoryFor(root), directories.Values);
        Assert.Equal(LauncherDatabase.DataDirectoryFor(root), directories[BackupDatabases.Launcher]);
    }

    [Fact]
    public void TheCloudBackupSettingsSurviveOnDisk() {
        using var workspace = new TempWorkspace();
        var path = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(path);
        store.Settings.BackUpToCloud = true;
        store.Settings.CloudBackupClientId = "client.apps.googleusercontent.com";
        store.Settings.CloudBackupClientSecret = "secret";
        store.Save();

        var reloaded = new LauncherSettingsStore(path).Settings;

        Assert.True(reloaded.BackUpToCloud);
        Assert.Equal("client.apps.googleusercontent.com", reloaded.CloudBackupClientId);
        Assert.Equal("secret", reloaded.CloudBackupClientSecret);
    }

    [Fact]
    public void TheCloudBackupIsOffUntilItIsTurnedOn() {
        using var workspace = new TempWorkspace();

        // A default launcher writes to nobody's Drive: the feature is the user's to switch on.
        var settings = new LauncherSettingsStore(workspace.Path("launcher.json")).Settings;

        Assert.False(settings.BackUpToCloud);
        Assert.Equal(string.Empty, settings.CloudBackupClientId);
    }

    /// <summary>An install with the config and the RavenDB the server ships, enough to resolve the folders.</summary>
    private static string Install(TempWorkspace workspace) {
        var server = workspace.CreateDirectory("server");

        workspace.WriteFile(Path.Combine(server, "Config", "Imlight.ini"),
            "[Global Settings]\nGameRevision = r806919.Wizard_1_610\n");
        workspace.WriteFile(Path.Combine(server, "Imlight.Director.exe"), [0]);
        workspace.WriteFile(Path.Combine(server, "RavenDBServer", "Raven.Server.dll"), [0]);

        return server;
    }
}
