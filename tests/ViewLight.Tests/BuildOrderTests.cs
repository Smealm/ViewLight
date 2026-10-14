using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Which of two builds is the older one decides what the user is told to update, so the ordering and the
/// sentence built from it are pinned down here rather than left to whoever reads the revision numbers.
/// </summary>
public class BuildOrderTests {

    [Fact]
    public void PutsTheOlderBuildFirst() {
        Assert.True(ClientBuildReader.Compare("r800000.Wizard_1_590", "r806919.Wizard_1_610") < 0);
        Assert.True(ClientBuildReader.Compare("r806919.Wizard_1_610", "r800000.Wizard_1_590") > 0);
        Assert.Equal(0, ClientBuildReader.Compare("r806919.Wizard_1_610", "r806919.Wizard_1_610"));
    }

    [Fact]
    public void ClaimsNothingAboutBuildsItCannotRead() {
        // Null, not "equal" and not "older": an unreadable build is not a comparison.
        Assert.Null(ClientBuildReader.Compare(null, "r806919.Wizard_1_610"));
        Assert.Null(ClientBuildReader.Compare("r806919.Wizard_1_610", null));
        Assert.Null(ClientBuildReader.Compare("", "r806919.Wizard_1_610"));
        Assert.Null(ClientBuildReader.Compare("unknown", "r806919.Wizard_1_610"));
    }

    [Fact]
    public void CallsAnOlderClientOutdatedAndSaysToUpdateTheGame() {
        var said = MainWindowViewModel.DescribeBuildMismatch(
            "r800000.Wizard_1_590", "r806919.Wizard_1_610", enforceRevision: true);

        Assert.NotNull(said);
        Assert.Contains("outdated", said);
        Assert.Contains("Update the game", said);

        // And it says what the revision check will actually do about it.
        Assert.Contains("turn the client away", said);
    }

    [Fact]
    public void CallsANewerClientOutdatedTooAndSaysToUpdateTheServer() {
        var said = MainWindowViewModel.DescribeBuildMismatch(
            "r806919.Wizard_1_610", "r800000.Wizard_1_590", enforceRevision: true);

        Assert.NotNull(said);
        Assert.Contains("newer than this server is built for", said);
        Assert.Contains("Update the server", said);
    }

    [Fact]
    public void NamesBothBuildsWhenItCannotTellWhichIsOlder() {
        var said = MainWindowViewModel.DescribeBuildMismatch("build-a", "build-b", enforceRevision: false);

        Assert.NotNull(said);
        Assert.Contains("build-a", said);
        Assert.Contains("build-b", said);

        // Without a known order it must not guess at a fix, and it still warns that things may break.
        Assert.DoesNotContain("outdated", said);
        Assert.Contains("may still get in", said);
    }
}
