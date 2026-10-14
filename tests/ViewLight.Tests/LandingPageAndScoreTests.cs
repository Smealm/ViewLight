using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

public class LandingPageTests {

    [Fact]
    public void HomeIsWhatAStoredValueThatMeansNothingFallsBackTo() {
        Assert.Equal(LauncherSection.Home, MainWindowViewModel.ParseSection(null));
        Assert.Equal(LauncherSection.Home, MainWindowViewModel.ParseSection(string.Empty));
        Assert.Equal(LauncherSection.Home, MainWindowViewModel.ParseSection("SettingsPage"));
        Assert.Equal(LauncherSection.Home, MainWindowViewModel.ParseSection("Home"));
    }

    [Theory]
    [InlineData("Profile")]
    [InlineData("Settings")]
    [InlineData("Logs")]
    [InlineData("About")]
    public void AnExplicitDefaultPageIsHonoured(string page) {
        Assert.Equal(page, MainWindowViewModel.NormaliseLandingPage(page));

        var expected = Enum.Parse<LauncherSection>(page);
        Assert.Equal(expected, MainWindowViewModel.ParseSection(page));
    }

    [Fact]
    public void TheStoredNamesStayInStepWithTheSectionsTheLauncherHas() {
        // LauncherSections is spelled out in the model so the settings file does not depend on the view
        // model's enum. That only works while the two lists say the same thing.
        var fromEnum = Enum.GetNames<LauncherSection>();

        Assert.Equal(fromEnum, LauncherSections.All);
        Assert.Equal(LauncherSections.Home, new LauncherSettings().DefaultSection);
    }

    [Fact]
    public void ALandingPageIsNormalisedWhateverTheCaseItWasWrittenIn() {
        Assert.Equal(LauncherSections.Settings, MainWindowViewModel.NormaliseLandingPage(" settings "));
        Assert.Equal(LauncherSections.Home, MainWindowViewModel.NormaliseLandingPage("nope"));
    }
}

public class AchievementScoreTests {

    [Fact]
    public void EveryBadgeIsWorthSomething() {
        Assert.NotEmpty(AchievementCatalog.All);

        foreach (var achievement in AchievementCatalog.All) {
            Assert.True(achievement.Score > 0, $"{achievement.Id} has no score");
        }
    }

    [Fact]
    public void HarderBadgesAreWorthMore() {
        var easy = AchievementScores.For(AchievementDifficulty.Easy);
        var standard = AchievementScores.For(AchievementDifficulty.Standard);
        var hard = AchievementScores.For(AchievementDifficulty.Hard);
        var legendary = AchievementScores.For(AchievementDifficulty.Legendary);

        Assert.True(easy < standard && standard < hard && hard < legendary);
    }

    [Fact]
    public void AProfilesScoreIsTheSumOfWhatItHasEarned() {
        var profile = new LauncherProfile { AccountId = 1 };
        profile.Achievements.Add(new EarnedAchievement("first-steps", DateTimeOffset.UnixEpoch, "a line"));
        profile.Achievements.Add(new EarnedAchievement("first-victory", DateTimeOffset.UnixEpoch, "a line"));

        // A profile with nothing used on it scores double, which is what the clean bonus is.
        var baseScore = AchievementCatalog.ById("first-steps")!.Score
                        + AchievementCatalog.ById("first-victory")!.Score;

        Assert.Equal(baseScore * AchievementScores.CleanMultiplier, AchievementScores.Of(profile));

        // Nothing earned is nothing scored, and a badge the catalog no longer knows cannot add points to a
        // total no screen could explain.
        Assert.Equal(0, AchievementScores.Of(new LauncherProfile { AccountId = 2 }));
        profile.Achievements.Add(new EarnedAchievement("gone-from-the-catalog", DateTimeOffset.UnixEpoch, "x"));
        Assert.Equal(baseScore * AchievementScores.CleanMultiplier, AchievementScores.Of(profile));
    }

    [Fact]
    public void AnAccountCommandsHaveBeenUsedOnGetsNoBonusRatherThanAPenalty() {
        // Everything about the two profiles is the same except the command, so the whole difference is the
        // bonus - a tampered-with account keeps every point it earned, it just does not double them.
        var clean = new LauncherProfile { AccountId = 1 };
        var seen = new LauncherProfile { AccountId = 2, CommandsSeenHere = 1 };

        var earned = new EarnedAchievement("first-steps", DateTimeOffset.UnixEpoch, "a line");
        clean.Achievements.Add(earned);
        seen.Achievements.Add(earned);

        var score = AchievementCatalog.ById("first-steps")!.Score;

        Assert.Equal(score * AchievementScores.CleanMultiplier, AchievementScores.Of(clean));
        Assert.Equal(score, AchievementScores.Of(seen));
        Assert.Equal(1, AchievementScores.MultiplierFor(seen));
    }

    [Fact]
    public void EveryCategoryInUseHasItsOwnHeading() {
        // A category that falls through to the same heading as another reads as having been mis-grouped,
        // which is exactly what adding a category to the enum and forgetting the caption would do.
        var badges = ProfileBadge.For(new LauncherProfile { AccountId = 1 }, debugLogging: true, databaseAnswers: true);
        var used = AchievementCatalog.All.Select(a => a.Category).Distinct().ToList();

        foreach (var category in used) {
            var names = badges.Where(b => b.Achievement.Category == category)
                .Select(b => b.CategoryName).Distinct().ToList();

            Assert.Single(names);
            Assert.False(string.IsNullOrWhiteSpace(names[0]));
        }

        // No two categories may share one: two sets of badges under the same heading read as one muddled
        // group, and the group is now the unit the screen is read in.
        var shared = used
            .GroupBy(c => badges.First(b => b.Achievement.Category == c).CategoryName)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" + ", g.Select(c => c.ToString())))
            .ToList();

        Assert.Empty(shared);
    }

    [Fact]
    public void TheLaddersStepUp() {
        // Playtime: each step earns the ones below it and no more than one rung above.
        var atSixHours = new LauncherProfile { AccountId = 1, PlaytimeSeconds = 6 * 3600 };

        Assert.True(AchievementCatalog.ById("settling-in")!.Earned(atSixHours));
        Assert.True(AchievementCatalog.ById("five-hours")!.Earned(atSixHours));
        Assert.False(AchievementCatalog.ById("ten-hours")!.Earned(atSixHours));

        // Battles won: the same ladder shape, counted from the server's log rather than from playtime, and
        // running in tens all the way up.
        var atFifteenWins = new LauncherProfile { AccountId = 1, DuelsWon = 15 };

        Assert.True(AchievementCatalog.ById("first-victory")!.Earned(atFifteenWins));
        Assert.True(AchievementCatalog.ById("battles-10")!.Earned(atFifteenWins));
        Assert.False(AchievementCatalog.ById("battles-20")!.Earned(atFifteenWins));
        Assert.False(AchievementCatalog.ById("battles-500")!.Earned(atFifteenWins));
    }

    [Fact]
    public void TheLevelLaddersRunToWhateverTheServerAllows() {
        // Generated to the cap rather than listed, so the top rung is the cap's own rung and the rungs in
        // between step by five.
        var top = $"storm-level-{AchievementCatalog.MaxLevel}";

        Assert.NotNull(AchievementCatalog.ById(top));
        Assert.NotNull(AchievementCatalog.ById("storm-level-5"));
        Assert.Null(AchievementCatalog.ById("storm-level-3"));
        Assert.Null(AchievementCatalog.ById($"storm-level-{AchievementCatalog.MaxLevel + 5}"));

        // Every school gets one, and each school's ladder is its own list of ids.
        foreach (var school in WizardSchool.Names) {
            Assert.NotNull(AchievementCatalog.ById($"{school.ToLowerInvariant()}-level-5"));
        }
    }

    [Fact]
    public void TheLevelLadderFollowsAServerWithASmallerCap() {
        // The cap is the server's to decide, so it is worth seeing the ladder rebuilt around one - a server
        // with a cap of sixty has twelve rungs per school, not forty.
        var wasLevel = AchievementCatalog.MaxLevel;
        var wasCreatures = AchievementCatalog.CreatureCap;

        try {
            AchievementCatalog.UseMaxLevel(60);

            Assert.Equal(60, AchievementCatalog.MaxLevel);
            Assert.NotNull(AchievementCatalog.ById("storm-level-60"));
            Assert.Null(AchievementCatalog.ById("storm-level-65"));

            var storm = AchievementCatalog.Tracks.Single(t => t.Id == "storm-level");

            Assert.Equal(12, storm.Rungs.Count);
            Assert.Equal(60, storm.Top);
            Assert.Equal(12, storm.Passed(60));
            Assert.Null(storm.Next(60));
        }
        finally {
            AchievementCatalog.UseMaxLevel(wasLevel);
            AchievementCatalog.UseCreatureCount(wasCreatures);
        }
    }

    [Fact]
    public void TheCreatureLadderRunsToTheServersOwnList() {
        var wasLevel = AchievementCatalog.MaxLevel;
        var wasCreatures = AchievementCatalog.CreatureCap;

        try {
            // A list of 317 ordinary creatures has thirteen rungs of twenty-five, the last of them at 325:
            // a top rung of 317 is not a milestone anybody would recognise.
            AchievementCatalog.UseCreatureCount(317);

            Assert.Equal(325, AchievementCatalog.CreatureCap);
            Assert.NotNull(AchievementCatalog.ById("creatures-325"));
            Assert.Null(AchievementCatalog.ById("creatures-350"));

            var creatures = AchievementCatalog.Tracks.Single(t => t.Id == "creatures");

            Assert.Equal(13, creatures.Rungs.Count);
            Assert.Equal(325, creatures.Top);

            // A list that did not come back sensible leaves the stock ladder alone rather than shrinking it
            // to nothing.
            AchievementCatalog.UseCreatureCount(0);
            Assert.Equal(AchievementCatalog.DefaultCreatureCap, AchievementCatalog.CreatureCap);
        }
        finally {
            AchievementCatalog.UseMaxLevel(wasLevel);
            AchievementCatalog.UseCreatureCount(wasCreatures);
        }
    }

    [Fact]
    public void EveryRungBelongsToTheLadderItSaysItDoes() {
        // The profile draws a ladder from its rungs' track ids, so a rung whose id names a ladder that does
        // not exist - or that belongs to two - would be a badge no card could ever show.
        var rungs = AchievementCatalog.All.Where(a => a.TrackId is not null).ToList();

        Assert.NotEmpty(rungs);

        foreach (var rung in rungs) {
            var track = Assert.Single(AchievementCatalog.Tracks, t => t.Id == rung.TrackId);

            Assert.True(rung.IsMilestone, $"{rung.Id} is a rung but is not a milestone");
            Assert.StartsWith($"{track.Id}-", rung.Id, StringComparison.Ordinal);
            Assert.Equal(track.Category, rung.Category);
            Assert.Contains(rung.Id, track.Rungs.Select(r => $"{track.Id}-{r}"));
        }

        // And the other way round: every rung a ladder claims is a badge the catalog actually has.
        foreach (var track in AchievementCatalog.Tracks) {
            Assert.NotEmpty(track.Rungs);

            foreach (var rung in track.Rungs) {
                var badge = AchievementCatalog.ById($"{track.Id}-{rung}");

                Assert.NotNull(badge);
                Assert.Equal(track.Requirement(rung), badge!.Requirement);
                Assert.StartsWith($"{track.Name}, ", badge.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ARungIsWorthMoreTheFurtherUpTheLadderItSits() {
        foreach (var track in AchievementCatalog.Tracks) {
            var rungs = track.Rungs.Select(r => AchievementCatalog.ById($"{track.Id}-{r}")!).ToList();

            Assert.True(
                rungs[^1].Score >= rungs[0].Score,
                $"{track.Id}'s last rung is worth less than its first");

            // What the ladder is worth in total, so a ladder that quietly lost its rungs is noticed here.
            Assert.True(rungs.Sum(r => r.Score) > 0, $"{track.Id} is worth nothing");
        }
    }

    [Fact]
    public void TheLaddersAreMilestonesAndNothingElseIs() {
        // Which badges are allowed to be announced (and silenced) as milestones.
        Assert.True(AchievementCatalog.ById("storm-level-5")!.IsMilestone);
        Assert.True(AchievementCatalog.ById("battles-10")!.IsMilestone);

        Assert.False(AchievementCatalog.ById("first-steps")!.IsMilestone);
        Assert.False(AchievementCatalog.ById("first-victory")!.IsMilestone);
        Assert.False(AchievementCatalog.ById("quest-complete")!.IsMilestone);
    }

    [Fact]
    public void ABadgeSaysWhatItIsWorthAndHowHardItWas() {
        var profile = new LauncherProfile { AccountId = 1 };
        var badge = ProfileBadge.For(profile, debugLogging: true, databaseAnswers: true)
            .First(b => b.Id == "hundred-hours");

        Assert.Equal(AchievementDifficulty.Legendary, badge.Achievement.Difficulty);

        // This profile is clean, so the badge shows what it is actually worth to it - and says what the
        // doubling was, rather than leaving a total the badge count cannot account for.
        var worth = badge.Achievement.Score * AchievementScores.CleanMultiplier;

        Assert.Contains($"{worth} pts", badge.ScoreText, StringComparison.Ordinal);
        Assert.Contains($"{badge.Achievement.Score} × {AchievementScores.CleanMultiplier}", badge.ScoreText,
            StringComparison.Ordinal);
        Assert.Contains("Legendary", badge.ScoreText, StringComparison.Ordinal);
    }
}
