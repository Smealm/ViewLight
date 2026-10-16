using System.Collections.ObjectModel;
using System.Globalization;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// How an earned badge reads, and how the badges are gathered on the profile.
/// </summary>
/// <remarks>
/// These are the two things a badge has to get right on screen: saying something about itself rather than
/// borrowing a line from whatever earned its neighbours, and being findable among the dozens of others.
/// </remarks>
public class ProfileBadgePresentationTests {

    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, 0, TimeSpan.FromHours(-4));

    private static PlayerProfileData Database(params PlayerWizardRecord[] wizards)
        => new(wizards, [new PlayerOnlineRecord(42, 1, "WizardZone_TheCommons", "Centaur")], [], null);

    private static string Stamp(DateTimeOffset at)
        => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

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

        public IReadOnlyList<ProfileBadge> Badges
            => ProfileBadge.For(Profile, debugLogging: true, databaseAnswers: true);

        public ProfileBadge Badge(string id) => Badges.Single(b => b.Id == id);

        /// <summary>The badges as the screen shows them: the ladders as one card each, the rest as tiles.</summary>
        public IReadOnlyList<ProfileBadgeGroup> Groups => ProfileBadgeGroup.For(Profile, Badges);

        public void Dispose() => _workspace.Dispose();
    }

    // ------------------------------------------------------------ what an earned badge says

    [Fact]
    public void ABadgeEarnedFromTheDatabaseDoesNotQuoteTheDatabaseSummary() {
        // A pass over the database earns several badges at once, so there is no one line about any of them.
        // The sentence used to be a summary of the whole account, which is what made every earned badge say
        // the same thing; now the badge says what it takes instead.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);

        var badge = bench.Badge("level-10");

        Assert.True(badge.IsEarned);
        Assert.Equal("Reach level 10 with a wizard.", badge.Detail);
        Assert.DoesNotContain("player database", badge.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BadgesEarnedTogetherDoNotAllSayTheSameThing() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);

        Assert.NotEqual(bench.Badge("level-10").Detail, bench.Badge("quest-complete").Detail);
    }

    [Fact]
    public void ABadgeEarnedByAServerLineQuotesThatLine() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(), At);
        bench.Ledger.ApplyLogLines([
            $"{Stamp(At)} [DBG] Duel 4972210732661112830 | Duel ended. Players win.",
        ]);

        var badge = bench.Badge("first-victory");

        Assert.True(badge.IsEarned);
        Assert.Equal("Duel 4972210732661112830 | Duel ended. Players win.", badge.Detail);
    }

    [Fact]
    public void ASummaryLeftOnABadgeByAnOlderLauncherIsNotShown() {
        // Profiles written before the fix hold that one sentence on every badge a pass swept. It was never
        // evidence about the badge it sits on, so it is not shown as if it were.
        using var bench = new Bench();

        bench.Profile.Achievements.Add(new EarnedAchievement(
            "quest-complete", At,
            "The player database: 2 wizard(s), best level 1,2 quest(s) finished, most gold 2."));

        var badge = bench.Badge("quest-complete");

        Assert.True(badge.IsEarned);
        Assert.Equal("Finish a quest in full.", badge.Detail);
    }

    [Fact]
    public void AnEarnedBadgeSaysWhenItWasEarnedOnALineOfItsOwn() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(42, 1, 40, 0, [])), At);

        var badge = bench.Badge("level-25");

        Assert.True(badge.IsEarned);
        Assert.StartsWith("Unlocked ", badge.UnlockedText, StringComparison.Ordinal);
        Assert.Contains("2026", badge.UnlockedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedBadgeSaysWhatItTakesAndHasNoDate() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(), At);

        var badge = bench.Badge("playtime-6000");

        Assert.True(badge.IsLocked);
        Assert.Equal(string.Empty, badge.UnlockedText);
        Assert.Equal("Spend 100 hours signed in to your own server.", badge.Detail);
    }

    // ------------------------------------------------------------ how they are gathered

    [Fact]
    public void EveryBadgeIsInExactlyOneGroupAndTheLaddersCountTheirOwnRungs() {
        using var bench = new Bench();

        var badges = bench.Badges;
        var groups = bench.Groups;

        // Every badge is accounted for exactly once: as a tile of its own, or as a rung of a ladder's card.
        Assert.Equal(badges.Count, groups.Sum(g => g.Total));

        Assert.Equal(
            [.. badges.Where(b => b.Achievement.TrackId is null).Select(b => b.Id)],
            [.. groups.SelectMany(g => g.Badges).Select(b => b.Id)]);

        Assert.Equal(
            badges.Count(b => b.Achievement.TrackId is not null),
            groups.SelectMany(g => g.Milestones).Sum(m => m.Total));
    }

    [Fact]
    public void TheLaddersComeOutAsOneCardEach() {
        using var bench = new Bench();

        var wizards = bench.Groups.Single(g => g.Name == "Your wizards");
        var battles = bench.Groups.Single(g => g.Name == "Battles");

        // One per school, and the two battle ladders: duels won, and the ordinary creatures the server's own
        // list holds.
        Assert.Equal(
            [.. WizardSchool.Names.Select(school => $"{school.ToLowerInvariant()}-level")],
            [.. wizards.Milestones.Select(m => m.Id)]);
        Assert.Equal(["battles", "creatures"], battles.Milestones.Select(m => m.Id));

        // And not one rung of them is drawn as a tile of its own, which is the wall of badges the cards
        // replace.
        Assert.DoesNotContain(wizards.Badges, b => b.Achievement.TrackId is not null);
        Assert.DoesNotContain(battles.Badges, b => b.Achievement.TrackId is not null);
    }

    [Fact]
    public void AMilestoneSaysWhereTheAccountIsAndWhatTheNextOneIs() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 12, 0, [], School: "Storm")), At);

        var storm = bench.Groups.Single(g => g.Name == "Your wizards")
            .Milestones.Single(m => m.Id == "storm-level");

        Assert.Equal(12, storm.Reached);
        Assert.Equal(2, storm.Passed);
        Assert.Equal(15, storm.Next);
        Assert.Equal("Now level 12", storm.CurrentText);
        Assert.Equal("Next milestone: level 15", storm.NextText);
        Assert.Equal($"2 of {AchievementCatalog.MaxLevel / 5} milestones", storm.RungsText);
        Assert.Equal("A milestone every 5 levels", storm.StepText);

        // The bar runs from the last rung passed to the next one, so it answers how close the next milestone
        // is: level 12 on a five-level ladder is two fifths of the way from the rung at ten to the one at
        // fifteen, not the eight percent of the whole ladder.
        Assert.Equal(0.4, storm.Fraction, 3);

        // Both rungs taken are worth the same and this profile is clean, so they are doubled; the next rung
        // is worth what it would add.
        Assert.Equal(40, storm.Points);
        Assert.Equal(20, storm.NextPoints);
        Assert.Equal("40 points earned here", storm.PointsText);
        Assert.Equal("+20 at the next", storm.NextPointsText);
        Assert.False(storm.IsComplete);
    }

    [Fact]
    public void TheFirstMilestoneIsMeasuredFromTheBottomOfTheLadder() {
        // A wizard at level 3 on a ladder that steps by five: nothing taken yet, and the bar is the whole way
        // from the start to the first rung, which is three fifths of it.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 3, 0, [], School: "Fire")), At);

        var fire = bench.Groups.Single(g => g.Name == "Your wizards")
            .Milestones.Single(m => m.Id == "fire-level");

        Assert.Equal(3, fire.Reached);
        Assert.Equal(0, fire.Passed);
        Assert.Equal(5, fire.Next);
        Assert.Equal("Now level 3", fire.CurrentText);
        Assert.Equal("Next milestone: level 5", fire.NextText);
        Assert.Equal("0 points earned here", fire.PointsText);
        Assert.Equal(0.6, fire.Fraction, 3);
    }

    [Fact]
    public void TheTimePlayedCardIsNamedForTheBadgeAtItsTopOnceItIsDone() {
        // A ladder whose last rung is a badge in its own right says so on its card, because that badge is the
        // one thing about the ladder nobody can see: the rungs are not drawn as tiles.
        using var bench = new Bench();

        bench.Profile.PlaytimeSeconds = 1000 * 3600;

        var playtime = bench.Groups.Single(g => g.Name == "Time on the server")
            .Milestones.Single(m => m.Id == "playtime");

        Assert.True(playtime.IsComplete);
        Assert.Equal(AchievementCatalog.ThousandHoursBadge, playtime.Name);
        Assert.Equal("Every milestone reached", playtime.NextText);
        Assert.Equal("A milestone every 5 hours after the first one, up to 1,000 hours", playtime.StepText);

        // What the card says the ladder has scored is the rungs it holds, the heap at the top included.
        Assert.Equal(
            bench.Badges.Where(b => b.Achievement.TrackId == "playtime" && b.IsEarned).Sum(b => b.Points),
            playtime.Points);
    }

    [Fact]
    public void ALadderClimbedToTheTopSaysSo() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, AchievementCatalog.MaxLevel, 0, [], School: "Storm")), At);

        var storm = bench.Groups.Single(g => g.Name == "Your wizards")
            .Milestones.Single(m => m.Id == "storm-level");

        Assert.True(storm.IsComplete);
        Assert.Null(storm.Next);
        Assert.False(storm.HasNext);
        Assert.Equal("Every milestone reached", storm.NextText);
        Assert.Equal(string.Empty, storm.NextPointsText);
        Assert.Equal(1, storm.Fraction);
    }

    [Fact]
    public void TheGroupsAddUpToTheProfilesScore() {
        // The number on the card above the groups is made of the groups - and the rungs of the ladders are
        // most of it, so a group that left them out would be a heading nobody could reconcile.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"], School: "Fire")), At);

        Assert.Equal(AchievementScores.Of(bench.Profile), bench.Groups.Sum(g => g.Points));
    }

    [Fact]
    public void AGroupCountsWhatIsUnlockedInItAndWhatThatScored() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(
            Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);

        var progress = bench.Groups.Single(g => g.Name == "How far you have got");

        // Level 10 and the finished quest, worth what each is worth on this profile - which includes the
        // clean bonus, since that is what the score above the groups is made of too.
        Assert.Equal(2, progress.Unlocked);
        Assert.Equal(
            progress.Badges.Where(b => b.IsEarned).Sum(b => b.Points),
            progress.Points);
        Assert.Equal(
            progress.Badges.Where(b => b.IsEarned).Sum(b => b.Achievement.Score) * AchievementScores.CleanMultiplier,
            progress.Points);
        Assert.Contains($"{progress.Unlocked} of {progress.Total}", progress.Summary, StringComparison.Ordinal);
        Assert.Contains($"{progress.Points} points", progress.Summary, StringComparison.Ordinal);
        Assert.True(progress.HasUnlocked);
    }

    [Fact]
    public void AnUntouchedGroupSaysSoWithoutBeingEmpty() {
        using var bench = new Bench();

        var battles = bench.Groups.Single(g => g.Name == "Battles");

        Assert.False(battles.HasUnlocked);
        Assert.Equal(0, battles.Points);
        Assert.Equal(0, battles.Fraction);
        Assert.Equal("0 points", battles.PointsText);
        Assert.True(battles.Total > 0);
    }

    [Fact]
    public void AGroupStartsShutAndCanBeOpened() {
        // Shut, because the catalog runs to forty rungs per school: opening on every badge of every category
        // is the wall the headings exist to break up.
        var group = new ProfileBadgeGroup(AchievementCategory.Battle, []);

        Assert.False(group.IsExpanded);
        Assert.Equal("▸", group.Chevron);

        group.ToggleCommand.Execute(null);

        Assert.True(group.IsExpanded);
        Assert.Equal("▾", group.Chevron);

        group.ToggleCommand.Execute(null);

        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void LookingAgainKeepsTheGroupThatWasOpenedOpen() {
        // The screen is scrolled through while the launcher looks at the player database every few seconds,
        // and almost every look learns nothing. The group the reader opened has to still be the same group,
        // still open, with the page where they left it - which is what refilling in place buys.
        using var bench = new Bench();

        var groups = new ObservableCollection<ProfileBadgeGroup>(bench.Groups);
        var battles = groups.Single(g => g.Name == "Battles");
        battles.ToggleCommand.Execute(null);

        Assert.True(battles.IsExpanded);

        ProfileBadgeGroup.Sync(groups, bench.Groups);

        Assert.Same(battles, groups.Single(g => g.Name == "Battles"));
        Assert.True(battles.IsExpanded);
        Assert.Equal(bench.Badges.Count, groups.Sum(g => g.Total));
    }

    [Fact]
    public void ABadgeEarnedWhileReadingIsAddedWithoutClosingTheGroup() {
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(), At);

        var groups = new ObservableCollection<ProfileBadgeGroup>(bench.Groups);
        var progress = groups.Single(g => g.Name == "How far you have got");
        progress.ToggleCommand.Execute(null);

        var before = progress.Unlocked;

        // A level reached while the reader has the group open.
        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);
        ProfileBadgeGroup.Sync(groups, bench.Groups);

        Assert.Same(progress, groups.Single(g => g.Name == "How far you have got"));
        Assert.True(progress.IsExpanded);
        Assert.True(progress.Unlocked > before);
    }

    [Fact]
    public void ABadgeEarnedWhileReadingSaysSoOnTheGroupHeadingAsWell() {
        // The tiles redraw themselves - they are in an observable collection - and the heading is computed
        // from that collection, so without a change raised for it the heading kept the numbers it had when
        // the screen was opened. Reported as the progress bar and the "unlocked N of M" line never moving.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(), At);

        var group = bench.Groups.Single(g => g.Name == "How far you have got");
        var summary = group.Summary;

        var said = new List<string>();
        group.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? string.Empty);

        // A level reached while the screen is open.
        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);
        ProfileBadgeGroup.Sync([group], bench.Groups);

        Assert.NotEqual(summary, group.Summary);
        Assert.Contains(nameof(ProfileBadgeGroup.Summary), said);
        Assert.Contains(nameof(ProfileBadgeGroup.Unlocked), said);
        Assert.Contains(nameof(ProfileBadgeGroup.Points), said);
        Assert.Contains(nameof(ProfileBadgeGroup.ProgressText), said);
        Assert.Contains(nameof(ProfileBadgeGroup.Fraction), said);
        Assert.Contains(nameof(ProfileBadgeGroup.HasUnlocked), said);
    }

    [Fact]
    public void AGroupThatHasNotChangedSaysNothingAtAll() {
        // Most looks at the player database learn nothing, and a heading that announced itself every ten
        // seconds would be a screen redrawing under whoever is reading it.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(42, 1, 12, 0, ["WC-TUT-C05-001"])), At);

        var group = bench.Groups.Single(g => g.Name == "How far you have got");

        var said = new List<string>();
        group.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? string.Empty);

        ProfileBadgeGroup.Sync([group], bench.Groups);

        Assert.Empty(said);
    }

    [Fact]
    public void AGroupsContentsAreLeftAloneWhenTheyAreAlreadyWhatTheyShouldBe() {
        using var bench = new Bench();

        var group = bench.Groups[0];
        var badges = group.Badges.ToList();
        var milestones = group.Milestones.ToList();

        group.Replace(badges, milestones, group.Worlds.ToList());

        Assert.Same(badges[0], group.Badges[0]);
        Assert.Equal(badges.Count, group.Badges.Count);
        Assert.Equal(milestones.Count, group.Milestones.Count);
    }

    [Fact]
    public void TheGroupsFollowTheCatalogAndANewOneArrivesShut() {
        var into = new ObservableCollection<ProfileBadgeGroup> { new(AchievementCategory.Battle, []) };

        ProfileBadgeGroup.Sync(into, [
            new(AchievementCategory.Spells, []),
            new(AchievementCategory.Battle, []),
            new(AchievementCategory.Exploration, []),
        ]);

        Assert.Equal(["Spells learned", "Battles", "Discovery"], into.Select(g => g.Name));
        Assert.All(into, g => Assert.False(g.IsExpanded));

        // A category the catalog no longer defines goes rather than lingering with stale badges in it.
        ProfileBadgeGroup.Sync(into, [new(AchievementCategory.Battle, [])]);

        Assert.Equal(["Battles"], into.Select(g => g.Name));
    }

    [Fact]
    public void OneCategoryIsOneGroup() {
        using var bench = new Bench();

        var groups = bench.Groups;

        Assert.Equal(groups.Count, groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).Count());

        // Nothing is an empty heading: every group has something to show, whether that is badges or the
        // ladders that are counted rather than drawn.
        Assert.All(groups, g => Assert.True(g.Badges.Count > 0 || g.Milestones.Count > 0));
    }

    [Fact]
    public void AMilestoneLandsInTheSameGroupAsTheBadgesBesideIt() {
        // The card and the rungs it stands for are one category's business: two ways of naming a heading is
        // how a ladder ends up with a group to itself.
        using var bench = new Bench();

        foreach (var group in bench.Groups) {
            Assert.All(group.Milestones, m => Assert.Equal(group.Name, m.CategoryName));
        }
    }
}
