using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What kind of thing an achievement is about, which is how the profile groups them.</summary>
public enum AchievementCategory {
    Playtime,
    Exploration,

    /// <summary>Won duels: how many battles you have come through.</summary>
    Battle,

    /// <summary>Things owned - gear, wands, decks - grouped by what they are.</summary>
    Collectathon,

    /// <summary>How far a wizard has got: levels, per school.</summary>
    Account,

    /// <summary>Spells learned, and a whole school learned through.</summary>
    Spells,

    Progress,
    Record,
}

/// <summary>
/// Where the truth behind an achievement comes from - and so what has to be there for it to be earned.
/// </summary>
/// <remarks>
/// Spelled out per achievement rather than left implicit, because the three sources come and go: the
/// player database only answers while the server runs, and two of the three need Imlight to be logging at
/// debug level. A badge that can never light up because its source is off is worth saying so about.
/// </remarks>
public enum AchievementSource {

    /// <summary>Counted by ViewLight while it is running the server - playtime and sessions.</summary>
    GameSession,

    /// <summary>Read out of the server's own log.</summary>
    ServerLog,

    /// <summary>Read out of the player database Imlight hosts while it runs.</summary>
    PlayerDatabase,
}

/// <summary>
/// How hard an achievement is, which is what its score is made of.
/// </summary>
/// <remarks>
/// Deliberately coarse: the point of the score is to make a rare badge worth more than a common one, and
/// a ladder of four is enough to say that without pretending the difference between two achievements can
/// be measured exactly. The score itself lives in <see cref="AchievementScores"/>, so every badge of a
/// difficulty is worth the same and the numbers are in one place.
/// </remarks>
public enum AchievementDifficulty {

    /// <summary>Earned by turning up - the first sign-in, the first step into a starting zone.</summary>
    Easy,

    /// <summary>A few hours of play, or a first real milestone.</summary>
    Standard,

    /// <summary>Real time in, or a milestone most players do not reach.</summary>
    Hard,

    /// <summary>A hundred hours, a hundred duels, the last of a progression.</summary>
    Legendary,
}

/// <summary>What each difficulty is worth, in one place so the score can be explained.</summary>
public static class AchievementScores {

    /// <summary>Points a badge of each difficulty is worth.</summary>
    public static int For(AchievementDifficulty difficulty) => difficulty switch {
        AchievementDifficulty.Easy => 10,
        AchievementDifficulty.Standard => 25,
        AchievementDifficulty.Hard => 50,
        _ => 100,
    };

    /// <summary>
    /// What a clean account's achievements are worth, as against one that has had commands used on it.
    /// </summary>
    /// <remarks>
    /// Doubling is the whole reward, and it is a reward rather than a penalty: a profile with commands on
    /// it is not accused of anything - the server records every command it is sent, and an administrator
    /// fixing somebody's stuck quest uses one - it simply does not get the bonus that playing honestly is
    /// worth. The alternative would be to take the score off an account for using the commands the server
    /// ships with, which is a judgement this launcher has no business making.
    /// </remarks>
    public const int CleanMultiplier = 2;

    /// <summary>
    /// What a profile scores for the achievements it holds, bonus included.
    /// </summary>
    /// <remarks>
    /// Summed from the catalog rather than stored, so a badge's score can be revised without rewriting
    /// anybody's profile, and a badge whose id is no longer in the catalog cannot contribute points to a
    /// total no screen could explain.
    /// </remarks>
    public static int Of(LauncherProfile profile)
        => profile.Achievements.Sum(a => ByIdOf(a.AchievementId)) * MultiplierFor(profile);

    /// <summary>What every achievement counts for on this profile.</summary>
    public static int MultiplierFor(LauncherProfile profile) => profile.IsClean ? CleanMultiplier : 1;

    /// <summary>
    /// Why a profile's score is doubled or is not, said in one line.
    /// </summary>
    /// <remarks>
    /// Said out loud rather than left as an invisible multiplier: a score that does not match the badges
    /// added up is a score nobody can explain, and the reason is the one thing that makes it worth having.
    /// </remarks>
    public static string MultiplierNote(LauncherProfile? profile) => profile switch {
        null => string.Empty,
        { IsClean: true } => $"Clean server — every badge counts {CleanMultiplier}×.",
        _ => "Commands have been used on this account, so its badges count once.",
    };

    private static int ByIdOf(string id)
        => AchievementCatalog.ById(id) is { } definition ? definition.Score : 0;

    /// <summary>The difficulty's name, in the same words the score is built from.</summary>
    public static string Name(AchievementDifficulty difficulty) => difficulty switch {
        AchievementDifficulty.Easy => "Easy",
        AchievementDifficulty.Standard => "Standard",
        AchievementDifficulty.Hard => "Hard",
        _ => "Legendary",
    };
}

/// <summary>
/// One achievement: what it is called, what earns it, and the rule that decides.
/// </summary>
/// <param name="Id">Stable id, which is what is written into a profile. Never re-used.</param>
/// <param name="Name">The badge's name.</param>
/// <param name="Requirement">What earns it, in the words the profile shows while it is still locked.</param>
/// <param name="Earned">The rule, judged against the profile as it stands.</param>
/// <param name="NeedsDebugLog">
/// Whether the server has to be logging at debug level for this. Zone entries and duel results are written
/// at <c>[DBG]</c>, which a stock <c>Logging.LogLevel = INFO</c> drops before it ever reaches the file.
/// </param>
/// <param name="Difficulty">How hard it is, which is what its score is made of.</param>
/// <param name="IsMilestone">
/// Whether this is a rung of a ladder rather than a badge of its own - one of hundreds generated from a
/// step and a cap. They are the ones that arrive in a run while somebody is levelling or farming, so they
/// are the ones the profile screen lets a player stop being told about.
/// </param>
/// <param name="TrackId">
/// The ladder this is a rung of, or null for a badge of its own. It is what lets the profile show the whole
/// ladder as one card with a bar on it instead of a tile per rung, and it is derived from the track rather
/// than stored separately so the two can never disagree about which rungs belong together.
/// </param>
public sealed record AchievementDefinition(
    string Id,
    string Name,
    string Requirement,
    AchievementCategory Category,
    AchievementSource Source,
    Func<LauncherProfile, bool> Earned,
    bool NeedsDebugLog = false,
    AchievementDifficulty Difficulty = AchievementDifficulty.Standard,
    bool IsMilestone = false,
    string? TrackId = null) {

    /// <summary>What this badge adds to a profile's score, before any multiplier.</summary>
    public int Score => AchievementScores.For(Difficulty);

    /// <summary>What it adds to a given profile's score, bonus included.</summary>
    public int PointsFor(LauncherProfile profile) => Score * AchievementScores.MultiplierFor(profile);
}

/// <summary>
/// One ladder of milestones: something that can be counted, the rungs it is counted in, and how to say
/// where an account has got to on it.
/// </summary>
/// <remarks>
/// The ladders are why the catalog can run to hundreds of badges without the profile screen becoming a wall
/// of them: a track is drawn as one card - where you are, what the next milestone is, how far away it is,
/// and what it is worth - and its rungs stay badges underneath, which is what is earned, stored and scored.
/// </remarks>
/// <param name="Id">Stable id, which its rungs' own ids are built from. Never re-used.</param>
/// <param name="Name">What the ladder is called on its card: "Storm Wizard", "Battles Won".</param>
/// <param name="Category">The group of the profile it belongs to.</param>
/// <param name="Source">Where its count comes from.</param>
/// <param name="Reached">How far along the ladder an account is, in the ladder's own units.</param>
/// <param name="Rungs">The thresholds, smallest first.</param>
/// <param name="Title">How a rung is named on its own: 15 -> "Level 15".</param>
/// <param name="Describe">How a value on the ladder is said: 12 -> "Level 12".</param>
/// <param name="Requirement">What earns a rung, in the words a locked badge shows.</param>
/// <param name="StepText">How far apart the rungs are, for the card's own line.</param>
/// <param name="NeedsDebugLog">Whether the count needs the server logging at debug level.</param>
public sealed record MilestoneTrack(
    string Id,
    string Name,
    AchievementCategory Category,
    AchievementSource Source,
    Func<LauncherProfile, int> Reached,
    IReadOnlyList<int> Rungs,
    Func<int, string> Title,
    Func<int, string> Describe,
    Func<int, string> Requirement,
    string StepText,
    bool NeedsDebugLog = false) {

    /// <summary>The top of the ladder, which is where it stops.</summary>
    public int Top => Rungs.Count == 0 ? 0 : Rungs[^1];

    /// <summary>How many rungs a value has passed.</summary>
    public int Passed(int reached) => Rungs.Count(rung => reached >= rung);

    /// <summary>The next rung up, or null when the ladder has been climbed to the top.</summary>
    public int? Next(int reached) {
        foreach (var rung in Rungs) {
            if (reached < rung) {
                return rung;
            }
        }

        return null;
    }
}

/// <summary>
/// Every achievement the launcher knows how to give, and the rules behind them.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is the whole list, deliberately: there is no way for a profile to hold a badge this file
/// does not define, and no way for one to be given twice. Adding an achievement is adding a row here.
/// </para>
/// <para>
/// <b>The badges about creatures exist, and are locked, and that is on purpose.</b> Beating a boss is
/// counted from the server's own creature list plus the duel that was won - so the half that is missing
/// is the server naming the creature in a duel at all. It logs a duel's participants by slot and runtime
/// id and never by template, so a win cannot be tied to a creature yet; see <see cref="DuelCreature"/>.
/// The badges are defined and judged like any other, so the day the server writes that line they start
/// being earned with no change here, and until then they are simply locked and the profile says why.
/// </para>
/// <para>
/// <b>What is genuinely not here: what you have collected.</b> Imlight's inventory keeps an item's
/// global id and nothing else - its template id is not written to the database, and no line in its log
/// records an item being granted by template - so there is no way to say which wands, robes or decks an
/// account owns. A collectathon badge would have to be counted from a list nothing writes, so there are
/// none; the item manifest the picker reads is the game's list, not the player's. Quests, levels, gold
/// and spells are all counted from the player database instead, where each is a fact the server wrote
/// down.
/// </para>
/// </remarks>
public static class AchievementCatalog {

    /// <summary>The zone names Imlight uses for the places starting achievements are about.</summary>
    public const string CommonsZone = "WizardZone_TheCommons";
    public const string UnicornWayZone = "WizardZone_UnicornWay";
    public const string GolemCourtZone = "WizardZone_GolemCourt";
    public const string RavenwoodZone = "WizardZone_Ravenwood";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The level cap Imlight's stock configuration ships with.</summary>
    public const int DefaultMaxLevel = 200;

    /// <summary>How far apart the levels of a school's ladder are.</summary>
    /// <remarks>
    /// Five, because that is the step the game's own progression makes worth marking: a rung every level
    /// would be a badge per level of play, and one every twenty would leave the middle of a wizard's life
    /// with nothing to show for it.
    /// </remarks>
    public const int LevelMilestoneStep = 5;

    /// <summary>How far apart the duels of the battle ladder are.</summary>
    public const int BattleMilestoneStep = 10;

    /// <summary>How many rungs the battle ladder has, so it ends at <c>10 * 50</c> duels.</summary>
    public const int BattleMilestoneCount = 50;

    /// <summary>How far apart the rungs of the creature ladder are.</summary>
    /// <remarks>
    /// Twenty-five, because the server's creature list runs to hundreds: a rung every ten would put one on
    /// nearly every creature anybody beats, which is the bloating the ladders exist to avoid.
    /// </remarks>
    public const int CreatureMilestoneStep = 25;

    /// <summary>The top of the creature ladder when the server's own creature list has not been read.</summary>
    public const int DefaultCreatureCap = 500;

    /// <summary>
    /// The highest level this server allows, which is how far the level ladders run.
    /// </summary>
    /// <remarks>
    /// Read from the server's <c>Character.MaxLevel</c> rather than assumed, because it is the server that
    /// decides where a wizard's levels stop - a cap of 200 has forty rungs per school and a cap of 60 has
    /// twelve, and writing either out by hand would be wrong on the other's server.
    /// </remarks>
    public static int MaxLevel { get; private set; } = DefaultMaxLevel;

    /// <summary>
    /// How far the creature ladder runs: the size of this server's own creature list, rounded up to a rung.
    /// </summary>
    /// <remarks>
    /// The same idea as <see cref="MaxLevel"/> - a player cannot beat more different creatures than the
    /// server's list holds, so a top rung fixed in code would either stop short of what is reachable or sit
    /// above it forever.
    /// </remarks>
    public static int CreatureCap { get; private set; } = DefaultCreatureCap;

    /// <summary>The ladders, one card each on the profile rather than a badge per rung.</summary>
    public static IReadOnlyList<MilestoneTrack> Tracks => Book.Tracks;

    public static IReadOnlyList<AchievementDefinition> All => Book.All;

    /// <summary>
    /// The worlds this server has, as learned from its own log, one badge apiece.
    /// </summary>
    /// <remarks>
    /// A world is only known once the server has been seen loading a zone of it - see <see cref="ProfileLedger"/> -
    /// so this starts empty on a fresh install and fills in as the server runs. That is the honest shape: the
    /// alternative is a list of world names written down here, which is a guess about a server's layout that
    /// would be wrong on somebody else's.
    /// </remarks>
    public static IReadOnlyList<string> Worlds { get; private set; } = [];

    /// <summary>
    /// Everything the catalog holds, swapped as one object so a reader never sees half a rebuild.
    /// </summary>
    private static CatalogBook Book { get; set; } = Build(DefaultMaxLevel, DefaultCreatureCap, []);

    /// <summary>One build of the catalog: its ladders, and every badge those and the fixed list add up to.</summary>
    private sealed record CatalogBook(
        IReadOnlyList<MilestoneTrack> Tracks, IReadOnlyList<AchievementDefinition> All);

    /// <summary>
    /// Rebuilds the catalog for a server whose level cap is <paramref name="maxLevel"/>.
    /// </summary>
    /// <remarks>
    /// The catalog is process-wide, so this is too - which is fine because it changes at most once per run,
    /// when the server's own config is read. A level cap that did not come back sensible is left alone
    /// rather than clamped to something invented: the stock cap stands, and the profile screen's ladders
    /// are then simply longer or shorter than that server's rather than wrong about it.
    /// </remarks>
    public static void UseMaxLevel(int maxLevel) {
        if (maxLevel < LevelMilestoneStep || maxLevel == MaxLevel) {
            return;
        }

        MaxLevel = maxLevel;
        Book = Build(maxLevel, CreatureCap, Worlds);
    }

    /// <summary>
    /// Rebuilds the catalog for the worlds the server is known to have.
    /// </summary>
    /// <remarks>
    /// Nothing else changes with them: a world only ever adds a badge, so a re-read of the same worlds is
    /// left alone and the profile screen is not rebuilt under somebody for nothing.
    /// </remarks>
    public static void UseWorlds(IReadOnlyList<string> worlds) {
        var known = worlds
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        if (known.SequenceEqual(Worlds)) {
            return;
        }

        Worlds = known;
        Book = Build(MaxLevel, CreatureCap, known);
    }

    /// <summary>
    /// Rebuilds the catalog for a server whose own creature list holds <paramref name="creatures"/> of them.
    /// </summary>
    /// <remarks>
    /// Rounded up to the ladder's own step, because a top rung at 317 creatures is not a milestone anybody
    /// would recognise. A count that did not come back sensible leaves the stock top rung in place rather
    /// than shrinking the ladder to nothing.
    /// </remarks>
    public static void UseCreatureCount(int creatures) {
        var cap = creatures <= 0
            ? DefaultCreatureCap
            : (creatures + CreatureMilestoneStep - 1) / CreatureMilestoneStep * CreatureMilestoneStep;

        if (cap == CreatureCap) {
            return;
        }

        CreatureCap = cap;
        Book = Build(MaxLevel, cap, Worlds);
    }

    /// <summary>
    /// Everything the catalog holds, for a server with these limits.
    /// </summary>
    /// <remarks>
    /// Takes the limits as arguments rather than reading <see cref="MaxLevel"/> and <see cref="CreatureCap"/>
    /// so that the first build - which happens while the static fields are still being initialized - cannot
    /// read fields that have not been set yet.
    /// </remarks>
    private static CatalogBook Build(int maxLevel, int creatureCap, IReadOnlyList<string> worlds) {
        var tracks = new List<MilestoneTrack>();

        foreach (var school in WizardSchool.Names) {
            tracks.Add(LevelTrack(school, maxLevel));
        }

        tracks.Add(BattleTrack());
        tracks.Add(CreatureTrack(creatureCap));

        return new CatalogBook(
            tracks,
            [.. FixedBadges(), .. WorldBadges(worlds), .. tracks.SelectMany(RungsFor)]);
    }

    /// <summary>
    /// One badge per world, earned by being in it.
    /// </summary>
    /// <remarks>
    /// Generated from the worlds the server itself has been seen to load, which is what makes this a fact
    /// about this server rather than a list of the game's worlds guessed at here.
    /// </remarks>
    private static IEnumerable<AchievementDefinition> WorldBadges(IReadOnlyList<string> worlds) {
        foreach (var world in worlds) {
            yield return new AchievementDefinition(
                $"world-{world.ToLowerInvariant()}",
                $"Discover {WorldName(world)}",
                $"Set foot in {WorldName(world)}, one of the worlds your server runs.",
                AchievementCategory.Exploration,
                AchievementSource.ServerLog,
                profile => profile.HasVisitedWorld(world),
                NeedsDebugLog: true,
                Difficulty: AchievementDifficulty.Standard);
        }
    }

    /// <summary>
    /// A world said the way the game says it: <c>WizardCity</c> is Wizard City.
    /// </summary>
    /// <remarks>
    /// A world label is the first segment of a zone path, run together the same way a zone name is, so it is
    /// split up the same way - see <see cref="ZoneNames"/>. Nothing is added to it: a label the launcher
    /// cannot make sense of is shown exactly as the server wrote it.
    /// </remarks>
    public static string WorldName(string world) => ZoneNames.Spelled(world);

    /// <summary>Every badge that is not a rung of a ladder.</summary>
    private static IReadOnlyList<AchievementDefinition> FixedBadges() => [
        // ------------------------------------------------------------ time on the server
        new("first-steps", "First Steps", "Sign in to your own server once.",
            AchievementCategory.Playtime, AchievementSource.GameSession, p => p.SessionCount >= 1,
            Difficulty: AchievementDifficulty.Easy),

        new("settling-in", "Settling In", "Spend an hour signed in to your own server.",
            AchievementCategory.Playtime, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour,
            Difficulty: AchievementDifficulty.Standard),

        new("ten-hours", "Ten Hours In", "Spend ten hours signed in to your own server.",
            AchievementCategory.Playtime, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour * 10,
            Difficulty: AchievementDifficulty.Hard),

        new("hundred-hours", "A Hundred Hours", "Spend a hundred hours signed in to your own server.",
            AchievementCategory.Playtime, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour * 100,
            Difficulty: AchievementDifficulty.Legendary),

        new("five-hours", "Five Hours In", "Spend five hours signed in to your own server.",
            AchievementCategory.Playtime, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour * 5,
            Difficulty: AchievementDifficulty.Standard),

        new("twenty-hours", "Twenty Hours In", "Spend twenty hours signed in to your own server.",
            AchievementCategory.Playtime, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour * 20,
            Difficulty: AchievementDifficulty.Hard),

        new("regular", "A Regular", "Sign in to your own server twenty-five times.",
            AchievementCategory.Playtime, AchievementSource.GameSession, p => p.SessionCount >= 25,
            Difficulty: AchievementDifficulty.Hard),

        // ------------------------------------------------------------ where you have been
        new("the-commons", "The Commons", "Set foot in the Commons.",
            AchievementCategory.Exploration, AchievementSource.ServerLog, p => HasVisited(p, CommonsZone),
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        new("unicorn-way", "Unicorn Way", "Set foot in Unicorn Way.",
            AchievementCategory.Exploration, AchievementSource.ServerLog, p => HasVisited(p, UnicornWayZone),
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        new("golem-court", "Golem Court", "Set foot in Golem Court.",
            AchievementCategory.Exploration, AchievementSource.ServerLog, p => HasVisited(p, GolemCourtZone),
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        new("ravenwood", "Ravenwood", "Set foot in Ravenwood.",
            AchievementCategory.Exploration, AchievementSource.ServerLog, p => HasVisited(p, RavenwoodZone),
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        new("wanderer", "Wanderer", "Visit ten different zones.",
            AchievementCategory.Exploration, AchievementSource.ServerLog, p => p.ZonesVisited.Count >= 10,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Hard),

        // ------------------------------------------------------------ what you have won
        new("first-victory", "First Victory", "Win a duel.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.DuelsWon >= 1,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        new("took-a-fall", "Took a Fall", "Lose a duel. It happens to everyone.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.DuelsLost >= 1,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Easy),

        // ------------------------------------------------------------------ the bosses
        // Counted as distinct creatures rather than as wins, because beating one boss ten times is not
        // beating ten bosses - and the server's creature list is what decides which of them are bosses.
        new("first-boss", "A Boss Down", "Beat a boss.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.BossesDefeated >= 1,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Standard),

        new("five-bosses", "Five Bosses", "Beat five different bosses.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.BossesDefeated >= 5,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Hard),

        new("twenty-five-bosses", "Twenty-Five Bosses", "Beat twenty-five different bosses.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.BossesDefeated >= 25,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Hard),

        new("hundred-bosses", "A Hundred Bosses", "Beat a hundred different bosses.",
            AchievementCategory.Battle, AchievementSource.ServerLog, p => p.BossesDefeated >= 100,
            NeedsDebugLog: true, Difficulty: AchievementDifficulty.Legendary),

        // Every tenth duel won and every twenty-fifth creature beaten are the two battle ladders rather than
        // badges here: one card each on the profile with a bar to the next milestone, instead of the three
        // fixed thresholds for creatures that used to sit in this list and say the same thing again.

        // ------------------------------------------------------------ how far you have got
        new("level-10", "Level 10", "Reach level 10 with a wizard.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.HighestLevel >= 10,
            Difficulty: AchievementDifficulty.Standard),

        new("level-25", "Level 25", "Reach level 25 with a wizard.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.HighestLevel >= 25,
            Difficulty: AchievementDifficulty.Hard),

        new("level-50", "Level 50", "Reach level 50 with a wizard.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.HighestLevel >= 50,
            Difficulty: AchievementDifficulty.Legendary),

        new("second-wizard", "A Second Wizard", "Have two wizards on the same account.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.Wizards.Count >= 2,
            Difficulty: AchievementDifficulty.Standard),

        new("quest-complete", "Quest Complete", "Finish a quest in full.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.QuestsCompleted >= 1,
            Difficulty: AchievementDifficulty.Easy),

        new("twenty-five-quests", "Twenty-Five Quests", "Finish twenty-five quests.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.QuestsCompleted >= 25,
            Difficulty: AchievementDifficulty.Standard),

        new("gold-10000", "Ten Thousand Gold", "Hold ten thousand gold.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.HighestGold >= 10_000,
            Difficulty: AchievementDifficulty.Standard),

        new("gold-100000", "A Hundred Thousand Gold", "Hold a hundred thousand gold.",
            AchievementCategory.Progress, AchievementSource.PlayerDatabase, p => p.HighestGold >= 100_000,
            Difficulty: AchievementDifficulty.Hard),

        // ------------------------------------------------------------ what the spellbook holds
        // Counted across the account rather than per wizard, and counted once each: two wizards who both
        // know the same spell are one spell this account knows, so a second wizard cannot double a total
        // that is meant to be about a collection.
        new("first-spell", "A First Spell", "Learn a spell.",
            AchievementCategory.Spells, AchievementSource.PlayerDatabase, p => p.DistinctSpellsLearned >= 1,
            Difficulty: AchievementDifficulty.Easy),

        new("ten-spells", "Spell Collector", "Learn ten different spells.",
            AchievementCategory.Spells, AchievementSource.PlayerDatabase, p => p.DistinctSpellsLearned >= 10,
            Difficulty: AchievementDifficulty.Standard),

        new("twenty-five-spells", "Spell Scholar", "Learn twenty-five different spells.",
            AchievementCategory.Spells, AchievementSource.PlayerDatabase, p => p.DistinctSpellsLearned >= 25,
            Difficulty: AchievementDifficulty.Hard),

        new("fifty-spells", "Spell Master", "Learn fifty different spells.",
            AchievementCategory.Spells, AchievementSource.PlayerDatabase, p => p.DistinctSpellsLearned >= 50,
            Difficulty: AchievementDifficulty.Legendary),

        // ------------------------------------------------------------ how you played it
        new("by-the-book", "By the Book", "Play ten hours without ever using a command on the account.",
            AchievementCategory.Record, AchievementSource.GameSession,
            p => TimeSpan.FromSeconds(p.PlaytimeSeconds) >= Hour * 10 && p.CommandsUsed == 0,
            Difficulty: AchievementDifficulty.Hard),
    ];

    /// <summary>
    /// One ladder per school, a rung every five levels to the server's own cap.
    /// </summary>
    /// <remarks>
    /// Per school rather than one overall, because a level 20 Storm wizard is a different piece of work from
    /// any wizard at all being level 20 - and an account with one of each would otherwise take every
    /// school's rungs off a single character. What is counted is the best any wizard of that school has
    /// reached, which is the only thing the player database actually says.
    /// </remarks>
    private static MilestoneTrack LevelTrack(string school, int maxLevel)
        => new(
            Id: $"{school.ToLowerInvariant()}-level",
            Name: $"{school} Wizard",
            Category: AchievementCategory.Account,
            Source: AchievementSource.PlayerDatabase,
            Reached: profile => profile.HighestLevelIn(school),
            Rungs: Rungs(LevelMilestoneStep, maxLevel),
            Title: rung => $"Level {rung}",
            Describe: value => $"level {value}",
            Requirement: rung => $"Reach level {rung} with a {school} wizard.",
            StepText: $"every {LevelMilestoneStep} levels");

    /// <summary>Every tenth duel won, up to <see cref="BattleMilestoneCount"/> rungs of them.</summary>
    private static MilestoneTrack BattleTrack()
        => new(
            Id: "battles",
            Name: "Battles Won",
            Category: AchievementCategory.Battle,
            Source: AchievementSource.ServerLog,
            Reached: profile => profile.DuelsWon,
            Rungs: Rungs(BattleMilestoneStep, BattleMilestoneStep * BattleMilestoneCount),
            Title: rung => $"{rung:N0}",
            Describe: value => $"{value:N0} duels won",
            Requirement: rung => $"Win {rung:N0} duels.",
            StepText: $"every {BattleMilestoneStep} duels won",
            NeedsDebugLog: true);

    /// <summary>
    /// Every twenty-fifth creature beaten that is not a boss, to the size of the server's own list.
    /// </summary>
    /// <remarks>
    /// Counted as distinct creatures rather than as wins, and only for the ones the server's list calls
    /// ordinary creatures - beating one creature ten times is not ten creatures, and a creature the list
    /// does not have is counted as neither. It is the list that decides both, which is why the top of this
    /// ladder is its size.
    /// </remarks>
    private static MilestoneTrack CreatureTrack(int cap)
        => new(
            Id: "creatures",
            Name: "Creatures Beaten",
            Category: AchievementCategory.Battle,
            Source: AchievementSource.ServerLog,
            Reached: profile => profile.MobsDefeated,
            Rungs: Rungs(CreatureMilestoneStep, cap),
            Title: rung => $"{rung:N0}",
            Describe: value => $"{value:N0} creatures beaten",
            Requirement: rung => $"Beat {rung:N0} different creatures that are not bosses.",
            StepText: $"every {CreatureMilestoneStep} creatures beaten",
            NeedsDebugLog: true);

    /// <summary>The thresholds of a ladder: the step, twice the step, and so on up to the top.</summary>
    private static IReadOnlyList<int> Rungs(int step, int cap) {
        var rungs = new List<int>();

        for (var at = step; at <= cap; at += step) {
            rungs.Add(at);
        }

        return rungs;
    }

    /// <summary>
    /// The badges a ladder is made of: one per rung, each naming the ladder it belongs to.
    /// </summary>
    /// <remarks>
    /// The rungs are still badges - they are what is earned, stored, logged and scored - and the track is
    /// how the profile shows them: one card with a bar to the next rung, rather than forty near-identical
    /// tiles. The id is derived from the track and the rung, so it can be read back without the catalog and
    /// cannot collide with anything else.
    /// </remarks>
    private static IEnumerable<AchievementDefinition> RungsFor(MilestoneTrack track) {
        foreach (var rung in track.Rungs) {
            yield return new AchievementDefinition(
                $"{track.Id}-{rung}",
                $"{track.Name}, {track.Title(rung)}",
                track.Requirement(rung),
                track.Category,
                track.Source,
                profile => track.Reached(profile) >= rung,
                track.NeedsDebugLog,
                MilestoneDifficulty(rung, track.Top),
                IsMilestone: true,
                TrackId: track.Id);
        }
    }

    /// <summary>
    /// How hard a rung of a ladder is, from how far along it sits.
    /// </summary>
    /// <remarks>
    /// A share of the way through rather than a threshold in absolute numbers, so a server with a cap of 60
    /// gets the same shape of ladder as one with a cap of 200 - the first rungs easy, the last one the
    /// hardest - instead of every rung on a small server coming out legendary.
    /// </remarks>
    private static AchievementDifficulty MilestoneDifficulty(int at, int last) {
        var reached = last <= 0 ? 1.0 : (double) at / last;

        return reached switch {
            <= 0.25 => AchievementDifficulty.Easy,
            <= 0.50 => AchievementDifficulty.Standard,
            <= 0.75 => AchievementDifficulty.Hard,
            _ => AchievementDifficulty.Legendary,
        };
    }

    public static IEnumerable<AchievementDefinition> InCategory(AchievementCategory category)
        => All.Where(a => a.Category == category);

    /// <summary>The difficulty's name, in the words the score is explained with.</summary>
    public static string DifficultyName(AchievementDifficulty difficulty) => AchievementScores.Name(difficulty);

    public static AchievementDefinition? ById(string? id)
        => id is null ? null : All.FirstOrDefault(a => a.Id == id);

    /// <summary>Whether the profile has been seen entering one of the achievements' zones.</summary>
    public static bool HasVisited(LauncherProfile profile, string zone)
        => profile.ZonesVisited.Any(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an achievement's source is switched on, and what to say when it is not.
    /// </summary>
    /// <param name="debugLogging">Whether the server is logging at debug level right now.</param>
    /// <param name="databaseAnswers">Whether the player database answered on the last look.</param>
    public static string? UnavailableBecause(
        AchievementDefinition achievement, bool debugLogging, bool databaseAnswers)
        => achievement switch {
            { NeedsDebugLog: true } when !debugLogging =>
                "Imlight has to be logging at DEBUG level for this one - the server writes zone entries and "
                + "duel results there. Settings has the switch.",

            { Source: AchievementSource.PlayerDatabase } when !databaseAnswers =>
                "This is read from the player database, which only answers while your server is running.",

            _ => null,
        };
}
