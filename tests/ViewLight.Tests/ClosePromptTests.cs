using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The close prompt exists because closing stops the server, so when it appears - and when it stays out
/// of the way - is worth pinning down.
/// </summary>
public class ClosePromptTests {

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AsksOnlyWhileAServerIsRunningAndTheUserStillWantsToBeAsked(
        bool askBeforeClosing, bool serverIsRunning, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.WarnBeforeClosing(askBeforeClosing, serverIsRunning));

    [Fact]
    public void AsksByDefault() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));

        Assert.True(store.Settings.ConfirmCloseWhenServerRunning);
    }

    [Fact]
    public void RemembersADontAskMeAgain() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.ConfirmCloseWhenServerRunning = false;
        store.Save();

        Assert.False(new LauncherSettingsStore(file).Settings.ConfirmCloseWhenServerRunning);
    }

    [Fact]
    public void KeepsAskingWhenTheStoredSettingsPredateTheSetting() {
        using var workspace = new TempWorkspace();

        // A launcher.json written before this setting existed has no key for it, and the default has to
        // be the safe one - the file below is what an older ViewLight leaves behind.
        var file = workspace.WriteFile("launcher.json", """
            {
              "ClientPath": "",
              "ServerPath": "",
              "CheckForUpdatesOnStartup": false,
              "LastSection": "Home"
            }
            """);

        Assert.True(new LauncherSettingsStore(file).Settings.ConfirmCloseWhenServerRunning);
    }

    [Fact]
    public void TurningThePromptOffCanBeUndone() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.ConfirmCloseWhenServerRunning = false;
        store.Save();

        // Settings is the only way back, so writing the preference the other way must stick too.
        var reopened = new LauncherSettingsStore(file);
        reopened.Settings.ConfirmCloseWhenServerRunning = true;
        reopened.Save();

        Assert.True(new LauncherSettingsStore(file).Settings.ConfirmCloseWhenServerRunning);
    }
}
