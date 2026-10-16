namespace ViewLight.Models;

/// <summary>
/// How a profile stands with the server's own command log: clean, or commands have been used on it.
/// </summary>
/// <remarks>
/// This is a marker, not a judgement, and it says only what the server recorded: Imlight writes every
/// command it is sent to <c>CommandLog</c>, with the account it came from. An account that has used
/// none stands clean, which is what gives a local server's accomplishments their weight - nobody
/// handed the levels out.
/// </remarks>
public enum ProfileStanding {

    /// <summary>No command has been used on this account, as far as the server's log goes.</summary>
    Clean,

    /// <summary>At least one command has been used. The profile shows how many, and the last one.</summary>
    CommandsUsed,
}

/// <summary>
/// One of the launcher's own profiles: what a Wizard101 account is to <i>this machine</i>.
/// </summary>
/// <remarks>
/// <para>
/// The account itself is the server's - it lives in Imlight's player database and the launcher only ever
/// reads it. Everything here is the launcher's own: the name and picture the profile is shown under,
/// how long it has played, which wizards it has, and which achievements it has been given. None of it is
/// sent anywhere, which is the point: this is a local profile system for a local server.
/// </para>
/// <para>
/// The counters below are the raw material achievements are judged on, and they are kept rather than
/// recomputed because their sources are not always there: the player database only answers while the
/// server runs, and the server log only goes back so far. A profile that has never been seen playing
/// still knows what it has done.
/// </para>
/// </remarks>
public sealed class LauncherProfile {

    /// <summary>The account GID this profile belongs to. The key, because it is what Play signs in with.</summary>
    public ulong AccountId { get; set; }

    /// <summary>The account's username as the server last had it. Only ever presented.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// What the profile calls itself. Blank means "use the username", so a profile with no opinion written
    /// out still follows a renamed account.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Avatar image file inside the profile's own folder, or null for the initial-letter one.</summary>
    public string? AvatarFile { get; set; }

    /// <summary>Banner image file inside the profile's own folder, or null for the plain banner.</summary>
    public string? BannerFile { get; set; }

    /// <summary>The first time Play started a session on this account.</summary>
    public DateTimeOffset? FirstPlayedAt { get; set; }

    /// <summary>The most recent moment this account was playing - a session starting, or one being credited.</summary>
    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>
    /// Time spent playing on the server this launcher runs, in seconds, added up across sessions.
    /// </summary>
    /// <remarks>
    /// Measured from the launcher's own session - the server it started and the game it started, both of
    /// them still running - rather than from the server's database saying the account is in the game, which
    /// is still true of a machine that is only idling on the profile screen. It is only ever this launcher's
    /// own server, because that is the only one it can observe. Sessions that happened while ViewLight was
    /// closed, or against another machine's server, are not counted and cannot be: nothing tells this
    /// launcher about them.
    /// </remarks>
    public long PlaytimeSeconds { get; set; }

    /// <summary>How many sessions Play has started on this account.</summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// The last look at a running session, which is what playtime is measured between.
    /// </summary>
    /// <remarks>
    /// Kept so a launcher closed mid-session does not lose the part of the session it did see, and cleared
    /// when a session ends or when the game connection goes: playtime is only ever counted between two looks
    /// that both found the game running. The name is a leftover from when it was the last look at the
    /// server's online list, and it is kept as it is because that is the field profiles are stored under.
    /// </remarks>
    public DateTimeOffset? LastSeenOnlineAt { get; set; }

    /// <summary>The zones this profile's wizard has been seen entering, by the server's own zone names.</summary>
    public List<string> ZonesVisited { get; set; } = [];

    /// <summary>
    /// The worlds this profile has been seen in, by the segment of the server's zone path that names them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A world is not something the log ever says outright: it is the first segment of the path the server
    /// loads a zone from (<c>WizardCity/WC_Hub</c>), and which zone that path is comes from the same block of
    /// the log - so the pairing is learned before a world can be recorded here. See <see cref="ProfileLedger"/>.
    /// </para>
    /// <para>
    /// Kept rather than derived, because the pairing is a thing the launcher learns and can forget: on a
    /// fresh start it has no idea which world Unicorn Way is in until the server loads a zone of it again,
    /// and what this account has already discovered must not depend on that having happened yet.
    /// </para>
    /// </remarks>
    public List<string> WorldsVisited { get; set; } = [];

    /// <summary>Whether this profile has been seen in a world.</summary>
    public bool HasVisitedWorld(string world)
        => WorldsVisited.Any(w => string.Equals(w, world, StringComparison.OrdinalIgnoreCase));

    /// <summary>Duels the profile's player won, as the server's log recorded them.</summary>
    public int DuelsWon { get; set; }

    /// <summary>Duels the profile's player lost, likewise.</summary>
    public int DuelsLost { get; set; }

    /// <summary>
    /// The creature templates this account has beaten, as the server named them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held as template IDs rather than names, because the ID is what the game's own creature list is
    /// keyed by - so a name can be looked up, corrected or translated later without touching this, and two
    /// creatures that happen to share a name stay tellable apart.
    /// </para>
    /// <para>
    /// Only ever added to. A creature beaten on this server stays beaten: a badge that could be taken away
    /// by a later loss would be no badge at all, and nothing here is a claim about right now.
    /// </para>
    /// </remarks>
    public List<uint> CreaturesDefeated { get; set; } = [];

    /// <summary>Whether this creature has been beaten on this server.</summary>
    public bool HasBeaten(uint creatureTemplateId) => CreaturesDefeated.Contains(creatureTemplateId);

    /// <summary>
    /// How many creatures this account has beaten, counted from the duels it won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A count rather than a list, because a duel does not name what it was against. The server writes
    /// which slots a spell reached and which slots a creature planned a turn on, and neither carries an ID:
    /// the participant line has a debug name on it (<c>140DD</c>), not a template. So a creature counted
    /// here is a slot on one of the creatures' circles that a spell of the player's side reached in a duel
    /// the players won - which is what the log supports, and what the profile screen says it is. The
    /// creatures that <i>are</i> identified, because the server named them, land in
    /// <see cref="CreaturesDefeated"/> instead, and that list is what a boss badge needs.
    /// </para>
    /// <para>
    /// Only ever added to. A creature beaten stays beaten, and nothing here is a claim about right now.
    /// </para>
    /// </remarks>
    public int CreaturesKilled { get; set; }

    /// <summary>
    /// How many of the creatures in <see cref="CreaturesDefeated"/> are bosses.
    /// </summary>
    /// <remarks>
    /// Whether a creature is a boss is a fact about the creature, not about the fight, so it is learned
    /// from the server's own creature list rather than from anything this profile watched: the list is
    /// what says a given template is a boss. It is re-counted every time that list is read, and left at
    /// its last answer while the list is missing - so which badges are reachable cannot turn on which
    /// files happen to be on disk.
    /// </remarks>
    public int BossesDefeated { get; set; }

    /// <summary>
    /// Re-counts the beaten creatures against the server's creature list.
    /// </summary>
    /// <remarks>
    /// A creature the list does not have is counted as nothing rather than as an ordinary one. Calling it a
    /// mob because it is not among the bosses would be a claim about it, and the only honest thing to say
    /// about one the list does not name is nothing - it stays beaten, and it counts towards the plain duel
    /// totals, which never needed to know what it was.
    /// </remarks>
    /// <param name="bosses">The creatures the list names as bosses.</param>
    /// <returns>How many of the beaten creatures the list calls bosses.</returns>
    public int ClassifyBeatenCreatures(IReadOnlySet<uint> bosses) {
        var bossCount = 0;

        foreach (var creature in CreaturesDefeated) {
            if (bosses.Contains(creature)) {
                bossCount++;
            }
        }

        BossesDefeated = bossCount;
        return bossCount;
    }

    /// <summary>The best level this account has reached as a wizard of one school.</summary>
    /// <remarks>
    /// Per school rather than overall, because a badge about playing a Storm wizard to level 20 is a
    /// different thing from having any wizard at all at level 20 - and an account with one of each would
    /// otherwise earn every school's badge from a single character.
    /// </remarks>
    public int HighestLevelIn(string school)
        => string.IsNullOrWhiteSpace(school)
            ? 0
            : Wizards.Where(w => string.Equals(w.School, school, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Level)
                .DefaultIfEmpty(0)
                .Max();

    /// <summary>
    /// How many different spells the account has learned, across all its wizards.
    /// </summary>
    /// <remarks>
    /// Counted as distinct rather than summed, because two wizards learning the same spell is one spell
    /// known by the account - and summing would let a second wizard double a total that is meant to be
    /// about a collection.
    /// </remarks>
    public int DistinctSpellsLearned => Wizards.SelectMany(w => w.LearnedSpells).Distinct().Count();

    /// <summary>Commands this launcher watched go by in the server's log.</summary>
    public int CommandsSeenHere { get; set; }

    /// <summary>
    /// Commands Imlight's own <c>CommandLog</c> collection holds for this account.
    /// </summary>
    /// <remarks>
    /// Read rather than derived, because the server is the one that records it - and it records every
    /// command before running it, including ones that fail or are refused.
    /// </remarks>
    public int CommandsOnRecord { get; set; }

    /// <summary>
    /// How many commands have been used on this account.
    /// </summary>
    /// <remarks>
    /// The larger of the two counts, never their sum: they are the same commands seen through two mirrors
    /// - the log this launcher watched, and the server's own record - so adding them would count a command
    /// twice and make the standing worse than the server says it is.
    /// </remarks>
    public int CommandsUsed => Math.Max(CommandsSeenHere, CommandsOnRecord);

    /// <summary>The most recent command text the server logged for this account, if any.</summary>
    public string? LastCommand { get; set; }

    /// <summary>When that command was used.</summary>
    public DateTimeOffset? LastCommandAt { get; set; }

    /// <summary>The wizards this account has, as the player database describes them.</summary>
    public List<ProfileWizard> Wizards { get; set; } = [];

    /// <summary>The achievements this profile has been given, oldest first.</summary>
    public List<EarnedAchievement> Achievements { get; set; } = [];

    /// <summary>What the profile is called on screen: its own name, or the account's.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    /// <summary>
    /// The name, so a picker listing profiles shows people rather than the type they are stored in.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Clean until the server's command log says otherwise.</summary>
    public ProfileStanding Standing => CommandsUsed > 0 ? ProfileStanding.CommandsUsed : ProfileStanding.Clean;

    public bool IsClean => Standing == ProfileStanding.Clean;

    /// <summary>Whether this profile has been given an achievement.</summary>
    public bool Has(string achievementId)
        => Achievements.Any(a => string.Equals(a.AchievementId, achievementId, StringComparison.Ordinal));

    /// <summary>
    /// Every number achievements are judged on, gathered from the profile itself so there is one source
    /// for them rather than one per screen.
    /// </summary>
    public ProfileCounters Counters => new(
        PlaytimeSeconds,
        SessionCount,
        ZonesVisited.Count,
        DuelsWon,
        DuelsLost,
        QuestsCompleted,
        HighestLevel,
        HighestGold,
        CommandsUsed);

    /// <summary>
    /// Quest completions across the account's wizards.
    /// </summary>
    /// <remarks>
    /// A quest counts when Imlight has written its <c>&lt;quest&gt;_Complete</c> registry entry, which the
    /// server only does when the last goal closes the quest - so a quest half done is not counted.
    /// </remarks>
    public int QuestsCompleted => Wizards.Sum(w => w.QuestsCompleted);

    /// <summary>The highest level any of the account's wizards has reached.</summary>
    public int HighestLevel => Wizards.Count == 0 ? 0 : Wizards.Max(w => w.Level);

    /// <summary>The most gold any of the account's wizards has held, as last read.</summary>
    public long HighestGold => Wizards.Count == 0 ? 0 : Wizards.Max(w => w.Gold);
}

/// <summary>Everything achievements are judged on, so the rules read as numbers rather than as fields.</summary>
/// <param name="PlaytimeSeconds">Total time signed in to the local server.</param>
/// <param name="Sessions">Times the account has been seen signing in.</param>
/// <param name="ZonesVisited">Distinct zones entered.</param>
/// <param name="DuelsWon">Duels the server's log recorded as won by players.</param>
/// <param name="DuelsLost">Duels it recorded as won by creatures.</param>
/// <param name="QuestsCompleted">Quests Imlight has marked complete for this account's wizards.</param>
/// <param name="HighestLevel">The best level among the account's wizards.</param>
/// <param name="HighestGold">The most gold among them.</param>
/// <param name="CommandsUsed">Commands the server's command log holds for this account.</param>
public sealed record ProfileCounters(
    long PlaytimeSeconds,
    int Sessions,
    int ZonesVisited,
    int DuelsWon,
    int DuelsLost,
    int QuestsCompleted,
    int HighestLevel,
    long HighestGold,
    int CommandsUsed) {

    public static ProfileCounters None => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A wizard of the account, as the player database has it.
/// </summary>
/// <remarks>
/// Imlight does not store a wizard's name as text: it stores the indices it was built from and resolves
/// them through its own name tables, which the launcher does not have. So the name here is the one the
/// server log used for a wizard signing in, matched to this character when it could be - and blank when
/// it could not, in which case the character is presented by its id rather than under someone else's name.
/// </remarks>
public sealed class ProfileWizard {

    /// <summary>The character's GID.</summary>
    public ulong CharId { get; set; }

    /// <summary>The name the server log used for this wizard, if it could be matched to the character.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The wizard's level, from <c>MagicSchoolBehavior.Level</c>.</summary>
    public int Level { get; set; }

    /// <summary>Gold held, from the character's game stats.</summary>
    public long Gold { get; set; }

    /// <summary>
    /// Quests this wizard has fully completed: the <c>&lt;quest&gt;_Complete</c> keys in its quest registry.
    /// </summary>
    public int QuestsCompleted { get; set; }

    /// <summary>
    /// The wizard's school, named, or empty when it has none the launcher recognises.
    /// </summary>
    /// <remarks>
    /// A wizard's school does not change, so it is only ever filled in, never merged or compared.
    /// </remarks>
    public string School { get; set; } = string.Empty;

    /// <summary>
    /// The spell templates in this wizard's spellbook.
    /// </summary>
    /// <remarks>
    /// Added to and never removed: a spell learned is learned, and a spell taken out of a deck is still
    /// known. Duplicates cannot arise from the server, which keeps its own list unique, but the merge
    /// checks anyway so a repeated read cannot inflate a badge's count.
    /// </remarks>
    public List<uint> LearnedSpells { get; set; } = [];

    /// <summary>When this character was last seen at all - by the log, or in the online list.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The name to show: the wizard's own, or its id when the name was never read.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Character {CharId}" : Name;

    /// <summary>
    /// The school, named, or that there is none to name.
    /// </summary>
    /// <remarks>
    /// A wizard whose school the launcher could not read is not given a default: "no school" is a real
    /// answer, and one wizard being shown as Balance because that is what fell out of an unread field would
    /// be worse than admitting the read did not work.
    /// </remarks>
    public string SchoolText => string.IsNullOrWhiteSpace(School) ? "School not read" : School;

    /// <summary>
    /// The level as the profile shows it.
    /// </summary>
    /// <remarks>
    /// A level that has not been read is said to have not been read rather than shown as zero: the player
    /// database is the only place it comes from, and it only answers while the server runs.
    /// </remarks>
    public string LevelText => Level <= 0 ? "Level not read yet" : $"Level {Level}";

    public string GoldText => $"{Gold:N0} gold";

    public string QuestsText => QuestsCompleted switch {
        0 => "no quests finished",
        1 => "1 quest finished",
        var count => $"{count} quests finished",
    };

    public string LastSeenText => LastSeenAt is { } seen
        ? $"last seen {seen.ToLocalTime():yyyy-MM-dd HH:mm}"
        : string.Empty;
}

/// <summary>
/// Everything the launcher remembers about profiles, as it is written to <c>profiles.json</c>.
/// </summary>
/// <remarks>
/// A named object rather than a bare array, so a later version can put something else beside the profiles
/// without every existing file failing to load.
/// </remarks>
public sealed class LauncherProfiles {

    public List<LauncherProfile> Profiles { get; set; } = [];

    /// <summary>
    /// Which world each zone belongs to, as the launcher has learned it.
    /// </summary>
    /// <remarks>
    /// Kept beside the profiles rather than in the settings file because it is learned from the server the
    /// same way a profile's history is, and it grows the same way: one entry per zone the server loads. It
    /// is the launcher's own note about a server's layout, and nothing reads it anywhere else.
    /// </remarks>
    public Dictionary<string, string> ZoneWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An achievement a profile has been given, with what it was given for.
/// </summary>
/// <param name="AchievementId">The catalog id, which is what the badge is looked up by.</param>
/// <param name="EarnedAt">When the launcher gave it - the moment the condition was seen to hold.</param>
/// <param name="Evidence">
/// The line the badge came from, quoted for the profile: what the server said that made it true. Kept so a
/// badge can be pointed at something rather than being an assertion.
/// </param>
public sealed record EarnedAchievement(string AchievementId, DateTimeOffset EarnedAt, string Evidence);

/// <summary>
/// An achievement that has just been given, and who it was given to.
/// </summary>
/// <remarks>
/// Carried together because the caller has to say so - "Adventurer earned by Jordan" is a sentence about
/// both - and looking the owner up afterwards would mean guessing from a badge id that every profile can
/// have.
/// </remarks>
public sealed record ProfileAward(LauncherProfile Profile, EarnedAchievement Achievement);
