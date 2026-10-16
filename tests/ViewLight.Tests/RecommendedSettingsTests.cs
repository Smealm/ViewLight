using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The one-click preset in Settings. It is deliberately narrow: the starter kit and the admin account, and
/// nothing the game itself makes easier - skipping the tutorial is a button on the character-creation
/// screen, while turning it off here costs every new character its school's first spell and its equipped
/// starter kit.
/// </summary>
public class RecommendedSettingsTests {

    [Fact]
    public void ThePresetWritesTheStarterKitAndLeavesTheTutorialAlone() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace, tutorialDisabled: false);

        Assert.False(launcher.TutorialDisabled);

        launcher.ApplyStarterPresetCommand.Execute(null);

        // The kit is narrowed, the credentials are written, and the one setting that is not the preset's to
        // decide is exactly as it was.
        Assert.Equal("87256,126983", launcher.DefaultItemsText);
        Assert.Equal([87256u, 126983u], launcher.StarterKit.Select(item => item.TemplateId));
        Assert.False(launcher.TutorialDisabled);

        var written = ImlightConfig.TryLoad(workspace.Path("server"))!;
        Assert.Equal("87256,126983", written.DefaultItems);
        Assert.False(written.TutorialDisabled);
        Assert.Equal("admin", written.AdminUsername);

        // And it says so, in words that name the preset rather than the setting it used to change.
        var lines = launcher.Log.Blocks.SelectMany(block => block.Lines).ToList();

        Assert.Contains(lines, entry => entry.Message.Contains("Applied recommended settings"));
        Assert.Contains(lines, entry => entry.Message.Contains("Skipping the tutorial is left as it is"));
    }

    [Fact]
    public void ATutorialThatWasAlreadyOffStaysOff() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace, tutorialDisabled: true);

        Assert.True(launcher.TutorialDisabled);

        launcher.ApplyStarterPresetCommand.Execute(null);

        // Not a setting the preset turns on - and not one it turns off either: a server whose owner chose to
        // skip the tutorial keeps that choice.
        Assert.True(launcher.TutorialDisabled);
        Assert.True(ImlightConfig.TryLoad(workspace.Path("server"))!.TutorialDisabled);
    }

    /// <summary>A launcher pointed at a server whose config already holds the admin account.</summary>
    private static MainWindowViewModel Launcher(TempWorkspace workspace, bool tutorialDisabled) {
        var serverFolder = workspace.CreateDirectory("server");
        workspace.WriteFile("server/Config/Imlight.ini",
            "[Character]\r\n"
            + $"TutorialDisabled = {(tutorialDisabled ? "true" : "false")}\r\n"
            + "DefaultItems = 1317124,1317234,284071\r\n\r\n"
            + "[Database]\r\n"
            + "AdminAccountUsername = admin\r\n"
            + "AdminAccountPassword = compsci\r\n");

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = workspace.CreateDirectory("game");
        store.Settings.ServerPath = serverFolder;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }
}
