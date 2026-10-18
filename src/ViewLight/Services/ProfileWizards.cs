using Avalonia.Media.Imaging;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One wizard as the profile screen shows it: the name it is drawn under, the school it belongs to and that
/// school's element logo, its level, what it holds, and how much of the game it has learned.
/// </summary>
/// <remarks>
/// <para>
/// A row is an <b>immutable snapshot</b>, for the same reason a badge is: the wizard itself is the
/// launcher's own record - a plain object whose fields move underneath whoever is holding it - so a screen
/// bound to the record cannot tell that anything changed. A level-up, a school read for the first time, a
/// purse that got heavier or a quest that finished is all the same thing to a binding: a number that has
/// moved inside an object nobody told it about. As a value, a row that has changed <i>is</i> a different row,
/// so "has anything changed" is one comparison and the answer decides whether the list is touched at all.
/// </para>
/// <para>
/// Nothing is invented here. Every string is the wizard's own, already worked out by
/// <see cref="ProfileWizard"/> - including the two that say a read has not happened yet rather than showing a
/// zero, because a database that only answers while the server runs is a database that is sometimes not
/// there. The one thing that is not the wizard's own is the school's logo, which is the game's art fetched by
/// <see cref="SchoolIconCache"/> and handed in rather than looked up from here - see <see cref="For"/>.
/// </para>
/// </remarks>
/// <param name="CharId">The character's own GID, which is what makes two rows tellable apart.</param>
/// <param name="DisplayName">The wizard's name, or its id when no name could be read for it.</param>
/// <param name="School">The school's own name, or an empty string when none could be read.</param>
/// <param name="SchoolText">The school as the card prints it - the name, or that it was not read.</param>
/// <param name="SchoolIcon">The school's element logo, or <c>null</c> while it is not to hand.</param>
/// <param name="LevelText">The level, or that it could not be read.</param>
/// <param name="LevelBadgeText">The level as the badge on the card shows it: the number, or a dash.</param>
/// <param name="GoldText">The gold held, written out.</param>
/// <param name="QuestsText">How many quests are finished, written out.</param>
/// <param name="SpellsText">How many spells the spellbook holds, written out.</param>
/// <param name="LastSeenText">When the character was last seen, or nothing when it has not been.</param>
public sealed record ProfileWizardRow(
    ulong CharId,
    string DisplayName,
    string School,
    string SchoolText,
    Bitmap? SchoolIcon,
    string LevelText,
    string LevelBadgeText,
    string GoldText,
    string QuestsText,
    string SpellsText,
    string LastSeenText) {

    /// <summary>Whether this wizard's school has a logo on its card.</summary>
    public bool HasSchoolIcon => SchoolIcon is not null;

    /// <summary>Whether the card has a line to say about when the wizard was last seen.</summary>
    /// <remarks>
    /// A wizard the launcher has never watched play has nothing to put there, and an empty line is a gap in
    /// the card rather than a fact about the wizard.
    /// </remarks>
    public bool HasLastSeen => LastSeenText.Length > 0;

    /// <summary>
    /// Everything the card cannot fit, in one line, for the tooltip it is hovered for.
    /// </summary>
    /// <remarks>
    /// A wizard's card is deliberately short - a face, a name, a level - so the numbers that used to have a
    /// card of their own are one hover away rather than gone. Nothing is added that is not the wizard's own:
    /// the parts are the same strings the old card printed, joined in the order it read them.
    /// </remarks>
    public string DetailToolTip => string.Join(" · ",
        new[] { SchoolText, LevelText, GoldText, SpellsText, QuestsText, LastSeenText }
            .Where(part => part.Length > 0));

    /// <summary>
    /// The rows for an account's wizards, in the order the launcher learned them.
    /// </summary>
    /// <remarks>
    /// The order is the profile's own list rather than a sort of this method's own: a wizard is added when
    /// the server first reports it, and a list that reordered itself as a level moved would move a row out
    /// from under somebody reading it. The logo is looked up through the caller because the school's art is
    /// a picture the screen owns rather than something a model reaches for - and because a launcher that has
    /// not fetched it yet must still produce the same rows, shown without it.
    /// </remarks>
    /// <param name="profile">Whose wizards these are, or null for a launcher with no profile on screen.</param>
    /// <param name="iconFor">The school's element logo, or null for a school whose logo is not to hand.</param>
    public static IReadOnlyList<ProfileWizardRow> For(
        LauncherProfile? profile, Func<string, Bitmap?>? iconFor = null) {

        if (profile is null) {
            return [];
        }

        return [.. profile.Wizards.Select(wizard => new ProfileWizardRow(
            wizard.CharId,
            wizard.DisplayName,
            wizard.School,
            wizard.SchoolText,
            iconFor?.Invoke(wizard.School),
            wizard.LevelText,
            wizard.LevelBadgeText,
            wizard.GoldText,
            wizard.QuestsText,
            wizard.SpellsText,
            wizard.LastSeenText))];
    }
}
