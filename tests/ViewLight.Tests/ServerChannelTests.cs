using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The build channel is saved state, and the values are strings so a later build can read an old file.
/// Those two facts are what these pin down: the fallback, and that the choice survives a restart.
/// </summary>
public class ServerChannelTests {

    [Fact]
    public void ALauncherThatHasNeverBeenAskedInstallsPrereleases() {
        Assert.Equal(ServerChannelChoices.Prerelease, new LauncherSettings().ServerChannel);
        Assert.Equal(ServerChannelChoices.Prerelease, ServerChannelChoices.Parse(null));
        Assert.Equal(ServerChannelChoices.Prerelease, ServerChannelChoices.Parse(string.Empty));
    }

    [Fact]
    public void AnUnknownChannelFallsBackRatherThanFailingToLoad() {
        Assert.Equal(ServerChannelChoices.Prerelease, ServerChannelChoices.Parse("Nightly"));
        Assert.Equal(ServerChannelChoices.Stable, ServerChannelChoices.Parse(ServerChannelChoices.Stable));
        Assert.Equal(ServerChannelChoices.BleedingEdge, ServerChannelChoices.Parse(ServerChannelChoices.BleedingEdge));
    }

    [Fact]
    public void EveryChoiceIsOfferedInTheOrderTheButtonsListThem() {
        Assert.Equal(
            [ServerChannelChoices.Stable, ServerChannelChoices.Prerelease, ServerChannelChoices.BleedingEdge],
            ServerChannelChoices.All);
    }

    [Fact]
    public void TheChoiceSurvivesARestart() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var first = new LauncherSettingsStore(file);
        first.Settings.ServerChannel = ServerChannelChoices.BleedingEdge;
        first.Save();

        var reloaded = new LauncherSettingsStore(file);

        Assert.Equal(ServerChannelChoices.BleedingEdge, reloaded.Settings.ServerChannel);
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public void TheBranchForABuildFromSourceSurvivesARestart() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        // Empty is a choice in its own right - the repository's default branch - so both states have to
        // come back as they went in.
        Assert.Equal(string.Empty, new LauncherSettings().BleedingEdgeBranch);

        var first = new LauncherSettingsStore(file);
        first.Settings.BleedingEdgeBranch = "feat/plugins";
        first.Save();

        var reloaded = new LauncherSettingsStore(file);

        Assert.Equal("feat/plugins", reloaded.Settings.BleedingEdgeBranch);
        Assert.Null(reloaded.LoadProblem);
    }
}
