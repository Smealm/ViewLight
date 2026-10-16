using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Portable mode is the difference between a launcher that works wherever it is put and one that only
/// works where it was installed, so the switch and the path rules behind it are pinned down here.
/// </summary>
public class PortableModeTests {

    // ------------------------------------------------------------------ the switch

    [Fact]
    public void PointsTheLauncherAtTheFolderItRunsFrom() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("on-a-stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            Assert.True(PortableMode.Apply(portableRoot));

            // Everything else - settings, server, art, game - is derived from this one folder.
            Assert.Equal(portableRoot, LauncherPaths.DataRoot);
            Assert.Equal(Path.Combine(portableRoot, "game"), LauncherPaths.GameDirectory);
            Assert.Equal(Path.Combine(portableRoot, "server"), LauncherPaths.DefaultServerPath);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    [Fact]
    public void LeavesANormalLauncherWhereItWas() {
        using var workspace = new TempWorkspace();
        var installed = workspace.CreateDirectory("installed-here");
        var original = LauncherPaths.DataRoot;

        try {
            Assert.False(PortableMode.Apply(installed));
            Assert.Equal(original, LauncherPaths.DataRoot);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    // ------------------------------------------------------------------ what gets written down

    [Fact]
    public void KeepsAFolderInsideTheLauncherRelativeToIt() {
        const string root = @"C:\stick";

        Assert.Equal("game", LauncherPaths.ToStoredPath(@"C:\stick\game", portable: true, root));
        Assert.Equal("server", LauncherPaths.ToStoredPath(@"C:\stick\server", portable: true, root));
    }

    [Fact]
    public void LeavesAFolderOutsideTheLauncherAbsolute() {
        const string root = @"C:\stick";

        // An install somewhere else genuinely is somewhere else: no relative path describes it, and
        // pretending otherwise would break the moment the launcher moved.
        Assert.Equal(@"D:\Games\Wizard101",
            LauncherPaths.ToStoredPath(@"D:\Games\Wizard101", portable: true, root));
        Assert.Equal(@"C:\Other\Wizard101",
            LauncherPaths.ToStoredPath(@"C:\Other\Wizard101", portable: true, root));
    }

    [Fact]
    public void StoresExactlyWhatItWasGivenWhenNotPortable() {
        Assert.Equal(@"C:\anywhere\game",
            LauncherPaths.ToStoredPath(@"C:\anywhere\game", portable: false, root: @"C:\stick"));
    }

    [Fact]
    public void ResolvesARelativeFolderAgainstTheLauncher() {
        Assert.Equal(Path.Combine(@"C:\stick", "game"),
            LauncherPaths.ResolveStoredPath("game", portable: true, root: @"C:\stick"));
    }

    [Fact]
    public void LeavesAnAbsoluteFolderAloneWhenResolving() {
        Assert.Equal(@"D:\Games\Wizard101",
            LauncherPaths.ResolveStoredPath(@"D:\Games\Wizard101", portable: true, root: @"C:\stick"));
        Assert.Equal("game",
            LauncherPaths.ResolveStoredPath("game", portable: false, root: @"C:\stick"));
    }

    [Fact]
    public void ARelativeFolderSurvivesTheRoundTrip() {
        const string root = @"C:\stick";
        var stored = LauncherPaths.ToStoredPath(Path.Combine(root, "game"), portable: true, root);

        Assert.Equal("game", stored);
        Assert.Equal(Path.Combine(root, "game"), LauncherPaths.ResolveStoredPath(stored, portable: true, root));
    }

    // ------------------------------------------------------------------ moving the folder

    [Fact]
    public void APortableLauncherWritesItsOwnFoldersRelativeToItself() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            PortableMode.Apply(portableRoot);

            var file = Path.Combine(portableRoot, "launcher.json");
            var store = new LauncherSettingsStore(file);
            store.Settings.ClientPath = LauncherPaths.GameDirectory;
            store.Settings.ServerPath = LauncherPaths.DefaultServerPath;
            store.Save();

            // What is on disk is relative, which is what makes the folder moveable...
            var written = File.ReadAllText(file);
            Assert.Contains("\"ClientPath\": \"game\"", written);
            Assert.Contains("\"ServerPath\": \"server\"", written);

            // ...and what comes back is a real path again, which is what the rest of the launcher needs.
            var reopened = new LauncherSettingsStore(file).Settings;
            Assert.Equal(LauncherPaths.GameDirectory, reopened.ClientPath);
            Assert.Equal(LauncherPaths.DefaultServerPath, reopened.ServerPath);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    // ------------------------------------------------------------------ copying the game in

    [Fact]
    public async Task AFinishedCopyIsReadWhereItLandedAndIsNotOfferedAgain() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("on-a-stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            PortableMode.Apply(portableRoot);

            // The machine's install, and the launcher's own game folder - which the launcher is already
            // pointed at, because that is what a fresh portable launcher starts out doing.
            var install = workspace.CreateDirectory("steam/steamapps/common/Wizard101");
            workspace.WriteFile(Path.Combine("steam/steamapps/common/Wizard101",
                GameFolderLocator.RelativeClientExecutable), "stub");
            workspace.WriteFile(Path.Combine("steam/steamapps/common/Wizard101",
                GameFolderLocator.RelativeRootWad), "wad");

            var store = new LauncherSettingsStore(LauncherPaths.SettingsFile);
            store.Settings.ClientPath = LauncherPaths.GameDirectory;
            store.Settings.ServerPath = workspace.CreateDirectory("server");
            store.Settings.CheckForUpdatesOnStartup = false;
            store.Save();

            var launcher = new MainWindowViewModel(store, fetchArt: false);
            launcher.PortableGameSource = install;

            // Nothing copied yet: the card asks, and its heading is the ask.
            Assert.True(launcher.PortableNeedsClient);
            Assert.True(launcher.ShowPortableClientPrompt);
            Assert.Equal("Copy your game into this folder", launcher.PortableClientCardTitle);

            await launcher.CopyGameIntoPortableCommand.ExecuteAsync(null);

            Assert.True(launcher.HasCopyGameMessage);

            // The folder the launcher plays from is the one it already pointed at - a portable launcher
            // plays out of its own - so the copy has to be noticed rather than announced by the path
            // changing. It was not noticed, and a finished copy went on being read as a missing game in
            // Settings and on Home until the folder was set by hand.
            Assert.Equal(LauncherPaths.GameDirectory, launcher.ClientPath);
            Assert.NotNull(launcher.ClientExecutablePath);
            Assert.Contains("both found", launcher.GameFolderStatus);
            Assert.Contains("Found", launcher.RootWadStatus);

            // And the asking is over: there is a game in the folder, so the card reports what happened
            // rather than offering to do it again.
            Assert.False(launcher.PortableNeedsClient);
            Assert.False(launcher.ShowPortableClientPrompt);
            Assert.Equal("Your game is already in this folder", launcher.PortableClientCardTitle);
            Assert.True(launcher.ShowPortableClientCard);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    [Fact]
    public void APortableLauncherStartsWithItsOwnFoldersRatherThanSomewhereElse() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            PortableMode.Apply(portableRoot);

            // No settings file yet: the defaults are inside the folder, not a detected install elsewhere.
            var fresh = new LauncherSettingsStore(Path.Combine(portableRoot, "launcher.json")).Settings;

            Assert.Equal(LauncherPaths.GameDirectory, fresh.ClientPath);
            Assert.Equal(LauncherPaths.DefaultServerPath, fresh.ServerPath);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }
}
