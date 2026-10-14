using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Which server a launch is aimed at decides two things the client cannot work out for itself: whether
/// it may be signed in with a session key, and whether it is a Steam account. Both are pinned here.
/// </summary>
public class PlayTargetTests {

    [Theory]
    [InlineData(PlayServerKind.Local, false)]
    [InlineData(PlayServerKind.Remote, false)]
    [InlineData(PlayServerKind.Local, true)]
    [InlineData(PlayServerKind.Remote, true)]
    [InlineData(PlayServerKind.Official, false)]
    public void SteamIsOnlyEverSentToWizard101sOwnServer(PlayServerKind kind, bool officialUsesSteam)
        => Assert.False(MainWindowViewModel.SteamRequiredFor(kind, officialUsesSteam));

    [Fact]
    public void ASteamOfficialLaunchCarriesTheSteamArgument() {
        // The whole point of the choice: a Steam account cannot sign in to Wizard101's own server
        // without -ST, and it must be what actually reaches the client.
        var steamRequired = MainWindowViewModel.SteamRequiredFor(PlayServerKind.Official, officialUsesSteam: true);

        Assert.True(steamRequired);
        Assert.Equal("-L login.us.wizard101.com 12000 -ST",
            ClientLauncher.BuildArguments(OfficialServer.Endpoint.Host, OfficialServer.Endpoint.Port,
                steamRequired: steamRequired));
    }
}
