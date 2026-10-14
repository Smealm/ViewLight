using System.Globalization;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The contract for naming a creature in a duel, and what the launcher does with it.
/// </summary>
/// <remarks>
/// The line this reads does not exist in Imlight yet - see <see cref="DuelCreature"/> for why boss badges
/// need it. This is the launcher's half of it, so the day the server writes the line the badges work with
/// no further change here. The lines below are written to the documented shape rather than copied out of a
/// log, because there is no log to copy from; this is the one place in the suite where that is true.
/// </remarks>
public class CreatureDefeatTests {

    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, 0, TimeSpan.FromHours(-4));

    private const string DuelId = "5348490766442470254";

    private static string Stamp(DateTimeOffset at)
        => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string CreatureJoined(uint templateId, string name, int slot = 4, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Slot {slot} | Creature {templateId} {name} joined";

    private static string DuelEnded(bool playersWon, string duelId = DuelId)
        => $"{Stamp(At)} [DBG] Duel {duelId} | Duel ended. {(playersWon ? "Players" : "Creatures")} win.";

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
        // The whole reason this line is needed is the ID. A line naming a creature without one cannot be
        // tied to anything, so it is not a fact - and guessing which creature it was is the mistake the ID
        // exists to prevent.
        Assert.Empty(ServerLogFacts.Parse($"{Stamp(At)} [DBG] Duel {DuelId} | Slot 4 | Creature Wraith joined"));
    }

    [Fact]
    public void TheParticipantsLineTheServerWritesTodayIsNotMistakenForACreature() {
        // "Participant 141BD joined" is what is actually in the log, and the hex id must not be read as a
        // template.
        Assert.Empty(ServerLogFacts.Parse(
            $"{Stamp(At)} [DBG] Duel {DuelId} | Slot 1 | Participant 141BD joined"));
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

        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.Empty(bench.Profile.CreaturesDefeated);
    }

    [Fact]
    public void ACreaturesDefeatSurvivesARestart() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.True(bench.AfterRestart().HasBeaten(35166));
    }

    // ------------------------------------------------------------------ boss, or just a creature

    /// <summary>The server's creature list, as the launcher reads it off disk.</summary>
    private static IReadOnlySet<uint> Roster(params uint[] ids) => new HashSet<uint>(ids);

    [Fact]
    public void ACreatureTheRosterCallsABossCountsAsOneAndNotAsAMob() {
        using var bench = new Bench();
        bench.SignIn();

        bench.Apply(
            CreatureJoined(35166, "Wraith-WCBoss-L05"),
            CreatureJoined(11111, "Firecat", slot: 3),
            DuelEnded(playersWon: true));

        bench.Ledger.UseCreatureRoster(Roster(35166), Roster(35166, 11111), At);

        Assert.Equal(1, bench.Profile.BossesDefeated);
        Assert.Equal(1, bench.Profile.MobsDefeated);
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

        bench.Ledger.UseCreatureRoster(Roster(35166), Roster(35166, 11111), At);

        Assert.Equal(1, bench.Profile.BossesDefeated);
        Assert.True(bench.Profile.Has("first-boss"));
    }

    [Fact]
    public void ACreatureTheRosterDoesNotHaveIsCountedAsNeither() {
        // Not among the bosses is not the same as being an ordinary creature: the list has to say which
        // it is, and about one it does not have there is nothing to say.
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(99999, "Something-Else"), DuelEnded(playersWon: true));

        bench.Ledger.UseCreatureRoster(Roster(35166), Roster(35166, 11111), At);

        Assert.Equal(0, bench.Profile.BossesDefeated);
        Assert.Equal(0, bench.Profile.MobsDefeated);
        Assert.True(bench.Profile.HasBeaten(99999));
        Assert.Equal(1, bench.Profile.DuelsWon);
    }

    [Fact]
    public void WithNoRosterReadYetABossCountsAsNeither() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));

        Assert.Equal(0, bench.Profile.BossesDefeated);
        Assert.Equal(0, bench.Profile.MobsDefeated);
        Assert.False(bench.Profile.Has("first-boss"));
        Assert.True(bench.Profile.HasBeaten(35166));
    }

    [Fact]
    public void WhatTheRosterSaysSurvivesARestart() {
        using var bench = new Bench();
        bench.SignIn();
        bench.Apply(CreatureJoined(35166, "Wraith-WCBoss-L05"), DuelEnded(playersWon: true));
        bench.Ledger.UseCreatureRoster(Roster(35166), Roster(35166, 11111), At);

        Assert.Equal(1, bench.AfterRestart().BossesDefeated);
    }
}
