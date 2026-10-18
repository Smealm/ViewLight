using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Settings, in pages, and the one button on them that cannot be taken back.
/// </summary>
/// <remarks>
/// The pages exist because the list of cards had grown past what one scroll can hold, so what is checked
/// here is that each page is the one that was asked for and that the second level only appears where it
/// means something. The reset is checked from the outside - it asks first, it says what it did, and it
/// never leaves the launcher in its own "resetting" state - because everything below it is
/// <see cref="LauncherReset"/>'s business and is pinned down on its own.
/// </remarks>
public class SettingsTabsTests {

    [Fact]
    public void SettingsOpensOnTheServerAndItsOwnHalves() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        Assert.True(launcher.IsServerSettings);
        Assert.True(launcher.IsImlightSettings);
        Assert.True(launcher.HasSettingsSubTabs);

        Assert.False(launcher.IsClientSettings);
        Assert.False(launcher.IsLauncherSettings);
        Assert.False(launcher.IsDataSettings);
        Assert.False(launcher.IsAuroriumSettings);

        // The client's headings are not on screen on the server's page, even though the client's own pick is
        // remembered - they are headings of a page that is not open.
        Assert.False(launcher.IsGameFolderSettings);
        Assert.False(launcher.IsCharacterSettings);
    }

    [Fact]
    public void APageOnlyShowsItsOwnHeadings() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.OpenClientSettingsTabCommand.Execute("Characters");
        launcher.OpenSettingsTabCommand.Execute("Client");

        Assert.True(launcher.IsCharacterSettings);
        Assert.False(launcher.IsGameFolderSettings);
        Assert.False(launcher.IsImlightSettings);
        Assert.False(launcher.IsAuroriumSettings);

        // The patch server's half of the server page is chosen while the client's page is open: it waits for
        // the page it belongs to rather than standing in the middle of the client's cards.
        launcher.OpenServerSettingsTabCommand.Execute("Aurorium");

        Assert.False(launcher.IsAuroriumSettings);
        Assert.True(launcher.IsCharacterSettings);

        launcher.OpenSettingsTabCommand.Execute("Server");

        Assert.True(launcher.IsAuroriumSettings);
        Assert.False(launcher.IsImlightSettings);
        Assert.False(launcher.IsCharacterSettings);

        // And the launcher's own page, which has no headings at all: nothing from either of the two above
        // is left on screen.
        launcher.OpenSettingsTabCommand.Execute("Launcher");

        Assert.False(launcher.IsImlightSettings);
        Assert.False(launcher.IsAuroriumSettings);
        Assert.False(launcher.IsGameFolderSettings);
        Assert.False(launcher.IsCharacterSettings);
        Assert.False(launcher.HasSettingsSubTabs);
    }

    [Fact]
    public void MovingThePageTellsTheHeadingsTheyHaveMoved() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        var changed = new List<string?>();
        launcher.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        launcher.OpenSettingsTabCommand.Execute("Client");

        // The sub-page the user picked has not moved, so nothing else would have told the screen - and a
        // heading that is never told it has gone is a heading that stays on screen over the next page's
        // cards.
        Assert.Contains(nameof(MainWindowViewModel.IsGameFolderSettings), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsImlightSettings), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsCharacterSettings), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsAuroriumSettings), changed);
    }

    [Fact]
    public void EachPageIsNamedAndReachedByName() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.OpenSettingsTabCommand.Execute("client");

        Assert.True(launcher.IsClientSettings);
        Assert.False(launcher.IsServerSettings);

        // The second level belongs to the server and the client, and is not drawn anywhere else.
        Assert.True(launcher.HasSettingsSubTabs);

        launcher.OpenSettingsTabCommand.Execute("Data");

        Assert.True(launcher.IsDataSettings);
        Assert.False(launcher.HasSettingsSubTabs);

        // A name this build does not know leaves the page where it was rather than landing on nothing.
        launcher.OpenSettingsTabCommand.Execute("PatchServer");

        Assert.True(launcher.IsDataSettings);
        launcher.OpenSettingsTabCommand.Execute(null);

        Assert.True(launcher.IsDataSettings);
    }

    [Fact]
    public void TheSubPagesAreReachedByNameToo() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        launcher.OpenServerSettingsTabCommand.Execute("aurorium");

        Assert.True(launcher.IsAuroriumSettings);
        Assert.False(launcher.IsImlightSettings);

        // The client's page is not the one that is open, so its heading is remembered rather than shown.
        launcher.OpenClientSettingsTabCommand.Execute("Characters");

        Assert.False(launcher.IsCharacterSettings);
        Assert.True(launcher.IsAuroriumSettings);

        launcher.OpenSettingsTabCommand.Execute("Client");

        Assert.True(launcher.IsCharacterSettings);
        Assert.False(launcher.IsGameFolderSettings);

        launcher.OpenClientSettingsTabCommand.Execute("nonsense");

        Assert.True(launcher.IsCharacterSettings);
    }

    [Fact]
    public void TheResetNamesBothDatabasesAndWhatItKeeps() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        // A launcher with no server here has one database to delete, and says so rather than listing a
        // server's that is not there.
        Assert.NotEmpty(launcher.ResetTargetNames);
        Assert.Contains(launcher.ResetTargetNames, name => name.Contains("ViewLight's own database"));
        Assert.DoesNotContain(launcher.ResetTargetNames, name => name.Contains("server's database"));

        Assert.Contains("Imlight install", launcher.ResetKeepsText);

        // With the server stopped and nothing syncing, there is nothing in the way - and nothing to say.
        Assert.True(launcher.CanResetNow);
        Assert.False(launcher.IsResetBlocked);
        Assert.Equal(string.Empty, launcher.ResetBlockedText);

        // And the cloud copy is not offered when nobody is signed in, because there is nothing this launcher
        // could delete up there.
        Assert.False(launcher.CanWipeCloudCopy);
    }

    [Fact]
    public void ARunningServerStopsTheResetWithAReason() {
        Assert.Contains("Stop the server", LauncherReset.WhyNot(true, false)!);
        Assert.Contains("sync is running", LauncherReset.WhyNot(false, true)!);
        Assert.Null(LauncherReset.WhyNot(false, false));
    }

    [Fact]
    public async Task TheResetAsksFirstAndDoesNothingWhenItIsCalledOff() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        var asked = new List<IReadOnlyList<string>>();

        launcher.ConfirmResetAsync = (targets, _) => {
            asked.Add(targets);
            return Task.FromResult<WipeRequest?>(null);
        };

        await launcher.StartResetCommand.ExecuteAsync(null);

        // Asked once, with the list the launcher would act on - and nothing was deleted.
        var named = Assert.Single(asked);
        Assert.Equal(launcher.ResetTargetNames, named);
        Assert.False(launcher.IsResetting);
        Assert.Contains("called off", launcher.Log.Blocks[^1].Body);
    }

    [Fact]
    public async Task TheResetDeletesWhatWasNamedAndLeavesTheLauncherUsable() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        // Something for it to delete, where the launcher's own database would be.
        var database = LauncherDatabase.DataDirectoryFor(LauncherPaths.DataRoot);
        Directory.CreateDirectory(database);
        File.WriteAllText(Path.Combine(database, "a-profile.json"), "{}");

        WipeRequest? answer = null;

        launcher.ConfirmResetAsync = (_, cloudPossible) => {
            // No sign-in in a test, so there is no cloud copy to offer to delete - and the answer still says
            // what the user wanted, because the window is what decides what is possible.
            Assert.False(cloudPossible);
            answer = new WipeRequest(IncludeCloudBackup: true);
            return Task.FromResult<WipeRequest?>(answer);
        };

        await launcher.StartResetCommand.ExecuteAsync(null);

        Assert.NotNull(answer);
        Assert.False(Directory.Exists(database));

        // The launcher is not left thinking a reset is in progress, and it says what it did and what is left
        // to do about it - the server is the user's to start, not the launcher's.
        Assert.False(launcher.IsResetting);
        Assert.True(launcher.IsEditingAllowed);

        var said = string.Join("\n", launcher.Log.Blocks.Select(block => block.Body));

        Assert.Contains("Starting over", said);
        Assert.Contains("new, empty database", said);
    }

    [Fact]
    public void TheResetBeingUnderWayStopsEverythingElseBeingEdited() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        Assert.True(launcher.IsEditingAllowed);

        launcher.IsResetting = true;

        // The databases are being deleted underneath the screens that read them, so the screens are off -
        // the same rule a cloud sync is under, for the same reason.
        Assert.False(launcher.IsEditingAllowed);
        Assert.True(launcher.HasResetNotice);
    }

    private static MainWindowViewModel Launcher(TempWorkspace workspace) {
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }
}
