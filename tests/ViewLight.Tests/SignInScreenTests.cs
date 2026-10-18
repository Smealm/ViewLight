using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Signing out of a profile and back in as another: what the profile page's Log out button does, what the
/// sign-in screen does, and the one rule that keeps a poll from undoing either.
/// </summary>
/// <remarks>
/// The screen itself is a view over this state, so the parts pinned down here are the ones that can go wrong
/// silently: a launcher that signs itself back in on the next look, or one that forgets which account it plays
/// as because the profile was put down.
/// </remarks>
public class SignInScreenTests {

    [Fact]
    public void SignedOutIsNotTheSameAsNoAccounts() {
        // A launcher with no profile to show says so on the profile page. The sign-in screen is only ever up
        // because somebody asked to be signed out, which is why it is a flag rather than a reading of the
        // profile list - an empty server must not cover the launcher.
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        Assert.False(launcher.IsSignedOut);
        Assert.False(launcher.HasProfile);
    }

    [Fact]
    public void LoggingOutPutsTheProfileDownAndKeepsWhoPlaySignsInAs() {
        using var workspace = new TempWorkspace();
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        var launcher = new MainWindowViewModel(store, fetchArt: false);

        // Play has an account, and the profile screen has the profile that belongs to it.
        var profile = new LauncherProfile { AccountId = 42, Username = "jordan" };
        launcher.Accounts.Add(new PlayerAccount(42, "jordan"));
        launcher.Profiles.Add(profile);
        launcher.SelectedProfile = profile;

        launcher.LogOutCommand.Execute(null);

        Assert.True(launcher.IsSignedOut);
        Assert.Null(launcher.SelectedProfile);
        Assert.Null(launcher.SelectedAccount);
        Assert.False(launcher.HasProfile);

        // The accounts are still there to choose between - that is the point of signing out.
        Assert.True(launcher.HasProfiles);

        // And logging out is not forgetting: the machine still knows who it plays as, so the next start and
        // the way back both have an answer.
        Assert.Equal(42UL, store.Settings.AutoLoginAccountId);
    }

    [Fact]
    public void SigningInPicksTheAccountAndComesBackToTheProfile() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        var profile = new LauncherProfile { AccountId = 42, Username = "jordan" };
        launcher.Accounts.Add(new PlayerAccount(42, "jordan"));
        launcher.Profiles.Add(profile);
        launcher.SelectedProfile = profile;

        launcher.LogOutCommand.Execute(null);
        launcher.SignInAs(42);

        Assert.False(launcher.IsSignedOut);
        Assert.Same(profile, launcher.SelectedProfile);
        Assert.True(launcher.IsProfileSelected);
        Assert.True(launcher.ShowProfilePage);
    }

    [Fact]
    public void AnAccountThatWentAwayBetweenTheTileAndTheClickSignsNobodyIn() {
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        launcher.Accounts.Add(new PlayerAccount(42, "jordan"));
        launcher.Profiles.Add(new LauncherProfile { AccountId = 42, Username = "jordan" });
        launcher.SelectedProfile = launcher.Profiles[0];
        launcher.LogOutCommand.Execute(null);

        // The profile list is filled by the server, so a tile can outlive the account it names - a server that
        // has moved on is a real answer, and pretending to sign in as somebody who is no longer there is not.
        launcher.SignInAs(99);

        Assert.True(launcher.IsSignedOut);
        Assert.Null(launcher.SelectedProfile);
    }

    [Fact]
    public void CancellingSignsBackInAsTheAccountThatWasSignedOut() {
        using var workspace = new TempWorkspace();
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        var launcher = new MainWindowViewModel(store, fetchArt: false);

        var jordan = new LauncherProfile { AccountId = 42, Username = "jordan" };
        var alex = new LauncherProfile { AccountId = 43, Username = "alex" };
        launcher.Accounts.Add(new PlayerAccount(42, "jordan"));
        launcher.Accounts.Add(new PlayerAccount(43, "alex"));
        launcher.Profiles.Add(jordan);
        launcher.Profiles.Add(alex);

        launcher.SelectedProfile = alex;
        launcher.LogOutCommand.Execute(null);
        launcher.CancelSignInCommand.Execute(null);

        // Back where they started rather than to nobody: logging out was a detour, and this is the undo. Which
        // account that is comes from the same place Play's automatic sign-in does - picking alex made the
        // launcher remember alex.
        Assert.False(launcher.IsSignedOut);
        Assert.Same(alex, launcher.SelectedProfile);
        Assert.Equal(43UL, store.Settings.AutoLoginAccountId);
    }

    [Fact]
    public void APollCannotSignTheLauncherBackIn() {
        // The profile list is read every ten seconds, and the account Play signs in as is remembered - so the
        // rule that decides who to put back has to know about being signed out, or the screen appears for one
        // poll and closes itself.
        Assert.Null(MainWindowViewModel.ProfileToRestore(
            signedOut: true, selected: 7, account: 7, remembered: 7));

        // Signed in, it is the profile on screen, then the account Play would use, then last time's.
        Assert.Equal(7UL, MainWindowViewModel.ProfileToRestore(
            signedOut: false, selected: 7, account: 8, remembered: 9));

        Assert.Equal(8UL, MainWindowViewModel.ProfileToRestore(
            signedOut: false, selected: null, account: 8, remembered: 9));

        Assert.Equal(9UL, MainWindowViewModel.ProfileToRestore(
            signedOut: false, selected: null, account: null, remembered: 9));

        // And with nothing remembered there is nobody to put back - never simply the first profile, which is
        // how a stranger's name used to end up on the screen of a launcher that had no accounts.
        Assert.Null(MainWindowViewModel.ProfileToRestore(
            signedOut: false, selected: null, account: null, remembered: null));
    }
}
