using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Verbose mode: the switch that shows the explanation under each control.
/// </summary>
/// <remarks>
/// The notes themselves are drawn by the window's own class in <c>LauncherStyles.axaml</c>, which a test process
/// has no window to render - so what is pinned down here is the half that decides it: off until it is turned
/// on, and remembered once it is.
/// </remarks>
public class VerboseModeTests {

    [Fact]
    public void TheLauncherDoesNotExplainItselfByDefault() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));

        // A launcher is used rather than read: the sentence under a control is worth having the first time and
        // is in the way every time after that.
        Assert.False(store.Settings.VerboseMode);
    }

    [Fact]
    public void AnOlderSettingsFileLoadsWithTheNotesHidden() {
        using var workspace = new TempWorkspace();

        // Written before the switch existed. A missing value is not an error, it is the safe choice.
        var file = workspace.WriteFile("launcher.json", """
            { "Theme": "Light" }
            """);

        Assert.False(new LauncherSettingsStore(file).Settings.VerboseMode);
    }

    [Fact]
    public void TurningItOnIsRememberedAndAppliesAtOnce() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");
        var store = new LauncherSettingsStore(file);

        var launcher = new MainWindowViewModel(store, fetchArt: false);

        Assert.False(launcher.VerboseMode);

        var said = new List<string>();
        launcher.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? string.Empty);

        launcher.VerboseMode = true;

        Assert.True(launcher.VerboseMode);
        Assert.True(store.Settings.VerboseMode);
        Assert.Contains(nameof(MainWindowViewModel.VerboseMode), said);

        // Saved where the next start reads it, and off again is saved the same way.
        Assert.True(new LauncherSettingsStore(file).Settings.VerboseMode);

        launcher.VerboseMode = false;
        Assert.False(new LauncherSettingsStore(file).Settings.VerboseMode);
    }
}
