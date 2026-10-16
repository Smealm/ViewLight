using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher keeps its own notes - profiles, playtime, badges, and which world each zone belongs to - in
/// a database of its own, run from the RavenDB the server ships. Where that database lives is the whole point
/// of it: the launcher's own folder in both modes, and never the server's data directory, which Imlight owns
/// and opens itself.
/// </summary>
public class LauncherDatabaseTests {

    [Fact]
    public void TheLauncherDatabaseLivesUnderTheLaunchersOwnRoot() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        Assert.Equal(Path.Combine(root, "database"), LauncherDatabase.DataDirectoryFor(root));
        Assert.Equal(Path.Combine(root, "raven", "launcher-settings.json"), LauncherDatabase.SettingsPathFor(root));
        Assert.Equal(Path.Combine(root, "raven", "launcher-logs"), LauncherDatabase.LogsDirectoryFor(root));

        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;
        var setup = LauncherDatabase.For(config, server, root, out var problem)!;

        Assert.Null(problem);
        Assert.Equal(LauncherDatabase.DataDirectoryFor(root), setup.DataDirectory);

        // Nothing shared with the server's database. Two Raven processes handed the same data directory would
        // be one of them holding a lock the other needs, and the same settings file would point one of them
        // at the other's documents.
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(config.ResolveEmbeddedDatabaseDirectory(server)),
            Path.TrimEndingDirectorySeparator(setup.DataDirectory));
        Assert.NotEqual(EmbeddedRavenSetup.SettingsPathFor(root), setup.SettingsFilePath);
        Assert.NotEqual(Path.Combine(root, "raven", "logs"), setup.LogsDirectory);
    }

    [Fact]
    public void TheLaunchersOwnDatabaseRunsOnTheLaunchersOwnCopyOfTheEngine() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;

        // Nothing copied yet: the engine inside the install is borrowed, which is what makes an install a
        // requirement rather than a nicety.
        var borrowed = LauncherDatabase.For(config, server, root, out var problem)!;

        Assert.Null(problem);
        Assert.Equal(config.ResolveRavenServerDirectory(server), borrowed.ServerDirectory);
        Assert.False(LauncherDatabase.RunsOnItsOwnEngine(borrowed, root));
        Assert.Contains("the server install", LauncherDatabase.EngineDescription(borrowed, root));

        // Once the launcher has made its own copy - from the install, at the version the install ships - that
        // is what its own notes run on, and the server's folder is not loaded at all.
        Assert.True(LauncherRavenEngine.MakeFresh(config, server, root).Copied);

        var own = LauncherDatabase.For(config, server, root, out problem)!;

        Assert.Null(problem);
        Assert.Equal(LauncherRavenEngine.DirectoryFor(root), own.ServerDirectory);
        Assert.True(LauncherDatabase.RunsOnItsOwnEngine(own, root));
        Assert.Contains("the launcher's own copy", LauncherDatabase.EngineDescription(own, root));

        // What is the launcher's own either way, and never the server's: the documents, the settings file,
        // the log and the port.
        Assert.Equal(LauncherDatabase.DataDirectoryFor(root), own.DataDirectory);
        Assert.Equal(LauncherDatabase.SettingsPathFor(root), own.SettingsFilePath);
        Assert.Equal(LauncherDatabase.LogsDirectoryFor(root), own.LogsDirectory);

        // And the install's own engine is still reachable on purpose, for the one case that needs it: a copy
        // that would not start.
        var fallback = LauncherDatabase.ForServerEngine(config, server, root, out problem)!;

        Assert.Null(problem);
        Assert.Equal(config.ResolveRavenServerDirectory(server), fallback.ServerDirectory);
        Assert.False(LauncherDatabase.RunsOnItsOwnEngine(fallback, root));
    }

    [Fact]
    public void WithNeitherEngineThereIsNothingToRunAndItSaysSo() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        var config = EmptyConfig(workspace.CreateDirectory("no-install"));

        Assert.Null(LauncherDatabase.For(config, workspace.Path("no-install"), root, out var problem));
        Assert.Contains("no RavenDB", problem);

        Assert.Null(LauncherDatabase.ForServerEngine(config, workspace.Path("no-install"), root, out var none));
        Assert.Contains("no RavenDB", none);
    }

    [Fact]
    public void APortableLauncherKeepsItsDatabaseInItsOwnFolder() {
        using var workspace = new TempWorkspace();

        var portable = workspace.CreateDirectory("ViewLight");
        File.WriteAllText(Path.Combine(portable, "portable.txt"), string.Empty);

        var wasOn = PortableMode.IsOn;
        var previousRoot = LauncherPaths.DataRoot;

        try {
            Assert.True(PortableMode.Apply(portable));

            var root = LauncherPaths.DataRoot;
            Assert.Equal(portable, root);

            // The whole point of the mode: the database is one more thing inside the folder, so a copied
            // folder arrives with its profiles in it rather than starting empty.
            var directory = LauncherDatabase.DataDirectoryFor(root);
            Assert.StartsWith(portable, directory, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(portable, "database"), directory);
        }
        finally {
            LauncherPaths.DataRoot = previousRoot;

            if (!wasOn) {
                PortableMode.Apply(Path.Combine(workspace.Root, "no-marker"));
            }
        }
    }

    [Fact]
    public void TheDatabaseRunsTheServersOwnRavenBinaries() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace);

        var setup = LauncherDatabase.For(ImlightConfig.TryLoad(server)!, server, workspace.Path("data"), out _)!;

        // The server's RavenDB, not a second copy: same folder, same assembly, same codebase as Imlight - and
        // an Imlight update replaces this database along with everything else it ships.
        Assert.Equal(ImlightConfig.RavenServerDirectory(server), setup.ServerDirectory);
        Assert.Equal(Path.Combine(server, "RavenDBServer", "Raven.Server.dll"), setup.ServerAssemblyPath);
    }

    [Fact]
    public void AFullRavenInstallIsWhereItRunsFromWhenTheServerUsesOne() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace, fullServer: true);

        var setup = LauncherDatabase.For(ImlightConfig.TryLoad(server)!, server, workspace.Path("data"), out var problem)!;

        // The switch that replaces the database is the server's own setting, and it replaces this database too:
        // they are the same engine, and a launcher running the old one would be reading half the story.
        Assert.Null(problem);

        // The config's own value ends in a separator, which the resolver keeps - so the comparison is about
        // the folder rather than about how it was spelled.
        Assert.Equal(Path.Combine(server, "RavenDB", "Server"),
            Path.TrimEndingDirectorySeparator(setup.ServerDirectory));
    }

    [Fact]
    public void ItsPortIsItsOwnAndAFreeOne() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace);

        // The port the server's database is configured with, which this must not take.
        File.WriteAllText(Path.Combine(server, "Config", "Imlight.ini"),
            "[Database]\nEmbeddedDatabasePort = 28080\n");

        var config = ImlightConfig.TryLoad(server)!;
        var setup = LauncherDatabase.For(config, server, workspace.Path("data"), out _)!;

        Assert.InRange(setup.Port, PortAllocator.MinimumPort, PortAllocator.MaximumPort);
        Assert.True(PortAllocator.IsAvailable(setup.Port));
        Assert.NotEqual(config.EmbeddedDatabasePort, setup.Port);
    }

    [Fact]
    public void TheWaitingTimeIsTheOneImlightConfigures() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace);
        var ini = Path.Combine(server, "Config", "Imlight.ini");

        File.WriteAllText(ini, "[Database]\nEmbeddedDatabaseTimeoutTime = 120\n");
        Assert.Equal(TimeSpan.FromSeconds(120),
            LauncherDatabase.For(ImlightConfig.TryLoad(server)!, server, workspace.Path("data"), out _)!.StartupTimeout);

        // Clamped at both ends, the same way the server's own database is: nobody is served by a launcher
        // giving up after one second, or waiting an hour.
        File.WriteAllText(ini, "[Database]\nEmbeddedDatabaseTimeoutTime = 1\n");
        Assert.Equal(TimeSpan.FromSeconds(10),
            LauncherDatabase.For(ImlightConfig.TryLoad(server)!, server, workspace.Path("data"), out _)!.StartupTimeout);

        File.WriteAllText(ini, "[Database]\nEmbeddedDatabaseTimeoutTime = 9000\n");
        Assert.Equal(TimeSpan.FromSeconds(300),
            LauncherDatabase.For(ImlightConfig.TryLoad(server)!, server, workspace.Path("data"), out _)!.StartupTimeout);
    }

    [Fact]
    public void AnInstallWithNoRavenInItSaysSoRatherThanStartingNothing() {
        using var workspace = new TempWorkspace();
        var server = workspace.CreateDirectory("server");

        var setup = LauncherDatabase.For(ImlightConfig.TryLoad(server) ?? EmptyConfig(server), server,
            workspace.Path("data"), out var problem);

        Assert.Null(setup);
        Assert.NotNull(problem);
        Assert.Contains(ImlightConfig.RelativeRavenServerDirectory, problem);
    }

    [Fact]
    public void NoServerFolderIsNoDatabase() {
        using var workspace = new TempWorkspace();

        var setup = LauncherDatabase.For(EmptyConfig(workspace.Path("nowhere")), null, workspace.Path("data"), out var problem);

        Assert.Null(setup);
        Assert.Contains("No server folder", problem);
    }

    // ------------------------------------------------------------------ the launcher holding it open

    [Fact]
    public void TheLaunchersDatabaseIsInsideItsOwnRoot() {
        using var workspace = new TempWorkspace();

        var launcher = Launcher(workspace, workspace.CreateDirectory("server"));

        Assert.Equal(Path.Combine(LauncherPaths.DataRoot, "database"), launcher.LauncherDatabaseDirectory);
        Assert.StartsWith(LauncherPaths.DataRoot, launcher.LauncherDatabaseDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALauncherStoppedOnItsFirstScreenDoesNotStartTheLaunchersDatabase() {
        using var workspace = new TempWorkspace();

        // No install to run a database from, and nothing to keep notes about yet: the launcher's own data
        // base is not started behind a screen that is telling the user to install the server first.
        var launcher = Launcher(workspace, workspace.CreateDirectory("server"));

        Assert.True(launcher.RequiresImlight);
        Assert.False(launcher.IsLauncherDatabaseUp);
        Assert.False(launcher.HasLauncherDatabaseStatus);
    }

    [Fact]
    public void TheLaunchersOwnDatabaseIsStartedOnceThereIsAnImlight() {
        using var workspace = new TempWorkspace();

        // An install and a machine with the runtime the database needs, so this is about the start rather
        // than about the requirement: the host is one that does not exist, so the attempt fails at once
        // instead of leaving a real database running behind a test.
        var server = Install(workspace);

        using var machine = new MachineWith(
            Path.Combine(workspace.Root, "not-a-real-dotnet.exe"), ("Microsoft.NETCore.App", "10.0.0"));

        var launcher = Launcher(workspace, server);

        Assert.False(launcher.RequiresImlight);

        Assert.True(
            WaitFor(() => launcher.LauncherDatabaseStatus.Contains("could not be started"),
                TimeSpan.FromSeconds(10)),
            $"the launcher's own database was never attempted: \"{launcher.LauncherDatabaseStatus}\"");

        // The status is the launcher's own, not the requirement screen's: what failed is the database, and
        // what it failed for is the notes the launcher keeps in it.
        Assert.Contains("launcher's own notes", launcher.LauncherDatabaseStatus);
        Assert.False(launcher.IsLauncherDatabaseUp);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A launcher pointed at a folder, with the update check off so nothing reaches GitHub.</summary>
    private static MainWindowViewModel Launcher(TempWorkspace workspace, string serverFolder) {
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = workspace.CreateDirectory("game");
        store.Settings.ServerPath = serverFolder;
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout) {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until) {
            if (condition()) {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>
    /// A machine with a host at a path and exactly these frameworks. What a real machine has is read out of
    /// a process it starts, so the answer is decided here rather than depending on whose machine this is.
    /// </summary>
    /// <remarks>
    /// A build with no runtime beside it, deliberately: this is the machine-wide install being put to work,
    /// and a bundle found beside the test binaries would quietly decide these tests instead.
    /// </remarks>
    private sealed class MachineWith : IDisposable {

        private readonly Func<string?> _find = DotnetHost.Find;
        private readonly Func<string?> _findBundled = DotnetHost.FindBundled;
        private readonly Func<string, IReadOnlyList<DotnetFramework>?> _list = DotnetHost.ListFrameworks;

        public MachineWith(string host, params (string Name, string Version)[] frameworks) {
            DotnetHost.FindBundled = () => null;
            DotnetHost.Find = () => host;
            DotnetHost.ListFrameworks = _ => frameworks.Select(f => new DotnetFramework(f.Name, f.Version)).ToList();
            DotnetHost.Forget();
        }

        public void Dispose() {
            DotnetHost.Find = _find;
            DotnetHost.FindBundled = _findBundled;
            DotnetHost.ListFrameworks = _list;
            DotnetHost.Forget();
        }
    }

    /// <summary>
    /// An install, built piece by piece: the config that says where the database is, and the RavenDB the
    /// server ships for it to be run from.
    /// </summary>
    private static string Install(TempWorkspace workspace, bool fullServer = false) {
        var server = workspace.CreateDirectory("server");

        workspace.WriteFile(Path.Combine(server, "Config", "Imlight.ini"),
            fullServer
                ? "[Database]\nEmbeddedDatabaseUseFull = True\nEmbeddedDatabaseFullPath = ../RavenDB/Server/\n"
                : "[Global Settings]\nGameRevision = r806919.Wizard_1_610\n");

        workspace.WriteFile(Path.Combine(server, "Imlight.Director.exe"), [0]);

        if (fullServer) {
            workspace.WriteFile(Path.Combine(server, "RavenDB", "Server", "Raven.Server.dll"), [0]);
        }
        else {
            workspace.WriteFile(Path.Combine(server, "RavenDBServer", "Raven.Server.dll"), [0]);
        }

        return server;
    }

    /// <summary>
    /// A config for a folder that has none, for the checks that are about the answer rather than the file.
    /// </summary>
    private static ImlightConfig EmptyConfig(string serverPath) {
        Directory.CreateDirectory(Path.Combine(serverPath, "Config"));
        File.WriteAllText(Path.Combine(serverPath, "Config", "Imlight.ini"), string.Empty);

        return ImlightConfig.TryLoad(serverPath)!;
    }
}
