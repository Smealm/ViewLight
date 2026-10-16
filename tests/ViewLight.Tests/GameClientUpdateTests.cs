using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The game in the launcher's own folder, and the two ways of bringing it up to the build installed elsewhere.
/// </summary>
/// <remarks>
/// Two methods, one result: the tests check both produce a folder equal to the source, and check the numbers
/// they report differ in the way the choice is about - one writes the whole game, the other writes only what
/// changed. What they are compared against is the two trees themselves rather than a count, because "the copy
/// is the source now" is the property that matters.
/// </remarks>
public class GameClientUpdateTests {

    // ------------------------------------------------------------------ bringing the copy up to date

    [Fact]
    public async Task OnlyTheFilesThatDifferAreWritten() {
        using var workspace = new TempWorkspace();

        var (source, copy) = TwoBuilds(workspace);

        var result = await GameClientUpdateService.UpdateAsync(source, copy, ClientUpdateMethod.Mirror);

        Assert.True(result.Success);

        // Three of the five files differ: the archive is a different build, the newer build adds a file, and
        // the revision the client states for itself is part of the build. The other two - the executable and
        // the dll it loads - are identical, and reading them is all that happens to them.
        Assert.Equal(3, result.Written);
        Assert.Equal(Files(source).Count - 2, result.Written);

        // And what the newer build no longer has goes.
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(copy, "Data", "GameData", "gone.wad")));

        AssertSameFiles(source, copy);
    }

    [Fact]
    public async Task CopyingTheGameInFreshWritesEverythingAndLeavesNothingBehind() {
        using var workspace = new TempWorkspace();

        var (source, copy) = TwoBuilds(workspace);
        var wasThere = Files(copy).Count;

        var result = await GameClientUpdateService.UpdateAsync(source, copy, ClientUpdateMethod.Replace);

        Assert.True(result.Success);

        // Every file the source has is written, including the ones that were already identical - that is what
        // this method costs - and every file the old copy had is cleared out first, which is the other half.
        Assert.Equal(Files(source).Count, result.Written);
        Assert.Equal(wasThere, result.Removed);

        AssertSameFiles(source, copy);
    }

    [Fact]
    public async Task AnUpdateWithNothingToDoWritesNothing() {
        using var workspace = new TempWorkspace();

        var server = workspace.CreateDirectory("install");
        Game(server, "r900000.Wizard_1_610");

        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r900000.Wizard_1_610");

        var result = await GameClientUpdateService.UpdateAsync(server, copy, ClientUpdateMethod.Mirror);

        // The comparison is the point of this method, and this is it working: a folder that already agrees
        // with the source is not written to at all.
        Assert.True(result.Success);
        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.Removed);
        Assert.Contains("already the build", result.Message);
    }

    [Fact]
    public async Task AnUpdateThatIsStoppedPartWayThroughSaysSoRatherThanFailing() {
        using var workspace = new TempWorkspace();

        var (source, copy) = TwoBuilds(workspace);

        // What the Cancel button does underneath: the wait is already over before the work starts, which is
        // the one case a test can drive without racing a real multi-gigabyte write. It has to come back as an
        // answer like any other - the command that ran it is a progress bar, not an exception handler.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await GameClientUpdateService.UpdateAsync(
            source, copy, ClientUpdateMethod.Mirror, cancellationToken: cancelled.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Message);
    }

    [Fact]
    public async Task AFolderThatWasDroppedByTheNewerBuildDoesNotLinger() {
        using var workspace = new TempWorkspace();

        var server = workspace.CreateDirectory("install");
        Game(server, "r900000.Wizard_1_610");

        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");
        workspace.WriteFile("package/game/Data/Old/leftover.wad", "gone in the newer build");

        await GameClientUpdateService.UpdateAsync(server, copy, ClientUpdateMethod.Mirror);

        // The file goes, and so does the folder it was the only thing in: a build that drops a folder should
        // not leave its skeleton in the copy for ever.
        Assert.False(Directory.Exists(Path.Combine(copy, "Data", "Old")));
    }

    [Fact]
    public async Task UpdatingFromSomethingThatIsNotAGameIsRefused() {
        using var workspace = new TempWorkspace();

        var notAGame = workspace.CreateDirectory("downloads");
        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");

        var result = await GameClientUpdateService.UpdateAsync(notAGame, copy, ClientUpdateMethod.Mirror);

        Assert.False(result.Success);
        Assert.Contains("does not look like a Wizard101 install", result.Message);

        // Refused before anything was touched: the copy is still the game it was.
        Assert.True(File.Exists(Path.Combine(copy, "bin", "WizardGraphicalClient.exe")));
    }

    [Fact]
    public async Task AFolderIsNotUpdatedFromItselfOrFromSomethingItContains() {
        using var workspace = new TempWorkspace();

        var game = workspace.CreateDirectory("game");
        Game(game, "r806919.Wizard_1_610");

        var itself = await GameClientUpdateService.UpdateAsync(game, game, ClientUpdateMethod.Replace);
        Assert.True(itself.Success);
        Assert.Equal(0, itself.Written);

        // A game folder and a folder inside it: writing that would read the files it is writing, so it is
        // refused before anything is looked at - and the source is a real install here, so what is being
        // tested is the nesting rather than the recognisability of the folder.
        var nested = await GameClientUpdateService.UpdateAsync(
            game, Path.Combine(game, "bin"), ClientUpdateMethod.Mirror);

        Assert.False(nested.Success);
        Assert.Contains("inside the other", nested.Message);
    }

    // ------------------------------------------------------------------ the question a start asks

    [Fact]
    public void ANewerBuildOnTheMachineIsOffered() {
        using var workspace = new TempWorkspace();

        var install = workspace.CreateDirectory("install");
        Game(install, "r900000.Wizard_1_610");

        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");

        var check = PortableClientUpdate.Check(copy, install);

        Assert.True(check.Available);
        Assert.Equal("r806919.Wizard_1_610", check.CopyBuild);
        Assert.Equal("r900000.Wizard_1_610", check.SourceBuild);

        // The card says which two builds are being compared and where the newer one is, because "you are out
        // of date" without either of those is not something a person can act on.
        var sentence = PortableClientUpdate.Describe(check);

        Assert.Contains("r806919.Wizard_1_610", sentence);
        Assert.Contains("r900000.Wizard_1_610", sentence);
        Assert.Contains(install, sentence);
    }

    [Fact]
    public void ACopyThatIsTheSameBuildOrNewerIsNotOffered() {
        using var workspace = new TempWorkspace();

        var install = workspace.CreateDirectory("install");
        Game(install, "r806919.Wizard_1_610");

        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");

        Assert.False(PortableClientUpdate.Check(copy, install).Available);

        // And a copy ahead of the install is not something to nag about either: an update would take it
        // backwards, and nothing here is going to do that.
        Game(copy, "r900000.Wizard_1_610");
        Assert.False(PortableClientUpdate.Check(copy, install).Available);
    }

    [Fact]
    public void ABuildThatCannotBeReadIsNotAnOutOfDateCopy() {
        using var workspace = new TempWorkspace();

        var install = workspace.CreateDirectory("install");
        Game(install, "r900000.Wizard_1_610");

        // A game folder with no revision anywhere - the client states its own build once it has run, and a
        // copy that has never been started has not yet.
        var copy = workspace.CreateDirectory("package/game");
        workspace.WriteFile("package/game/bin/WizardGraphicalClient.exe", [0]);

        var check = PortableClientUpdate.Check(copy, install);

        Assert.False(check.Available);
        Assert.Contains("could not be read", check.Reason);
    }

    [Fact]
    public void AFolderWithNoGameInItHasNothingToCompare() {
        using var workspace = new TempWorkspace();

        var install = workspace.CreateDirectory("install");
        Game(install, "r900000.Wizard_1_610");

        // The launcher's own folder with no game copied into it yet: the first thing it needs is a copy, not
        // an update, and the card for that is a different one.
        var empty = workspace.CreateDirectory("empty-folder");

        var check = PortableClientUpdate.Check(empty, install);

        Assert.False(check.Available);
        Assert.Contains("no game to compare", check.Reason);

        // And a remembered folder that is no longer a game folder is passed over rather than trusted: it is
        // skipped when the copy is being asked what to compare itself against.
        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");

        var movedAway = workspace.Path("moved-away");
        Assert.NotEqual(Path.GetFullPath(movedAway), PortableClientUpdate.SourceFor(copy, movedAway));
    }

    [Fact]
    public void TheInstallOfThisMachineIsFoundWhenNothingIsRemembered() {
        using var workspace = new TempWorkspace();

        var copy = workspace.CreateDirectory("package/game");
        Game(copy, "r806919.Wizard_1_610");

        // The remembered folder is the first answer - and it is used as given rather than searched for, which
        // is what makes the comparison stable across starts.
        var install = workspace.CreateDirectory("install");
        Game(install, "r900000.Wizard_1_610");

        Assert.Equal(Path.GetFullPath(install), PortableClientUpdate.SourceFor(copy, install));

        // Nothing remembered, and no game of one's own in the copy's own folder to mistake for the install.
        Assert.DoesNotContain(Path.GetFullPath(copy), PortableClientUpdate.SourceFor(copy, null) ?? string.Empty);
    }

    // ------------------------------------------------------------------ the card, and the button

    [Fact]
    public async Task APortableLauncherOffersTheUpdateAndThenDoesIt() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("on-a-stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            PortableMode.Apply(portableRoot);

            // The machine's own install, and the copy the portable launcher plays from.
            var install = workspace.CreateDirectory("steam/steamapps/common/Wizard101");
            Game(install, "r900000.Wizard_1_610");

            var copy = LauncherPaths.GameDirectory;
            Game(copy, "r806919.Wizard_1_610");

            var store = new LauncherSettingsStore(LauncherPaths.SettingsFile);
            store.Settings.ClientPath = copy;
            store.Settings.ServerPath = workspace.CreateDirectory("server");
            store.Settings.CheckForUpdatesOnStartup = false;
            store.Settings.PortableGameSourcePath = install;
            store.Save();

            var launcher = new MainWindowViewModel(store, fetchArt: false);

            // What the start says: the card is up, and it names both builds.
            Assert.True(launcher.HasPortableClientUpdate);
            Assert.True(launcher.ShowPortableClientUpdateCard);
            Assert.Contains("r806919.Wizard_1_610", launcher.PortableUpdateSummary);
            Assert.Contains("r900000.Wizard_1_610", launcher.PortableUpdateSummary);
            Assert.Equal(Path.GetFullPath(install), launcher.PortableUpdateSource);

            // The reading method is the one chosen until somebody chooses otherwise, and both notes say what
            // the choice costs.
            Assert.True(launcher.PortableUpdateOnlyChanges);
            Assert.Contains("processor time", launcher.PortableUpdateMethodNote);

            await launcher.UpdatePortableClientCommand.ExecuteAsync(null);

            // The copy is the install now, and there is nothing left to offer - the next start has nothing to
            // say about it either, which is the point of doing it.
            AssertSameFiles(install, copy);
            Assert.False(launcher.HasPortableClientUpdate);

            // The card itself stays up to report what it did, rather than vanishing the moment it worked: a
            // multi-gigabyte write that leaves no trace on screen would leave somebody wondering if it ran.
            Assert.True(launcher.ShowPortableClientUpdateCard);
            Assert.True(launcher.HasPortableUpdateMessage);
            Assert.Contains("Updated", launcher.PortableUpdateMessage);

            Assert.False(PortableClientUpdate.Check(copy, install).Available);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    [Fact]
    public void TheMethodRadiosWorkInBothDirections() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        // Both radios are bound to computed properties, the way the cloud sign-in's are. A get-only one would
        // leave the method impossible to change once the other had been picked, and a build does not catch it -
        // a binding is compiled against the type, not against the setter - so the setters are asserted here.
        foreach (var name in new[] { nameof(launcher.PortableUpdateOnlyChanges),
                                     nameof(launcher.PortableUpdateReplacesEverything) }) {
            var property = typeof(MainWindowViewModel).GetProperty(name);

            Assert.NotNull(property);
            Assert.True(property!.CanRead);
            Assert.True(property.CanWrite);
        }

        // Reading both copies is what it does until somebody says otherwise, and the note says what that costs.
        Assert.True(launcher.PortableUpdateOnlyChanges);
        Assert.False(launcher.PortableUpdateReplacesEverything);
        Assert.Contains("processor time", launcher.PortableUpdateMethodNote);

        launcher.PortableUpdateReplacesEverything = true;

        Assert.True(launcher.PortableUpdateReplacesEverything);
        Assert.False(launcher.PortableUpdateOnlyChanges);
        Assert.Contains("written again", launcher.PortableUpdateMethodNote);

        // And back again, which is the half a one-way setter would have lost.
        launcher.PortableUpdateOnlyChanges = true;

        Assert.True(launcher.PortableUpdateOnlyChanges);
        Assert.False(launcher.PortableUpdateReplacesEverything);
    }

    [Fact]
    public void TheOfferCanBePutAwayWithoutTouchingTheCopy() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("on-a-stick");
        File.WriteAllText(Path.Combine(portableRoot, PortableMode.MarkerFileName), string.Empty);

        var original = LauncherPaths.DataRoot;

        try {
            PortableMode.Apply(portableRoot);

            var install = workspace.CreateDirectory("install");
            Game(install, "r900000.Wizard_1_610");

            var copy = LauncherPaths.GameDirectory;
            Game(copy, "r806919.Wizard_1_610");

            var store = new LauncherSettingsStore(LauncherPaths.SettingsFile);
            store.Settings.ClientPath = copy;
            store.Settings.ServerPath = workspace.CreateDirectory("server");
            store.Settings.CheckForUpdatesOnStartup = false;
            store.Settings.PortableGameSourcePath = install;
            store.Save();

            var launcher = new MainWindowViewModel(store, fetchArt: false);
            Assert.True(launcher.HasPortableClientUpdate);

            launcher.DismissPortableClientUpdateCommand.Execute(null);

            // Not now means not now - and nothing was written, so the copy is exactly as it was.
            Assert.False(launcher.HasPortableClientUpdate);
            Assert.Contains("r806919.Wizard_1_610", ClientBuildReader.Read(copy));
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    [Fact]
    public void ANormalLauncherIsNeverAskedAboutACopyItDoesNotHave() {
        using var workspace = new TempWorkspace();
        var portableRoot = workspace.CreateDirectory("on-a-stick");

        var original = LauncherPaths.DataRoot;
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        try {
            // The same folders, and portable mode off: nothing here is the launcher's own copy to update.
            var install = workspace.CreateDirectory("install");
            Game(install, "r806919.Wizard_1_610");

            store.Settings.ClientPath = install;
            store.Settings.PortableGameSourcePath = install;
            store.Save();

            Assert.False(PortableMode.Apply(portableRoot));

            var launcher = new MainWindowViewModel(store, fetchArt: false);

            Assert.False(launcher.HasPortableClientUpdate);
            Assert.False(launcher.ShowPortableClientUpdateCard);
        }
        finally {
            LauncherPaths.DataRoot = original;
            PortableMode.Apply(workspace.Root);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// An installed build and the launcher's older copy of it: the same client, a different archive, a file
    /// the newer build adds and one it no longer ships.
    /// </summary>
    private static (string Source, string Copy) TwoBuilds(TempWorkspace workspace) {
        var source = workspace.CreateDirectory("install");
        var copy = workspace.CreateDirectory("package/game");

        Game(source, "r900000.Wizard_1_610");
        Game(copy, "r806919.Wizard_1_610");

        // The archive is a different build, byte for byte.
        workspace.WriteFile("install/Data/GameData/root.wad", "the newer archive");
        workspace.WriteFile("package/game/Data/GameData/root.wad", "the older archive");

        // Added by the newer build, dropped by it, and identical in both.
        workspace.WriteFile("install/Data/GameData/added.wad", "new in the newer build");
        workspace.WriteFile("package/game/Data/GameData/gone.wad", "gone in the newer build");
        workspace.WriteFile("install/bin/thesame.dll", "identical in both builds");
        workspace.WriteFile("package/game/bin/thesame.dll", "identical in both builds");

        return (source, copy);
    }

    /// <summary>A game folder that is recognisable as one, with a build of its own in it.</summary>
    private static void Game(string folder, string build) {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(Path.Combine(folder, "Data", "GameData"));

        File.WriteAllBytes(Path.Combine(folder, GameFolderLocator.RelativeClientExecutable), [0]);
        File.WriteAllText(Path.Combine(folder, ClientBuildReader.RelativeRevisionFile), build);
    }

    /// <summary>Every file under a folder, by relative path.</summary>
    private static List<string> Files(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether two folders hold the same files, with the same contents - which is the whole point of an
    /// update, and the one thing a count cannot show.
    /// </summary>
    private static void AssertSameFiles(string expected, string actual) {
        Assert.Equal(Files(expected), Files(actual));

        foreach (var relative in Files(expected)) {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(expected, relative)),
                File.ReadAllBytes(Path.Combine(actual, relative)));
        }
    }
}
