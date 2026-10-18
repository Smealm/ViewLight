using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The profile's history: what it lists, in what order, and how it says when something happened.
/// </summary>
/// <remarks>
/// The history is read from the same badges the achievements page shows, so almost everything that can go
/// wrong here is a question about selection and order rather than about wording. The one thing that moves on
/// its own is a date - "Today" becomes "Yesterday" whether or not the server ran - which is why the words are
/// pinned down with a fixed moment rather than with the clock the test happens to run on.
/// </remarks>
public class ProfileHistoryTests {

    private static readonly DateTimeOffset Now =
        new(2026, 10, 16, 21, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void OnlyWhatWasEarnedAppearsNewestFirst() {
        var profile = Profile(
            Award("first-steps", Now.AddDays(-4)),
            Award("regular", Now.AddDays(-9)),
            Award("playtime-3600", Now.AddDays(-1)));

        var history = ProfileTimeline.For(Badges(profile), Now);

        // The order is the thing that makes a history a history: not the catalog's order, and not the order
        // the awards happen to be stored in.
        Assert.Equal(["playtime-3600", "first-steps", "regular"], history.Select(entry => entry.Id));

        // A badge still out there is not history. Only three of these were ever given.
        Assert.DoesNotContain(history, entry => entry.Id == "level-50");
        Assert.All(history, entry => Assert.True(entry.Points > 0));
    }

    [Fact]
    public void AMilestoneIsCalledAMilestoneAndABadgeIsNot() {
        var profile = Profile(
            Award("playtime-3600", Now.AddDays(-1)),
            Award("first-steps", Now.AddDays(-2)));

        var history = ProfileTimeline.For(Badges(profile), Now);

        var milestone = Assert.Single(history, entry => entry.Id == "playtime-3600");
        Assert.True(milestone.IsMilestone);
        Assert.Equal("Milestone", milestone.KindText);

        var badge = Assert.Single(history, entry => entry.Id == "first-steps");
        Assert.False(badge.IsMilestone);
        Assert.Equal("Achievement", badge.KindText);

        // And the group is the same heading the achievements page files it under, so the two cannot drift.
        Assert.Equal(AchievementHeadings.Of(AchievementCategory.Playtime), milestone.CategoryName);
    }

    [Fact]
    public void TheListStopsAtTheLimitAndSaysWhereTheRestIs() {
        var profile = Profile(
            Award("first-steps", Now.AddDays(-1)),
            Award("regular", Now.AddDays(-2)),
            Award("level-10", Now.AddDays(-3)));

        var history = ProfileTimeline.For(Badges(profile), Now, limit: 2);

        Assert.Equal(["first-steps", "regular"], history.Select(entry => entry.Id));

        Assert.StartsWith("The newest 2 of 3", ProfileTimeline.Caption(total: 3, shown: 2));
        Assert.StartsWith("3 earned", ProfileTimeline.Caption(total: 3, shown: 3));
        Assert.StartsWith("One thing earned", ProfileTimeline.Caption(total: 1, shown: 1));
        Assert.Contains("Nothing earned yet", ProfileTimeline.Caption(total: 0, shown: 0));
    }

    [Fact]
    public void AnAwardTheCatalogNoLongerKnowsIsLeftOutRatherThanShownAsAnId() {
        var profile = Profile(
            Award("first-steps", Now.AddDays(-1)),
            Award("an-achievement-this-version-never-had", Now.AddDays(-2)));

        var history = ProfileTimeline.For(Badges(profile), Now);

        // The launcher cannot say what it was, and a row with nothing to say is worse than no row.
        Assert.Equal("first-steps", Assert.Single(history).Id);
    }

    [Fact]
    public void WhenSomethingHappenedIsSaidInDaysWhenItWasTodayOrYesterday() {
        // Built from the machine's own clock rather than from a fixed offset, because the words are about
        // local days: the same instant is today on one screen and tomorrow on another, and the rule is what
        // is being pinned down, not the zone this test happens to run in.
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 20, 15, 0, now.Offset);

        Assert.Equal("Today 20:15", ProfileTimeline.DescribeEarned(today, now));
        Assert.Equal("Yesterday 20:15", ProfileTimeline.DescribeEarned(today.AddDays(-1), now));

        // Anything older is its date, because "nine days ago" is arithmetic the reader should not have to do.
        var nineDays = today.AddDays(-9);
        var older = ProfileTimeline.DescribeEarned(nineDays, now);

        Assert.DoesNotContain("Today", older);
        Assert.DoesNotContain("Yesterday", older);
        Assert.Contains(nineDays.ToString("MMM"), older);

        // And a year ago says its year, because a date without one would read as this year's.
        var lastYear = today.AddDays(-400);
        Assert.Contains(lastYear.Year.ToString(), ProfileTimeline.DescribeEarned(lastYear, now));
    }

    [Fact]
    public void TwoThingsEarnedInTheSameSecondDoNotSwapPlacesBetweenLooks() {
        var at = Now.AddHours(-3);
        var profile = Profile(Award("regular", at), Award("first-steps", at));

        var first = ProfileTimeline.For(Badges(profile), Now);
        var second = ProfileTimeline.For(Badges(profile), Now);

        // The tie is broken by name, so the list does not depend on the order the awards were stored in -
        // which is what would make two rows trade places every ten seconds.
        Assert.Equal(first.Select(entry => entry.Id), second.Select(entry => entry.Id));
        Assert.Equal(["A Regular", "First Steps"], first.Select(entry => entry.Name));
    }

    [Fact]
    public void ALauncherWithNoProfileHasNoHistory() {
        Assert.Empty(ProfileTimeline.For([], Now));
        Assert.False(ProfileTimeline.HasMoved([], []));

        var one = ProfileTimeline.For(Badges(Profile(Award("first-steps", Now))), Now);
        Assert.True(ProfileTimeline.HasMoved([], one));
        Assert.False(ProfileTimeline.HasMoved(one, one));
    }

    private static LauncherProfile Profile(params EarnedAchievement[] awards) => new() {
        AccountId = 7,
        Username = "Jordan",
        Achievements = [.. awards],
    };

    private static EarnedAchievement Award(string id, DateTimeOffset at) => new(id, at, string.Empty);

    private static IReadOnlyList<ProfileBadge> Badges(LauncherProfile profile)
        => ProfileBadge.For(profile, debugLogging: true, databaseAnswers: true);
}
