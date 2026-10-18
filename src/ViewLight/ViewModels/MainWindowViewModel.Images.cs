using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The pictures a profile can be given: the game's own school element logos, which the wizard cards wear,
/// and the two collections a picture or a banner can be browsed for rather than picked off disk.
/// </summary>
/// <remarks>
/// <para>
/// Two things that look alike and are not. A school's logo is fetched once and read from the cache forever
/// after, because there are seven of them and they never change - so the work here is to get them and hold
/// them. A browsed picture is the user's own choice out of thousands, so the work is to go and get the one
/// they settled on, and to keep it as part of the profile exactly as a picked file would be.
/// </para>
/// <para>
/// Each of these reaches off this machine, so each is offered rather than done: nothing is downloaded until
/// a card is on screen to draw it on or a person has asked to browse. A launcher that is offline shows the
/// initial-letter circle and the plain wizard card, and says nothing about it - there is nothing to say.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    private readonly SchoolIconCache _schoolIcons = new();

    /// <summary>
    /// The school logos that have been fetched, by school, ready for a wizard card to be drawn with.
    /// </summary>
    /// <remarks>
    /// Held here rather than asked of the cache per card, because a refresh builds every row and a row that
    /// looked a picture up asynchronously could not be built at all. A school that is not in here has no logo
    /// yet, and a card without its school's logo is still a card.
    /// </remarks>
    private readonly Dictionary<string, Bitmap?> _schoolIconBitmaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the seven logos have been fetched or failed, so they are not asked for again.</summary>
    private bool _schoolIconsWarmed;

    /// <summary>Set while they are being fetched, so seven cards ask for them once rather than seven times.</summary>
    private bool _schoolIconsWarming;

    /// <summary>
    /// Set by the window so a profile picture or banner can be browsed for without the view model knowing
    /// about views.
    /// </summary>
    /// <remarks>
    /// The source is which collection to browse, and the answer is the picture itself rather than a path:
    /// these come off the network, and what the launcher keeps is the bytes, exactly as it keeps the bytes of
    /// a chosen file.
    /// </remarks>
    public Func<ImageBrowserSource, Task<ChosenImage?>>? BrowseImageAsync { get; set; }

    /// <summary>
    /// The game's element logo for a school, or <c>null</c> while it is not to hand.
    /// </summary>
    /// <param name="school">
    /// A school as the player database spells it, which may be an id, a name, or something that is not a
    /// wizard's school at all - in which case there is nothing to draw.
    /// </param>
    public Bitmap? SchoolIconFor(string? school) {
        var name = WizardSchool.NameOf(school);

        return name.Length == 0 ? null : _schoolIconBitmaps.GetValueOrDefault(name);
    }

    /// <summary>Whether any school's logo is on screen at all, which is what the card's explanation turns on.</summary>
    public bool HasSchoolIcons => _schoolIconBitmaps.Values.Any(bitmap => bitmap is not null);

    /// <summary>
    /// Fetches the seven school element logos, once per run, in the background.
    /// </summary>
    /// <remarks>
    /// Called from the places a wizard card can appear rather than from the constructor, because a launcher
    /// sitting on Home has no use for them: they are two kilobytes each and the first one is needed on the
    /// profile screen. Everything is attempted, including schools that fail - which is remembered as having
    /// failed, so an offline launcher asks seven times rather than once per wizard per poll.
    /// </remarks>
    private void WarmSchoolIcons() {
        if (_schoolIconsWarmed || _schoolIconsWarming) {
            return;
        }

        _schoolIconsWarming = true;
        _ = WarmSchoolIconsAsync();
    }

    private async Task WarmSchoolIconsAsync() {
        try {
            foreach (var school in SchoolIcons.Names) {
                var bitmap = await _schoolIcons.LoadAsync(school).ConfigureAwait(true);
                _schoolIconBitmaps[school] = bitmap;
            }

            var missing = _schoolIconBitmaps.Count(pair => pair.Value is null);

            if (missing > 0) {
                AppendLog(LogLevel.Info,
                    $"{missing} of the {SchoolIcons.Names.Count} school logos could not be fetched, so those "
                    + "wizard cards are drawn without one. They are tried again next time ViewLight starts.");
            }
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Info, $"The school logos could not be fetched ({ex.Message}).");
        }
        finally {
            _schoolIconsWarmed = true;
            _schoolIconsWarming = false;
        }

        // The cards are drawn from rows that were built before the pictures existed, so they are built again
        // now that they do - which is what makes a logo appear rather than a card that says it has none.
        RefreshWizards(SelectedProfile);
        RefreshAccountCards();
        OnPropertyChanged(nameof(HasSchoolIcons));
    }

    // ---------------------------------------------------------------- browsing for a picture

    /// <summary>Opens the gamerpic archive and keeps whatever is chosen as the profile's picture.</summary>
    [RelayCommand]
    private Task BrowseProfilePictureAsync() => BrowseProfileImageAsync(banner: false);

    /// <summary>Opens wallhaven and keeps whatever is chosen as the profile's banner.</summary>
    [RelayCommand]
    private Task BrowseProfileBannerAsync() => BrowseProfileImageAsync(banner: true);

    /// <summary>
    /// Browses one of the two collections and adopts the picture chosen from it.
    /// </summary>
    /// <remarks>
    /// The picture is kept the same way a file from disk is - into the profile's own document, so it is part
    /// of what the launcher holds and part of any backup of it - and the Log says where it came from, because
    /// a banner that arrived from the internet is worth being able to point at.
    /// </remarks>
    private async Task BrowseProfileImageAsync(bool banner) {
        if (BrowseImageAsync is null || SelectedProfile is null) {
            return;
        }

        var chosen = await BrowseImageAsync(
            banner ? ImageBrowserSource.Wallpapers : ImageBrowserSource.Gamerpics).ConfigureAwait(true);

        if (chosen is null || SelectedProfile is not { } profile) {
            return;
        }

        var kept = await _profiles
            .AdoptImageBytesAsync(profile, chosen.Bytes, chosen.Extension, banner)
            .ConfigureAwait(true);

        if (kept is null) {
            AppendLog(LogLevel.Error,
                $"Could not keep {chosen.Description} as the profile's "
                + $"{(banner ? "banner" : "picture")}: {_profiles.Problem ?? "the picture could not be kept."}");
            return;
        }

        ForgetPicture(profile.AccountId, kept);

        AppendLog(LogLevel.Info,
            $"Profile {(banner ? "banner" : "picture")} set from {chosen.Description} "
            + $"({ServerInstallService.FormatBytes(chosen.Bytes.Length)}).");

        RefreshProfiles();
    }
}
