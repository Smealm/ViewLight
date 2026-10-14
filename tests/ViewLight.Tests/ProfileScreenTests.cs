using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The profile's two pages and the account card in the corner of the window: which page is on screen, what
/// the heading over it says, and when the card folds itself back up.
/// </summary>
/// <remarks>
/// These are the rules the screens are drawn from. A page that is shown for the wrong section, or a card
/// that folds under the pointer, is not a rendering fault - the view is doing exactly what it was told.
/// </remarks>
public class ProfileScreenTests {

    [Fact]
    public void AchievementsAreTheProfilesSecondPageRatherThanASectionOfItsOwn() {
        // Reached from the profile and shown there alone: the screen belongs to the account whose badges it
        // shows, so a launcher left on another section would be showing badges for nobody.
        Assert.True(MainWindowViewModel.ShowsAchievements(
            LauncherSection.Profile, ProfilePage.Achievements));

        Assert.False(MainWindowViewModel.ShowsAchievements(
            LauncherSection.Profile, ProfilePage.Profile));

        foreach (var section in Enum.GetValues<LauncherSection>().Where(s => s != LauncherSection.Profile)) {
            Assert.False(MainWindowViewModel.ShowsAchievements(section, ProfilePage.Achievements));
        }
    }

    [Fact]
    public void EveryPageSaysWhichPageItIs() {
        foreach (var section in Enum.GetValues<LauncherSection>()) {
            foreach (var page in Enum.GetValues<ProfilePage>()) {
                Assert.False(string.IsNullOrWhiteSpace(MainWindowViewModel.TitleFor(section, page)));
                Assert.False(string.IsNullOrWhiteSpace(MainWindowViewModel.SubtitleFor(section, page)));
            }
        }

        // And the profile's two pages are told apart by both halves of the heading: an achievements screen
        // headed "Profile" would read as the profile with something missing from it.
        Assert.NotEqual(
            MainWindowViewModel.TitleFor(LauncherSection.Profile, ProfilePage.Profile),
            MainWindowViewModel.TitleFor(LauncherSection.Profile, ProfilePage.Achievements));

        Assert.NotEqual(
            MainWindowViewModel.SubtitleFor(LauncherSection.Profile, ProfilePage.Profile),
            MainWindowViewModel.SubtitleFor(LauncherSection.Profile, ProfilePage.Achievements));
    }

    [Fact]
    public void TheProfileTabIsToldToShowItselfWhenItIsOpened() {
        // The views are drawn from a section and a page, so the section changing has to say so out loud:
        // notifying the sidebar's selected classes and nothing else is a Profile tab that comes up empty,
        // because neither of its views is ever told to show itself.
        using var workspace = new TempWorkspace();

        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        var said = new List<string>();
        launcher.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? string.Empty);

        Assert.False(launcher.ShowProfilePage);

        launcher.NavigateCommand.Execute("Profile");

        Assert.True(launcher.ShowProfilePage);
        Assert.Contains(nameof(MainWindowViewModel.ShowProfilePage), said);

        // And the achievements screen takes its place and gives it back, saying so both times.
        said.Clear();
        launcher.OpenAchievementsCommand.Execute(null);

        Assert.True(launcher.ShowAchievementsPage);
        Assert.False(launcher.ShowProfilePage);
        Assert.Contains(nameof(MainWindowViewModel.ShowAchievementsPage), said);
        Assert.Contains(nameof(MainWindowViewModel.ShowProfilePage), said);

        said.Clear();
        launcher.CloseAchievementsCommand.Execute(null);

        Assert.True(launcher.ShowProfilePage);
        Assert.Contains(nameof(MainWindowViewModel.ShowProfilePage), said);

        // Leaving the profile hides its second page with it, and coming back to Profile lands on the
        // profile: the page is honoured on Profile and nowhere else.
        launcher.NavigateCommand.Execute("Settings");
        Assert.False(launcher.ShowProfilePage);
        Assert.False(launcher.ShowAchievementsPage);

        launcher.NavigateCommand.Execute("Profile");
        launcher.OpenAchievementsCommand.Execute(null);
        Assert.True(launcher.ShowAchievementsPage);

        launcher.ShowAccountCommand.Execute(null);
        Assert.True(launcher.ShowProfilePage);
        Assert.True(launcher.IsProfileSelected);

        // And the sidebar's Profile entry is a request for the account even when Profile is already the
        // section on screen: the achievements screen is opened from the profile rather than landed on.
        launcher.OpenAchievementsCommand.Execute(null);
        launcher.NavigateCommand.Execute("Profile");

        Assert.True(launcher.ShowProfilePage);
        Assert.False(launcher.ShowAchievementsPage);
    }

    [Fact]
    public void TheCardNeverFoldsUnderThePointer() {
        // The countdown can expire in the same instant the pointer comes back to the card, and folding it
        // up then is the card vanishing from under the hand that was reaching for it.
        Assert.True(MainWindowViewModel.ProfileCardShouldFold(isOpen: true, pointerOver: false));
        Assert.False(MainWindowViewModel.ProfileCardShouldFold(isOpen: true, pointerOver: true));
        Assert.False(MainWindowViewModel.ProfileCardShouldFold(isOpen: false, pointerOver: false));

        // A moment's grace rather than no wait at all, and not so long that the card feels stuck open.
        Assert.InRange(MainWindowViewModel.ProfileCardLingerDuration, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5));
    }
}
