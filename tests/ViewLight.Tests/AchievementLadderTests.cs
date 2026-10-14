using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The ladders the catalog adds past the basics: each school played up, and what the spellbook holds.
/// </summary>
/// <remarks>
/// Driven through <see cref="ProfileLedger"/> rather than against the catalog directly, because the ledger
/// is where the profile screen's badges come from. A rule that is right but never reached would still be a
/// badge nobody can earn, so the path the launcher actually uses is the one under test.
/// </remarks>
public class AchievementLadderTests {

    private static readonly DateTimeOffset At = new(2026, 10, 14, 20, 0, 0, 0, TimeSpan.FromHours(-4));

    private static PlayerWizardRecord Wizard(
        int level, string school = "", uint charId = 1, params uint[] spells)
        => new(42, charId, level, 0, [], school, spells);

    /// <summary>The database's answer for one account, with the wizard signed in so playtime can be read.</summary>
    private static PlayerProfileData Database(params PlayerWizardRecord[] wizards)
        => new(wizards, [new PlayerOnlineRecord(42, 1, "WizardZone_TheCommons", "Centaur")], [], null);

    private sealed class Bench : IDisposable {

        private readonly TempWorkspace _workspace = new();

        public Bench() {
            Store = new ProfileStore(_workspace.Path("profiles.json"));
            Ledger = new ProfileLedger(Store);
            Ledger.NoteAccounts([new PlayerAccount(42, "jordan")]);
        }

        public ProfileStore Store { get; }

        public ProfileLedger Ledger { get; }

        public LauncherProfile Profile => Store.GetOrCreate(42, "jordan");

        public void Apply(PlayerProfileData data) => Ledger.ApplyDatabase(data, At);

        public void Dispose() => _workspace.Dispose();
    }

    // ------------------------------------------------------------------ each school, played up

    [Fact]
    public void ASchoolsLadderIsEarnedByThatSchoolAndByNoOther() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(20, "Storm")));

        Assert.True(bench.Profile.Has("storm-level-5"));
        Assert.True(bench.Profile.Has("storm-level-20"));
        Assert.False(bench.Profile.Has("fire-level-5"));
        Assert.False(bench.Profile.Has("ice-level-20"));
    }

    [Fact]
    public void ARungIsEarnedByBeingReachedRatherThanPassed() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(15, "Myth")));

        Assert.True(bench.Profile.Has("myth-level-15"));
        Assert.False(bench.Profile.Has("myth-level-20"));
    }

    [Fact]
    public void ASchoolBadgeCannotBeTakenFromAnotherWizardsLevel() {
        // A level 20 Fire and a level 3 Storm. If the ladder read the account's best level rather than the
        // best of that school, one character would take every school's badges on its own.
        using var bench = new Bench();

        bench.Apply(Database(Wizard(20, "Fire", charId: 1), Wizard(3, "Storm", charId: 2)));

        Assert.True(bench.Profile.Has("fire-level-20"));
        Assert.False(bench.Profile.Has("storm-level-5"));
    }

    [Fact]
    public void AWizardWithNoSchoolStillEarnsTheLadderThatIsNotAboutASchool() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(50)));

        Assert.True(bench.Profile.Has("level-50"));
        Assert.DoesNotContain(
            bench.Profile.Achievements,
            a => a.AchievementId.Contains("-level-", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the spellbook

    [Fact]
    public void SpellsAreCountedOnceEachAcrossTheAccount() {
        // Six spells on one wizard and the same six plus four more on the other is ten spells this account
        // knows, not sixteen.
        using var bench = new Bench();

        uint[] shared = [1, 2, 3, 4, 5, 6];
        uint[] extra = [7, 8, 9, 10];

        bench.Apply(Database(
            Wizard(10, "Life", charId: 1, spells: shared),
            Wizard(10, "Life", charId: 2, spells: [.. shared, .. extra])));

        Assert.Equal(10, bench.Profile.DistinctSpellsLearned);
        Assert.True(bench.Profile.Has("ten-spells"));
        Assert.False(bench.Profile.Has("twenty-five-spells"));
    }

    [Fact]
    public void AWizardWhoHasLearnedNothingEarnsNoSpellBadge() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(10, "Death")));

        Assert.False(bench.Profile.Has("first-spell"));
    }

    [Fact]
    public void OneSpellIsEnoughForTheFirstSpellBadge() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(10, "Death", spells: [42])));

        Assert.True(bench.Profile.Has("first-spell"));
    }

    [Fact]
    public void ASpellLearnedIsNotLostByReadingTheWizardAgain() {
        using var bench = new Bench();

        bench.Apply(Database(Wizard(10, "Death", spells: [42])));
        bench.Apply(Database(Wizard(12, "Death", spells: [42])));

        Assert.Equal(1, bench.Profile.DistinctSpellsLearned);
    }
}
