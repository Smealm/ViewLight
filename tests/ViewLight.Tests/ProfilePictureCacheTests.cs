using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// What the profile screen reads, and when.
/// </summary>
/// <remarks>
/// Two things went wrong here and both of them looked the same to the user - a picture that was not the one
/// they had just chosen. Replacing a picture keeps its name (<c>avatar.png</c> either way), so the name alone
/// cannot say whether what is on screen is current; and a read that failed was remembered as an answer, so one
/// miss at startup left the initial-letter circle there until the launcher was restarted. The rule is pinned
/// down here, away from a screen and a database.
/// </remarks>
public class ProfilePictureCacheTests {

    [Fact]
    public void APictureThatIsReplacedIsReadAgainEvenThoughItKeepsItsName() {
        // Same profile, same names - and a different picture behind them. Nothing about the names says so, which
        // is why the run's own picture count is part of the key.
        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(pictures: 1), lastReadFailed: false, retryIsDue: false));

        // And the same for the banner, which is the other half of the pair: replacing it leaves the avatar alone
        // and must still bring the new banner up.
        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(banner: "banner.jpg"), lastReadFailed: false, retryIsDue: false));
    }

    [Fact]
    public void TheNotesBeingReadAgainIsEnoughToReadThePicturesAgain() {
        // A restore from the cloud puts different pictures behind the same names, and the launcher knows it
        // because its own notes were read again - not because anything on screen changed.
        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(notes: 1), lastReadFailed: false, retryIsDue: false));
    }

    [Fact]
    public void NothingIsReadWhenNothingHasChanged() {
        // This is called from every refresh of the derived state, several of which happen per second.
        Assert.False(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(), lastReadFailed: false, retryIsDue: false));
    }

    [Fact]
    public void AProfileWithNoPictureHasOneKindOfNothingToShow() {
        // A circle drawn from the initials, an avatar, a banner, and a profile nobody has given a picture to all
        // have to be told apart: the first three are a read, the last is not.
        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(avatar: null, banner: null), lastReadFailed: false, retryIsDue: false));

        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: new MainWindowViewModel.ProfilePictureKey(7, null, null, 0, 0),
            lastReadFailed: false, retryIsDue: false));
    }

    [Fact]
    public void AReadThatFailedIsTriedAgainRatherThanKeptAsTheAnswer() {
        // The picture is not there to show, so nothing on screen changes and nothing about the profile changes
        // either - the retry is the only thing that can bring it up, and it waits its turn rather than being tried
        // from every refresh.
        Assert.False(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(), lastReadFailed: true, retryIsDue: false));

        Assert.True(MainWindowViewModel.PicturesNeedReading(
            shown: Key(), wanted: Key(), lastReadFailed: true, retryIsDue: true));
    }

    /// <summary>One profile's pictures, as the screen keys them.</summary>
    private static MainWindowViewModel.ProfilePictureKey Key(
        string? avatar = "avatar.png", string? banner = null, int notes = 0, int pictures = 0)
        => new(42, avatar, banner, notes, pictures);
}
