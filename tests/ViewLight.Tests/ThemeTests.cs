using Avalonia.Styling;
using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The theme is a stored preference that decides a live setting, so both halves are pinned down: what
/// an unknown or missing value turns into, and that a choice survives a restart.
/// </summary>
public class ThemeTests {

    [Theory]
    [InlineData("Light", ThemeChoices.Light)]
    [InlineData("light", ThemeChoices.Light)]
    [InlineData("  Light  ", ThemeChoices.Light)]
    [InlineData("Dark", ThemeChoices.Dark)]
    [InlineData("DARK", ThemeChoices.Dark)]
    [InlineData("System", ThemeChoices.System)]
    [InlineData("system", ThemeChoices.System)]
    [InlineData("", ThemeChoices.System)]
    [InlineData("   ", ThemeChoices.System)]
    [InlineData(null, ThemeChoices.System)]
    [InlineData("Banana", ThemeChoices.System)]
    public void AnythingUnusableFollowsTheDesktop(string? stored, string expected)
        => Assert.Equal(expected, MainWindowViewModel.NormaliseTheme(stored));

    [Fact]
    public void SystemMeansTheFrameworkDefaultRegardlessOfPlatform() {
        // ThemeVariant.Default is Avalonia's "ask the desktop", which is what makes this behave the
        // same on Windows and on a Linux build.
        Assert.Same(ThemeVariant.Default, MainWindowViewModel.VariantFor(ThemeChoices.System));
        Assert.Same(ThemeVariant.Light, MainWindowViewModel.VariantFor("Light"));
        Assert.Same(ThemeVariant.Dark, MainWindowViewModel.VariantFor("Dark"));

        // A value written by some other version is not an error, it is the safe choice.
        Assert.Same(ThemeVariant.Default, MainWindowViewModel.VariantFor("Midnight"));
    }

    [Fact]
    public void FollowsTheDesktopByDefault() {
        using var workspace = new TempWorkspace();

        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));

        Assert.Equal(ThemeChoices.System, store.Settings.Theme);
    }

    [Fact]
    public void RemembersAChosenTheme() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.Theme = ThemeChoices.Light;
        store.Save();

        Assert.Equal(ThemeChoices.Light, new LauncherSettingsStore(file).Settings.Theme);
    }

    [Fact]
    public void KeepsAThemeWrittenByAFutureVersionRatherThanCrashing() {
        using var workspace = new TempWorkspace();

        // A newer launcher might offer a theme this one has never heard of. Loading must survive it,
        // and the mapping has to fall back rather than throw.
        var file = workspace.WriteFile("launcher.json", """
            { "Theme": "HighContrast" }
            """);

        var stored = new LauncherSettingsStore(file).Settings.Theme;

        Assert.Equal("HighContrast", stored);
        Assert.Equal(ThemeChoices.System, MainWindowViewModel.NormaliseTheme(stored));
    }
}
