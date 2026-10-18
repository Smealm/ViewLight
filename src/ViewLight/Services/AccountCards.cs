using System.Collections.Immutable;
using Avalonia.Media.Imaging;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One wizard as an account's tile on the sign-in screen summarises it: the name it is drawn under, the
/// school it belongs to and that school's element logo, and its level.
/// </summary>
/// <remarks>
/// A line rather than a <see cref="ProfileWizardRow"/> because a tile has room for three facts and no more.
/// Nothing is invented: the strings are the wizard's own, already worked out by <see cref="ProfileWizard"/>,
/// and a school whose logo has not been fetched yet is drawn by its level rather than by a made-up symbol.
/// </remarks>
/// <param name="CharId">The character's own GID, which is what makes two lines tellable apart.</param>
/// <param name="DisplayName">The wizard's name, or its id when no name could be read for it.</param>
/// <param name="SchoolText">The school as the profile names it, or that it was not read.</param>
/// <param name="SchoolIcon">The school's element logo, or <c>null</c> while it is not to hand.</param>
/// <param name="LevelText">The level, or that it could not be read.</param>
/// <param name="LevelBadgeText">The level as the card prints it: the number, or a dash when unread.</param>
public sealed record AccountWizardLine(
    ulong CharId,
    string DisplayName,
    string SchoolText,
    Bitmap? SchoolIcon,
    string LevelText,
    string LevelBadgeText) {

    /// <summary>Whether this wizard's school has a logo to draw.</summary>
    public bool HasSchoolIcon => SchoolIcon is not null;
}

/// <summary>
/// One profile as the sign-in screen draws it: who it is, their picture and banner, what the account is worth,
/// and the wizards behind it.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than the profile itself, for the same reason a badge is: a profile's score moves as badges
/// are earned and its wizards move as the server is read, so a tile bound to the record could not tell that
/// anything had changed. A tile that has changed <i>is</i> a different tile, and the list is only touched when
/// one has.
/// </para>
/// <para>
/// The pictures are handed in rather than looked up here, because a bitmap is something the screen owns and
/// because a launcher whose pictures have not arrived yet must still produce the same tiles, drawn without
/// them. The score is <see cref="AchievementScores.Of"/>, which is the same number the profile's own circle
/// shows - one rule for what a profile is worth rather than two.
/// </para>
/// </remarks>
/// <param name="AccountId">The account the tile signs in as, which is what identifies it.</param>
/// <param name="Name">The profile's own name, or the account's when it has none.</param>
/// <param name="Username">The account's username, said under the name.</param>
/// <param name="Initials">The letters the picture falls back to.</param>
/// <param name="Avatar">The profile picture, or <c>null</c> for the initial-letter circle.</param>
/// <param name="Banner">The profile banner, or <c>null</c> for the plain tile.</param>
/// <param name="Score">What the account's achievements are worth, bonus included.</param>
/// <param name="Wizards">The first few wizards, in the order the launcher learned them.</param>
/// <param name="MoreWizards">How many wizards the tile has no room for.</param>
/// <param name="WizardCount">How many wizards the account has in all, for the caption.</param>
public sealed record AccountCard(
    ulong AccountId,
    string Name,
    string Username,
    string Initials,
    Bitmap? Avatar,
    Bitmap? Banner,
    int Score,
    ImmutableArray<AccountWizardLine> Wizards,
    int MoreWizards,
    int WizardCount) {

    /// <summary>How many wizards a tile lists before it stops and counts the rest.</summary>
    public const int WizardLimit = 3;

    /// <summary>
    /// Whether two tiles say the same thing, the list of wizards included.
    /// </summary>
    /// <remarks>
    /// Stated here rather than left to the record's own comparison, because a record compares a collection
    /// member by reference: two tiles built from one unchanged profile would be different tiles, and the row on
    /// screen would be rebuilt on every poll - under the pointer of whoever was about to click one.
    /// </remarks>
    public bool Equals(AccountCard? other)
        => other is not null
           && AccountId == other.AccountId
           && Name == other.Name
           && Username == other.Username
           && Initials == other.Initials
           && ReferenceEquals(Avatar, other.Avatar)
           && ReferenceEquals(Banner, other.Banner)
           && Score == other.Score
           && MoreWizards == other.MoreWizards
           && WizardCount == other.WizardCount
           && Wizards.Length == other.Wizards.Length
           && Wizards.Zip(other.Wizards).All(pair => pair.First == pair.Second);

    public override int GetHashCode()
        => HashCode.Combine(AccountId, Name, Username, Initials, Score, MoreWizards, WizardCount);

    public bool HasAvatar => Avatar is not null;

    public bool HasBanner => Banner is not null;

    public bool HasWizards => !Wizards.IsDefaultOrEmpty;

    public bool HasMoreWizards => MoreWizards > 0;

    /// <summary>What the account's achievements are worth, as the tile writes it.</summary>
    public string ScoreText => Score.ToString("N0");

    /// <summary>How many wizards are left over, as the last line of the list.</summary>
    public string MoreWizardsText => $"+{MoreWizards} more";

    /// <summary>
    /// What the tile's list of wizards is headed with, which also says whether there is a list at all.
    /// </summary>
    public string WizardsCaption => WizardCount switch {
        0 => "No wizards read yet",
        1 => "1 wizard",
        var count => $"{count} wizards",
    };

    /// <summary>
    /// The tiles for the profiles the picker offers, in the order it offers them.
    /// </summary>
    /// <remarks>
    /// The order is the picker's own - alphabetical by name, which is what <c>Profiles</c> already is - rather
    /// than a sort of this method's own, so the two screens list the same accounts in the same order. Nothing
    /// is filtered: a profile is only ever in the picker because the server this launcher is pointed at holds
    /// the account it belongs to.
    /// </remarks>
    /// <param name="profiles">The profiles to draw, in the order they are offered.</param>
    /// <param name="avatarFor">The profile's picture, or null while it is not to hand.</param>
    /// <param name="bannerFor">The profile's banner, or null while it is not to hand.</param>
    /// <param name="schoolIconFor">A school's element logo, or null while it is not to hand.</param>
    public static IReadOnlyList<AccountCard> For(
        IEnumerable<LauncherProfile> profiles,
        Func<LauncherProfile, Bitmap?>? avatarFor = null,
        Func<LauncherProfile, Bitmap?>? bannerFor = null,
        Func<string, Bitmap?>? schoolIconFor = null) {

        return [.. profiles.Select(profile => {
            var wizards = profile.Wizards
                .Take(WizardLimit)
                .Select(wizard => new AccountWizardLine(
                    wizard.CharId,
                    wizard.DisplayName,
                    wizard.SchoolText,
                    schoolIconFor?.Invoke(wizard.School),
                    wizard.LevelText,
                    wizard.LevelBadgeText))
                .ToImmutableArray();

            return new AccountCard(
                profile.AccountId,
                profile.Name,
                profile.Username,
                profile.Initials,
                avatarFor?.Invoke(profile),
                bannerFor?.Invoke(profile),
                AchievementScores.Of(profile),
                wizards,
                Math.Max(0, profile.Wizards.Count - wizards.Length),
                profile.Wizards.Count);
        })];
    }
}
