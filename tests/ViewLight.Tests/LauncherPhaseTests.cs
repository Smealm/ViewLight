using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The phase decides what Home says and whether Home says anything at all, so the rules are pinned down
/// here rather than left inside the event handlers that apply them.
/// </summary>
public class LauncherPhaseTests {

    // ------------------------------------------------------------------ what is shown

    [Fact]
    public void AnIdleLauncherSaysNothing() {
        foreach (var phase in Enum.GetValues<LauncherPhase>()) {
            Assert.Equal(phase != LauncherPhase.Idle, MainWindowViewModel.IsStatusShown(phase));
        }
    }

    [Fact]
    public void EveryPhaseGetsExactlyOneIndicator() {
        // The status panel picks its colour and whether it animates from these, so a phase that fell
        // into none of them would render as a dot with nothing to say.
        foreach (var phase in Enum.GetValues<LauncherPhase>()) {
            var readings = new[] {
                MainWindowViewModel.IsPhaseWorking(phase),
                MainWindowViewModel.IsPhaseGood(phase),
                phase == LauncherPhase.Failed,
            };

            Assert.True(readings.Count(r => r) <= 1, $"{phase} matches more than one indicator.");
        }
    }

    // ------------------------------------------------------------------ what the status says

    [Fact]
    public void TheStatusIsWrittenAboutGettingIntoTheGame() {
        // The point of the panel is that Play is doing something for you, so the headline never leads
        // with the server. Which server is starting, and what it is doing, is the line underneath.
        foreach (var phase in Enum.GetValues<LauncherPhase>()) {
            if (!MainWindowViewModel.IsStatusShown(phase)) {
                continue;
            }

            foreach (var client in Enum.GetValues<ClientState>()) {
                var headline = MainWindowViewModel.HeadlineFor(phase, client, "Something broke");
                Assert.False(string.IsNullOrWhiteSpace(headline), $"{phase} has no headline.");
                Assert.DoesNotContain("server", headline, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal("Getting ready…",
            MainWindowViewModel.HeadlineFor(LauncherPhase.StartingServer, ClientState.NotRunning, ""));
        Assert.Equal("Warming up…",
            MainWindowViewModel.HeadlineFor(LauncherPhase.WarmingServer, ClientState.NotRunning, ""));
        Assert.Equal("Starting Wizard101…",
            MainWindowViewModel.HeadlineFor(LauncherPhase.StartingClient, ClientState.Starting, ""));
        Assert.Equal("Wizard101 is running",
            MainWindowViewModel.HeadlineFor(LauncherPhase.Playing, ClientState.Running, ""));

        // A failure still says what failed, rather than being flattened into the rule.
        Assert.Equal("The server stopped unexpectedly",
            MainWindowViewModel.HeadlineFor(LauncherPhase.Failed, ClientState.Closed, "The server stopped unexpectedly"));
    }

    [Fact]
    public void TheWorkingStepsSayWhatTheServerIsDoing() {
        // The other half of the same rule: the server is named where it belongs.
        Assert.Contains("server",
            MainWindowViewModel.DetailFor(LauncherPhase.WarmingServer, ClientState.NotRunning, ""),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server",
            MainWindowViewModel.DetailFor(LauncherPhase.StartingServer, ClientState.NotRunning, ""),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal("The server is still running, so press Play to go back in.",
            MainWindowViewModel.DetailFor(LauncherPhase.ServerReady, ClientState.Closed, ""));
    }

    // ------------------------------------------------------------------ the server's own moves

    [Theory]
    [InlineData(LauncherPhase.Idle, ServerState.Starting, false, LauncherPhase.StartingServer)]
    [InlineData(LauncherPhase.Idle, ServerState.WarmingUp, false, LauncherPhase.WarmingServer)]
    [InlineData(LauncherPhase.StartingServer, ServerState.WarmingUp, false, LauncherPhase.WarmingServer)]
    [InlineData(LauncherPhase.WarmingServer, ServerState.Ready, false, LauncherPhase.ServerReady)]
    [InlineData(LauncherPhase.Idle, ServerState.Ready, false, LauncherPhase.ServerReady)]
    [InlineData(LauncherPhase.Idle, ServerState.Faulted, false, LauncherPhase.Failed)]
    [InlineData(LauncherPhase.Playing, ServerState.Faulted, false, LauncherPhase.Failed)]
    [InlineData(LauncherPhase.Playing, ServerState.Stopped, false, LauncherPhase.Idle)]
    [InlineData(LauncherPhase.ServerReady, ServerState.Stopped, false, LauncherPhase.Idle)]
    [InlineData(LauncherPhase.Idle, ServerState.Stopped, false, LauncherPhase.Idle)]
    // Stopping keeps what it was: the game may still be up, and it is about to go down.
    [InlineData(LauncherPhase.Playing, ServerState.Stopping, false, LauncherPhase.Playing)]
    public void FollowsTheServer(
        LauncherPhase current, ServerState state, bool playInFlight, LauncherPhase expected)
        => Assert.Equal(expected, MainWindowViewModel.PhaseForServer(current, state, playInFlight));

    [Fact]
    public void PlayKeepsWaitingRatherThanSettlingOnAReadyServer() {
        // Pressing Play means the game starts next. Reading a warming server is always right; calling it
        // ready there would flash the wrong message on the way to launching the client.
        Assert.Equal(LauncherPhase.WarmingServer,
            MainWindowViewModel.PhaseForServer(LauncherPhase.WarmingServer, ServerState.Ready, playInFlight: true));
        Assert.Equal(LauncherPhase.ServerReady,
            MainWindowViewModel.PhaseForServer(LauncherPhase.WarmingServer, ServerState.Ready, playInFlight: false));
    }

    // ------------------------------------------------------------------ the game's own moves

    [Theory]
    [InlineData(ClientState.Starting, ServerState.Ready, LauncherPhase.StartingClient)]
    [InlineData(ClientState.Running, ServerState.Ready, LauncherPhase.Playing)]
    [InlineData(ClientState.Closed, ServerState.Ready, LauncherPhase.ServerReady)]
    [InlineData(ClientState.Closed, ServerState.Stopped, LauncherPhase.Idle)]
    [InlineData(ClientState.Closed, ServerState.Faulted, LauncherPhase.Idle)]
    public void FollowsTheClient(ClientState client, ServerState server, LauncherPhase expected)
        => Assert.Equal(expected,
            MainWindowViewModel.PhaseForClient(LauncherPhase.StartingClient, client, server));

    [Fact]
    public void AClosedGameLeavesARunningServerReported() {
        // Closing the game does not stop the server, so the launcher keeps saying the server is there -
        // and the user can press Play again.
        Assert.Equal(LauncherPhase.ServerReady,
            MainWindowViewModel.PhaseForClient(LauncherPhase.Playing, ClientState.Closed, ServerState.Ready));
        Assert.True(MainWindowViewModel.IsStatusShown(LauncherPhase.ServerReady));
    }

    [Fact]
    public void AFailureOutlastsTheClientItTookDown() {
        // A crashed server closes the game a moment later, and that must not wipe the only explanation
        // the user is going to get.
        Assert.Equal(LauncherPhase.Failed,
            MainWindowViewModel.PhaseForClient(LauncherPhase.Failed, ClientState.Closed, ServerState.Faulted));
        Assert.Equal(LauncherPhase.Failed,
            MainWindowViewModel.PhaseForClient(LauncherPhase.Failed, ClientState.Closed, ServerState.Stopped));
    }

    [Fact]
    public void APhaseWithNothingChangedStaysWhereItIs() {
        Assert.Equal(LauncherPhase.Playing,
            MainWindowViewModel.PhaseForServer(LauncherPhase.Playing, ServerState.Stopping, false));
        Assert.Equal(LauncherPhase.Failed,
            MainWindowViewModel.PhaseForClient(LauncherPhase.Failed, ClientState.NotRunning, ServerState.Faulted));
    }

    // ------------------------------------------------------------------ the two builds

    [Fact]
    public void SaysNothingWhenTheTwoBuildsAgree() {
        Assert.Null(MainWindowViewModel.DescribeBuildMismatch(
            "r806919.Wizard_1_610", "r806919.Wizard_1_610", enforceRevision: true));
    }

    [Fact]
    public void SaysNothingWhenEitherBuildIsUnknown() {
        // An install that has never run the client has no build to compare, and inventing a warning for
        // it would be worse than saying nothing.
        Assert.Null(MainWindowViewModel.DescribeBuildMismatch(null, "r806919.Wizard_1_610", true));
        Assert.Null(MainWindowViewModel.DescribeBuildMismatch("r806919.Wizard_1_610", null, true));
    }

    [Fact]
    public void ExplainsAMismatchAndSaysWhatItMeansForGettingIn() {
        var enforced = MainWindowViewModel.DescribeBuildMismatch(
            "r800000.Wizard_1_590", "r806919.Wizard_1_610", enforceRevision: true);
        var tolerated = MainWindowViewModel.DescribeBuildMismatch(
            "r800000.Wizard_1_590", "r806919.Wizard_1_610", enforceRevision: false);

        Assert.NotNull(enforced);
        Assert.NotNull(tolerated);

        Assert.Contains("r800000.Wizard_1_590", enforced);
        Assert.Contains("r806919.Wizard_1_610", enforced);
        Assert.Contains("turn the client away", enforced);

        // With the revision check off, the same two builds are only a warning about reporting bugs.
        Assert.Contains("may still get in", tolerated);
    }
}
