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

    // ------------------------------------------------------------------ time played

    [Fact]
    public void TheTimePlayedLadderIsTheShapeItWasAskedFor() {
        // Half an hour, an hour, and then five hours at a time to a thousand - with the twenty hours the
        // game itself uses as a boundary and the hundred a wizard is supposed to have played both landing on
        // rungs of it rather than on badges of their own.
        var playtime = AchievementCatalog.Tracks.Single(t => t.Id == "playtime");

        Assert.Equal(AchievementCategory.Playtime, playtime.Category);
        Assert.Equal([30, 60, 300, 600, 900, 1200, 1500], playtime.Rungs.Take(7));
        Assert.Equal(AchievementCatalog.PlaytimeCap, playtime.Top);
        Assert.Equal(1000 * 60, AchievementCatalog.PlaytimeCap);

        // A rung a minute apart would be a badge per look at the database, so the ladder is the thresholds
        // themselves and nothing else: the rung above the cap is not there, and neither is one between two.
        Assert.NotNull(AchievementCatalog.ById($"playtime-{AchievementCatalog.PlaytimeCap}"));
        Assert.Null(AchievementCatalog.ById($"playtime-{AchievementCatalog.PlaytimeCap + AchievementCatalog.PlaytimeMilestoneStep}"));
        Assert.Null(AchievementCatalog.ById("playtime-6030"));
    }

    [Fact]
    public void PlaytimeIsCountedFromTheHourTheAccountHasActuallyPlayed() {
        // Minutes, and the same minutes the launcher adds up: half an hour of play is the first rung and no
        // more, and a second short of an hour is not the hour.
        var half = new LauncherProfile { AccountId = 1, PlaytimeSeconds = 30 * 60 };
        var hour = new LauncherProfile { AccountId = 1, PlaytimeSeconds = 60 * 60 };

        Assert.True(AchievementCatalog.ById("playtime-30")!.Earned(half));
        Assert.False(AchievementCatalog.ById("playtime-60")!.Earned(half));
        Assert.True(AchievementCatalog.ById("playtime-60")!.Earned(hour));
        Assert.False(AchievementCatalog.ById("playtime-300")!.Earned(hour));

        // And nothing is earned by a profile that has never played.
        var untouched = new LauncherProfile { AccountId = 1 };

        Assert.All(
            AchievementCatalog.All.Where(a => a.TrackId == "playtime"),
            a => Assert.False(a.Earned(untouched)));
    }

    [Fact]
    public void TheThousandthHourIsABadgeOfItsOwnWorthAHeap() {
        // The point of the ladder's top rung: rather than one more playtime rung it is a badge with a name,
        // and it is what the ladder says it is worth rather than what its difficulty says - a heap, as asked
        // for, on the one badge in the catalog where the coarse scale does not apply.
        var thousand = AchievementCatalog.ById($"playtime-{AchievementCatalog.PlaytimeCap}")!;

        Assert.Equal(AchievementCatalog.ThousandHoursBadge, thousand.Name);
        Assert.Equal(AchievementCatalog.ThousandHoursPoints, thousand.Score);
        Assert.True(thousand.IsMilestone);
        Assert.Equal("playtime", thousand.TrackId);
        Assert.Equal("Spend 1,000 hours signed in to your own server.", thousand.Requirement);

        // It is granted when the rung is done, and not a minute before.
        Assert.True(thousand.Earned(new LauncherProfile { AccountId = 1, PlaytimeSeconds = 1000 * 3600 }));
        Assert.False(thousand.Earned(new LauncherProfile { AccountId = 1, PlaytimeSeconds = 1000 * 3600 - 60 }));
    }

    [Fact]
    public void EveryOtherRungOfTheLadderIsPricedAndNamedByItsLadder() {
        // The finale is the exception, and it has to stay the only one: everything else a ladder generates is
        // named for the ladder and worth what its difficulty is worth, which is what keeps a score explainable
        // from the badges on the profile.
        var rungs = AchievementCatalog.All.Where(a => a.TrackId == "playtime").ToList();
        var finale = $"playtime-{AchievementCatalog.PlaytimeCap}";

        Assert.All(rungs.Where(r => r.Id != finale), rung => {
            Assert.Equal(AchievementScores.For(rung.Difficulty), rung.Score);
            Assert.StartsWith("Playtime, ", rung.Name, StringComparison.Ordinal);
        });

        Assert.All(
            AchievementCatalog.All.Where(a => a.FixedScore is not null),
            a => Assert.Equal(finale, a.Id));
    }

    [Fact]
    public void TheBadgesTimePlayedUsedToBeAreGoneRatherThanKeptBesideTheLadder() {
        // Five fixed thresholds cannot sit beside a ladder of the same five thresholds without the profile
        // scoring the same hour twice, and the catalog is the whole list - so the ladder replaces them. The
        // hours themselves are not lost: they are the playtime the ladder is measured in, so the rungs up to
        // what an account has played are earned on its next read.
        foreach (var id in new[] { "settling-in", "five-hours", "ten-hours", "twenty-hours", "hundred-hours" }) {
            Assert.Null(AchievementCatalog.ById(id));
        }
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
