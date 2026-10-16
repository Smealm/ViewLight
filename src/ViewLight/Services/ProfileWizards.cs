using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One wizard as the profile screen shows it: the name it is drawn under, its school and level, what it
/// holds, and when it was last seen.
/// </summary>
/// <remarks>
/// <para>
/// A row is an <b>immutable snapshot</b>, for the same reason a badge is: the wizard itself is the
/// launcher's own record - a plain object whose fields move underneath whoever is holding it - so a screen
/// bound to the record cannot tell that anything changed. A level-up, a school read for the first time, a
/// purse that got heavier or a quest that finished is all the same thing to a binding: a number that has
/// moved inside an object nobody told it about. As a value, a row that has changed <i>is</i> a different row,
/// so \"has anything changed\" is one comparison and the answer decides whether the list is touched at all.
/// </para>
/// <para>
/// Nothing is invented here. Every string is the wizard's own, already worked out by
/// <see cref="ProfileWizard"/> - including the two that say a read has not happened yet rather than showing a
/// zero, because a database that only answers while the server runs is a database that is sometimes not
/// there.
/// </para>
/// </remarks>
/// <param name="CharId">The character's own GID, which is what makes two rows tellable apart.</param>
/// <param name="DisplayName">The wizard's name, or its id when no name could be read for it.</param>
/// <param name="SchoolText">The school, or that it could not be read.</param>
/// <param name="LevelText">The level, or that it could not be read.</param>
/// <param name="GoldText">The gold held, written out.</param>
/// <param name="QuestsText">How many quests are finished, written out.</param>
/// <param name="LastSeenText">When the character was last seen, or nothing when it has not been.</param>
public sealed record ProfileWizardRow(
    ulong CharId,
    string DisplayName,
    string SchoolText,
    string LevelText,
    string GoldText,
    string QuestsText,
    string LastSeenText) {

    /// <summary>
    /// The rows for an account's wizards, in the order the launcher learned them.
    /// </summary>
    /// <remarks>
    /// The order is the profile's own list rather than a sort of this method's own: a wizard is added when
    /// the server first reports it, and a list that reordered itself as a level moved would move a row out
    /// from under somebody reading it.
    /// </remarks>
    /// <param name="profile">Whose wizards these are, or null for a launcher with no profile on screen.</param>
    public static IReadOnlyList<ProfileWizardRow> For(LauncherProfile? profile)
        => profile is null
            ? []
            : [.. profile.Wizards.Select(wizard => new ProfileWizardRow(
                wizard.CharId,
                wizard.DisplayName,
                wizard.SchoolText,
                wizard.LevelText,
                wizard.GoldText,
                wizard.QuestsText,
                wizard.LastSeenText))];
}
