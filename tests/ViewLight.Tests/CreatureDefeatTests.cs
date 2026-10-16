using System.Globalization;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// What a duel says about the creatures that were in it, and what the launcher does with it.
/// </summary>
/// <remarks>
/// <para>
/// Two ways of knowing, and the lines below are both of them, written in the shape Imlight's own source
/// writes them - the samples are out of a real duel on a real server. The line that names a creature
/// outright (<c>| Creature 35166 Wraith-WCBoss-L05 joined</c>) does not exist yet, and the launcher's half
/// of it is here so that it works the day the server writes it. What the server <i>does</i> write is which
/// slots a creature planned a turn on (<c>| Slot 1 | Smart: true | Aggressive: true | Selfish: false</c> -
/// only a creature writes one) and what a spell reached, and those two together with a won duel are what a
/// counted creature is made of.
/// </para>
/// <para>
/// A count is all the second way can leave behind: a duel says which slots held creatures and never which
/// creature was on one - the participant line carries a debug tag like <c>140DD</c>, not a template id - so
/// creatures beaten are counted rather than listed. See <see cref="ProfileLedger"/>.
/// </para>
/// </remarks>
public class CreatureDefeatTests {

    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, 0, TimeSpan.FromHours(-4));

    private const string DuelId = "4854107232535182118";

    /// <summary>The server's own creature list, as the launcher reads it off disk - a name each.</summary>
    private static readonly CreatureManifestEntry Boss = new(35166u, "Wraith-WCBoss-L05");
    private static readonly CreatureManifestEntry Mob = new(11111u, "Firecat");
    private static readonly CreatureManifestEntry AnotherMob = new(22222u, "Nightside");

    private static string Stamp(DateTimeOffset at)
        => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string CreatureJoined(uint templateId, string name, int slot = 4, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Slot {slot} | Creature {templateId} {name} joined";

    private static string DuelEnded(bool playersWon, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Duel ended. {(playersWon ? "Players" : "Creatures")} win.";

    /// <summary>
    /// A duel beginning, under the id the server gives it.
    /// </summary>
    /// <remarks>
    /// That id is the sigil's, so the same one comes round again for the next fight there - which is why
    /// this line is read rather than ignored. See <see cref="DuelStarted"/>.
    /// </remarks>
    private static string DuelCreated(string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Created. Grace period over in 3.75";

    /// <summary>A creature planning a turn, which is the server saying the slot holds one.</summary>
    private static string CreatureTurn(int slot, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Slot {slot} | Smart: true | Aggressive: true | Selfish: false";

    /// <summary>A participant taking a slot, under the server's own debug name for it.</summary>
    private static string Participant(string name, int slot, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Slot {slot} | Participant {name} joined";

    /// <summary>A spell reaching the slots it names.</summary>
    private static string SpellHit(uint spell, int caster, int[] targets, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Slot {caster} | Spell {spell} hits targets "
           + $"[{string.Join(", ", targets)}]";

    // ------------------------------------------------------------------ reading the line

    [Fact]
    public void ReadsTheCreatureADuelIsAgainst() {
        var fact = Assert.IsType<DuelCreature>(Assert.Single(
            ServerLogFacts.Parse(CreatureJoined(35166, "Wraith-WCBoss-L05"))));

        Assert.Equal(DuelId, fact.DuelId);
        Assert.Equal(4, fact.Slot);
        Assert.Equal(35166u, fact.TemplateId);
        Assert.Equal("Wraith-WCBoss-L05", fact.Name);
        Assert.Equal(At, fact.At);
    }

    [Fact]
    public void TheNameIsOptionalBecauseTheIdIsWhatMatters() {
        var fact = Assert.IsType<DuelCreature>(Assert.Single(
            ServerLogFacts.Parse($"{Stamp(At)} [DBG] Duel {DuelId} | Slot 4 | Creature 35166 joined")));

        Assert.Equal(35166u, fact.TemplateId);
        Assert.Equal(string.Empty, fact.Name);
    }

    [Fact]
    public void ACreatureWithNoTemplateIdSaysNothing() {
        // The whole reason that line is needed is the ID. A line naming a creature without one cannot be
        // tied to anything, so it is not a fact - and guessing which creature it was is the mistake the ID
        // exists to prevent.
        Assert.Empty(ServerLogFacts.Parse($"{Stamp(At)} [DBG] Duel {DuelId} | Slot 4 | Creature Wraith joined"));
    }

    [Fact]
    public void ReadsADuelBeginning() {
        var fact = Assert.IsType<DuelStarted>(Assert.Single(ServerLogFacts.Parse(DuelCreated())));

        Assert.Equal(DuelId, fact.DuelId);
        Assert.Equal(At, fact.At);
    }

    [Fact]
    public void ReadsACreaturePlanningATurnAsTheSlotItWasOn() {
        var fact = Assert.IsType<DuelCreatureTurn>(Assert.Single(ServerLogFacts.Parse(CreatureTurn(1))));

        Assert.Equal(DuelId, fact.DuelId);
        Assert.Equal(1, fact.Slot);
        Assert.Equal(At, fact.At);

        // A line that names no slot as a number cannot be matched to one, so it says nothing.
        Assert.Empty(ServerLogFacts.Parse(
            $"{Stamp(At)} [DBG] Duel {DuelId} | Slot one | Smart: true | Aggressive: true | Selfish: false"));
    }

    [Fact]
    public void TheParticipantsLineTheServerWritesTodayNamesNoCreature() {
        // "Participant 140DD joined" is what is actually in the log - this one is copied out of one. The tag
        // is the participant object's own debug name, not a template id and not a name a creature list could
        // carry, so nothing is counted from this line: it is the creature's turn that says a slot holds a
        // creature. Note that it is still read as a participant rather than as a creature.
        var fact = Assert.IsType<DuelParticipant>(Assert.Single(ServerLogFacts.Parse(
            Participant("140DD", slot: 1))));

        Assert.Equal(DuelId, fact.DuelId);
        Assert.Equal(1, fact.Slot);
        Assert.Equal("140DD", fact.Name);

        // A participant whose name is not there at all says nothing about who joined.
        Assert.Empty(ServerLogFacts.Parse(
            $"{Stamp(At)} [DBG] Duel {DuelId} | Slot 1 | Participant  joined"));
    }

    [Fact]
    public void ReadsWhatASpellHit() {
        var fact = Assert.IsType<DuelSpellHit>(Assert.Single(ServerLogFacts.Parse(
            SpellHit(1234567u, 0, [1, 2, 4]) + " (Crits!)")));

        Assert.Equal(DuelId, fact.DuelId);
        Assert.Equal(0, fact.CasterSlot);
        Assert.Equal(1234567u, fact.SpellTemplateId);
        Assert.Equal([1, 2, 4], fact.Slots);
    }

    [Fact]
    public void ASpellThatHitNothingReadableIsNotAHit() {
        // No targets is a hit on nobody, a spell that is not a spell id cannot be tied to a cast, and a
        // target that is not a number cannot be matched to a slot - so none of them is a fact.
        Assert.Empty(ServerLogFacts.Parse(SpellHit(1234567u, 0, [])));
        Assert.Empty(ServerLogFacts.Parse(
            $"{Stamp(At)} [DBG] Duel {DuelId} | Slot 0 | Spell a-spell hits targets [1]"));
        Assert.Empty(ServerLogFacts.Parse(
            $"{Stamp(At)} [DBG] Duel {DuelId} | Slot 0 | Spell 1234567 hits targets [1, two]"));
    }

    // ------------------------------------------------------------------ what it credits

    private sealed class Bench : IDisposable {

        private readonly TempWorkspace _workspace = new();
        private readonly string _path;
        private readonly ProfileLedger _ledger;

        public Bench() {
            _path = _workspace.Path("profiles.json");
            Store = new ProfileStore(_path);
            _ledger = new ProfileLedger(Store);
            _ledger.NoteAccounts([new PlayerAccount(42, "jordan")]);
        }

        public ProfileStore Store { get; }

        public ProfileLedger Ledger => _ledger;

        public LauncherProfile Profile => Store.GetOrCreate(42, "jordan");

        /// <summary>
        /// One account signed in, as the database would report it.
        /// </summary>
        /// <remarks>
        /// A duel is credited only when exactly one profile was signed in, and who is signed in is the
        /// database's answer - not the log's - because the database names the account. So being "signed in"
        /// in these tests means a database read, exactly as it does for the launcher.
        /// </remarks>
        public void SignIn() => _ledger.ApplyDatabase(
            new PlayerProfileData([], [new PlayerOnlineRecord(42, 1, "WizardZone_TheCommons", "Centaur")], [], null),
            At);

        public void Apply(params string[] lines) => _ledger.ApplyLogLines(lines);

        /// <summary>The same store re-read from disk, as a later launcher run would see it.</summary>
        public LauncherProfile AfterRestart() => new ProfileStore(_path).GetOrCreate(42, "jordan");

        public void Dispose() => _workspace.Dispose();
    }

    // ------------------------------------------------------------------ the named creatures

    [Fact]
    public void ACreatureInADuelAPlayerWonIsCountedAsBeaten() {
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            CreatureJoined(35166, "Wraith-WCBoss-L05"),
            CreatureJoined(35099, "Banshee-WCBoss-L04", slot: 3),
            DuelEnded(playersWon: true));

        Assert.Equal([35099u, 35166u], bench.Profile.CreaturesDefeated.Order());
        Assert.True(bench.Profile.HasBeaten(35166));
    }

    [Fact]
    public void ALostDuelCountsNothing() {
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: false));

        Assert.Empty(bench.Profile.CreaturesDefeated);
        Assert.Equal(0, bench.Profile.CreaturesKilled);
        Assert.Equal(1, bench.Profile.DuelsLost);
    }

    [Fact]
    public void ABeatenCreatureIsCountedOnceHoweverManyTimesItIsBeaten() {
        using var bench = new Bench();
        bench.SignIn();

        for (var i = 0; i < 3; i++) {
            bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));
        }

        Assert.Single(bench.Profile.CreaturesDefeated);
        Assert.Equal(3, bench.Profile.DuelsWon);
    }

    [Fact]
    public void AnUnfinishedDuelsCreaturesAreNotCreditedToALaterDuel() {
        // The creature line and the ending are separate lines, so a duel that never ended must not have its
        // creatures handed to the next duel that does.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            CreatureJoined(11111, "Abandoned-Mob", slot: 2, duelId: "111"),
            DuelEnded(playersWon: true, duelId: "222"));

        Assert.Empty(bench.Profile.CreaturesDefeated);
    }

    [Fact]
    public void WithNobodySignedInADuelIsCreditedToNobody() {
        using var bench = new Bench();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [1]),
            DuelEnded(playersWon: true));

        Assert.Empty(bench.Profile.CreaturesDefeated);
        Assert.Equal(0, bench.Profile.CreaturesKilled);
    }

    [Fact]
    public void ACreaturesDefeatSurvivesARestart() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.True(bench.AfterRestart().HasBeaten(35166));
    }

    // ------------------------------------------------------------------ the lines the server writes today

    [Fact]
    public void TheTwoCreaturesOfAWonDuelAreBothCounted() {
        // The duel this rule was built from, line for line: the server's own two creatures on the creatures'
        // circles, spells from the player's circle reaching one and then the other, and the players winning.
        // Neither creature is named - the participant line carries a tag, not a template - so the duel
        // leaves a count of two behind rather than two creatures the launcher could name.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            Participant("140DD", slot: 1),
            CreatureTurn(slot: 0),
            CreatureTurn(slot: 1),
            SpellHit(661258515u, caster: 4, [0]),
            SpellHit(1427280917u, caster: 4, [1]),
            DuelEnded(playersWon: true));

        Assert.Equal(2, bench.Profile.CreaturesKilled);
        Assert.Empty(bench.Profile.CreaturesDefeated);
        Assert.Equal(1, bench.Profile.DuelsWon);
        Assert.True(bench.Profile.Has("first-victory"));
    }

    [Fact]
    public void ACreaturesOwnSpellIsNotThePlayerBeatingAnything() {
        // Creatures cast too, and a creature hitting the player - or another creature - is not the player
        // defeating it. Only a cast from a player's circle counts.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 0),
            CreatureTurn(slot: 1),
            SpellHit(1523944378u, caster: 0, [4]),
            SpellHit(1496157882u, caster: 1, [0]),
            DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.CreaturesKilled);
    }

    [Fact]
    public void ASlotNoCreatureEverPlannedOnIsNotACreature() {
        // The circle layout says circles 0-3 are the creatures', but that is the server's arrangement rather
        // than something the launcher can see - so a duel in which no creature ever planned a turn proves
        // nothing, and nothing is credited for it rather than a slot being taken for a creature.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            SpellHit(1234567u, caster: 4, [0, 1]),
            DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.CreaturesKilled);
        Assert.Equal(1, bench.Profile.DuelsWon);
    }

    [Fact]
    public void TheSameCreatureHitTwiceInOneDuelIsCountedOnce() {
        // What a duel leaves behind is a count of the creatures in it, not of the spells that landed: one
        // creature hit by two spells is one creature beaten.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [1]),
            SpellHit(7654321u, caster: 5, [1]),
            DuelEnded(playersWon: true));

        Assert.Equal(1, bench.Profile.CreaturesKilled);
    }

    [Fact]
    public void APlayerHittingNobodyButTheirOwnSideIsNotACreature() {
        // A spell that landed on the players' own circles reached no creature: the creature circles are the
        // ones below the players', and only those count.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 0),
            SpellHit(1234567u, caster: 4, [4, 5]),
            DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.CreaturesKilled);
    }

    [Fact]
    public void ALostDuelCreditsNoCreatureEvenWhenSpellsHit() {
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [1]),
            DuelEnded(playersWon: false));

        Assert.Equal(0, bench.Profile.CreaturesKilled);
        Assert.Equal(1, bench.Profile.DuelsLost);
    }

    [Fact]
    public void AnUnfinishedDuelsSlotsAreNotCreditedToALaterDuel() {
        // The parts arrive as separate lines and the ending is what concludes them, so a duel that never
        // ended must not have its slots handed to the next duel that does.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            CreatureTurn(slot: 1, duelId: "111"),
            SpellHit(1234567u, caster: 4, [1], duelId: "222"),
            DuelEnded(playersWon: true, duelId: "222"));

        Assert.Equal(0, bench.Profile.CreaturesKilled);
    }

    [Fact]
    public void ASecondFightAtTheSameSigilStartsWithNoneOfTheFirstsSlots() {
        // A duel's id is the sigil's, so fighting again at the same sigil writes the same id - and every line
        // about the new fight is then indistinguishable from one about the old. What the earlier fight said
        // is thrown away when the next one starts, which is the whole reason the Created line is read.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [1]),
            DuelEnded(playersWon: true));

        Assert.Equal(1, bench.Profile.CreaturesKilled);

        bench.Apply(
            DuelCreated(),
            SpellHit(7654321u, caster: 4, [1]),
            DuelEnded(playersWon: true));

        Assert.Equal(1, bench.Profile.CreaturesKilled);
        Assert.Equal(2, bench.Profile.DuelsWon);
    }

    [Fact]
    public void TheCreatureLadderMovesWithTheKills() {
        // The whole point of counting them: twenty-five creatures beaten is the first rung of the profile's
        // own creature ladder, and nothing else in the catalog moves with it.
        using var bench = new Bench();
        bench.SignIn();

        for (var i = 0; i < 25; i++) {
            bench.Apply(
                DuelCreated(),
                CreatureTurn(slot: 1),
                SpellHit(1234567u, caster: 4, [1]),
                DuelEnded(playersWon: true));
        }

        Assert.Equal(25, bench.Profile.CreaturesKilled);
        Assert.True(bench.Profile.Has("creatures-25"));
        Assert.False(bench.Profile.Has("creatures-50"));
    }

    [Fact]
    public void KillsSurviveARestart() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 0),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [0, 1]),
            DuelEnded(playersWon: true));

        Assert.Equal(2, bench.AfterRestart().CreaturesKilled);
    }

    // ------------------------------------------------------------------ boss, or just a creature

    /// <summary>The server's creature list, as the launcher reads it off disk.</summary>
    private static IReadOnlyList<CreatureManifestEntry> Roster(params CreatureManifestEntry[] creatures) => creatures;

    [Fact]
    public void ACreatureTheRosterCallsABossCountsAsABoss() {
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            CreatureJoined(35166, "Wraith-WCBoss-L05"),
            CreatureJoined(11111, "Firecat", slot: 3),
            DuelEnded(playersWon: true));

        bench.Ledger.UseCreatureRoster(Roster(Boss, Mob), At);

        Assert.Equal(1, bench.Profile.BossesDefeated);
        Assert.True(bench.Profile.Has("first-boss"));
    }

    [Fact]
    public void ABossBeatenBeforeTheRosterWasReadStillCounts() {
        // The roster is a file, so it can turn up after the duel. Nothing has said what the creature was
        // until it does, and a badge that turned on which of the two was read first would be no badge.
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.BossesDefeated);

        bench.Ledger.UseCreatureRoster(Roster(Boss, Mob), At);

        Assert.Equal(1, bench.Profile.BossesDefeated);
        Assert.True(bench.Profile.Has("first-boss"));
    }

    [Fact]
    public void ACreatureTheRosterDoesNotHaveIsNotCountedAsABoss() {
        // Not among the bosses is not the same as being an ordinary creature: the list has to say which it
        // is, and about one it does not have there is nothing to say.
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(99999, "Something-Else"), DuelEnded(playersWon: true));

        bench.Ledger.UseCreatureRoster(Roster(Boss, Mob), At);

        Assert.Equal(0, bench.Profile.BossesDefeated);
        Assert.True(bench.Profile.HasBeaten(99999));
        Assert.Equal(1, bench.Profile.DuelsWon);
    }

    [Fact]
    public void WithNoRosterReadYetABossCountsAsNeither() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.BossesDefeated);
        Assert.False(bench.Profile.Has("first-boss"));
        Assert.True(bench.Profile.HasBeaten(35166));
    }

    [Fact]
    public void WhatTheRosterSaysSurvivesARestart() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));
        bench.Ledger.UseCreatureRoster(Roster(Boss, Mob), At);

        Assert.Equal(1, bench.AfterRestart().BossesDefeated);
    }

    [Fact]
    public void CountingKillsDoesNotNeedARosterAtAll() {
        // The point of the whole change: a creature beaten is counted from the duel's own lines, so a
        // launcher with no creature list on disk still knows what was beaten - only which of them were
        // bosses is left for the list.
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            DuelCreated(),
            CreatureTurn(slot: 0),
            CreatureTurn(slot: 1),
            SpellHit(1234567u, caster: 4, [0, 1]),
            DuelEnded(playersWon: true));

        Assert.Equal(2, bench.Profile.CreaturesKilled);
        Assert.Equal(0, bench.Profile.BossesDefeated);
    }
}
