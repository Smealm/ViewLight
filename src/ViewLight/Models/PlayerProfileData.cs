namespace ViewLight.Models;

/// <summary>
/// What the player database says about profiles: the wizards an account has, who is signed in right now,
/// and every command the server has seen used.
/// </summary>
/// <remarks>
/// <para>
/// Read the same way the accounts are - over the embedded database's HTTP port while Imlight is running -
/// and guarded the same way, but <b>separately guarded</b>. The accounts decide whether automatic sign-in
/// works, and a wizard document the launcher does not understand must never switch that off; the reverse
/// matters too, so a database that answers oddly here turns the badges off and says so while leaving
/// sign-in alone.
/// </para>
/// <para>
/// Nothing here is invented. A wizard is not named, because Imlight stores a wizard's name as indices
/// into its own name tables rather than as text - so names come from the server log and are matched to a
/// character when they can be, and never guessed at.
/// </para>
/// </remarks>
public sealed record PlayerProfileData(
    IReadOnlyList<PlayerWizardRecord> Wizards,
    IReadOnlyList<PlayerOnlineRecord> Online,
    IReadOnlyList<PlayerCommandRecord> Commands,
    string? Problem) {

    public static PlayerProfileData Unreadable(string problem) => new([], [], [], problem);

    /// <summary>Whether everything read back looked the way this launcher expects.</summary>
    public bool IsUsable => Problem is null;

    public static PlayerProfileData Empty { get; } = new([], [], [], null);
}

/// <summary>An account's wizard, as the character document describes it.</summary>
/// <param name="AccountId">The account that owns the wizard.</param>
/// <param name="CharId">The character's own GID.</param>
/// <param name="Level">From <c>MagicSchoolBehavior.Level</c>.</param>
/// <param name="Gold">From the character's game stats.</param>
/// <param name="CompletedQuests">
/// The quests whose <c>&lt;name&gt;_Complete</c> registry entry the server wrote, which happens only when
/// the last goal of a quest closes it - a quest in progress is not in here.
/// </param>
/// <param name="School">
/// The wizard's school, named, from <c>MagicSchoolBehavior.MagicSchool</c>. Empty for a wizard with no
/// school set, or one whose school is not one of the seven a player can be - there is nothing to say about
/// a school the launcher does not know, so it says nothing rather than naming it wrong.
/// </param>
/// <param name="LearnedSpells">
/// The spell templates in the wizard's spellbook, from <c>SpellbookBehavior.LearnedSpellTemplateIds</c>.
/// </param>
/// <param name="Name">
/// The wizard's name as the document carries it outright, from <c>PlayerNameBehavior.NameOverride</c>. Empty
/// for the usual case, where the name is stored as indices into the game's name tables instead.
/// </param>
/// <param name="NameKeys">
/// The packed name the wizard was built from, from <c>PlayerNameBehavior.NameIndices</c>: one byte each for
/// the first, middle and last name. Null when the document does not say.
/// </param>
/// <param name="Gender">
/// Which of the game's two first-name tables the key means, from <c>PlayerNameBehavior.Gender</c>. Null when
/// the document does not say, in which case no name is resolved from the key - the two tables are different
/// lists, so reading the wrong one would put someone else's first name on the character.
/// </param>
public sealed record PlayerWizardRecord(
    ulong AccountId,
    ulong CharId,
    int Level,
    long Gold,
    IReadOnlyList<string> CompletedQuests,
    string School = "",
    IReadOnlyList<uint>? LearnedSpells = null,
    string Name = "",
    uint? NameKeys = null,
    uint? Gender = null) {

    /// <summary>The spells this wizard knows, never null, so callers do not have to say so.</summary>
    public IReadOnlyList<uint> Spells => LearnedSpells ?? [];
}

/// <summary>
/// A player who is in the game right now, as the server's own online list has them.
/// </summary>
/// <remarks>
/// Imlight writes one of these when a wizard attaches to a game server and deletes it when the session
/// ends, so its presence is the server's answer to "is this account signed in" - which is what playtime
/// is measured from.
/// </remarks>
public sealed record PlayerOnlineRecord(ulong AccountId, ulong CharId, string Zone, string Realm);

/// <summary>
/// A command the server logged, with the account it came from.
/// </summary>
/// <remarks>
/// Imlight writes one of these for every command before it runs it, so this is the server's own record of
/// what was typed - which is what makes a profile's standing worth anything.
/// </remarks>
public sealed record PlayerCommandRecord(ulong AccountId, ulong CharId, DateTimeOffset At, string Text);
