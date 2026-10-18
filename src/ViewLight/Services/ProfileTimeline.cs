using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One thing the account has earned, as the profile's history shows it: what it was, when, what it was worth,
/// and the line that says what made it true.
/// </summary>
/// <remarks>
/// The history is not a second record of anything. Every entry is a badge the profile already carries - which
/// is why nothing here can disagree with the achievements page - and it is a value so a refresh can tell
/// whether the list on screen has moved. The two kinds are the two kinds the catalog has: a milestone is a
/// rung of one of the ladders, and everything else is a badge of its own.
/// </remarks>
/// <param name="Id">The achievement's id, which is what tells two entries apart.</param>
/// <param name="Name">What it is called on the achievements page.</param>
/// <param name="CategoryName">The group it belongs to, as the heading there reads it.</param>
/// <param name="IsMilestone">Whether it is a rung of a ladder rather than a badge of its own.</param>
/// <param name="EarnedAt">When it was given, which is what the history is ordered by.</param>
/// <param name="EarnedText">When it was given, as a line somebody reads rather than compares.</param>
/// <param name="Points">What it added to the profile's score, bonus included.</param>
/// <param name="Detail">The server's line about it when there was one, or what it takes.</param>
public sealed record ProfileTimelineEntry(
    string Id,
    string Name,
    string CategoryName,
    bool IsMilestone,
    DateTimeOffset EarnedAt,
    string EarnedText,
    int Points,
    string Detail) {

    /// <summary>Which of the two kinds this is, in the word the history labels it with.</summary>
    public string KindText => IsMilestone ? "Milestone" : "Achievement";

    /// <summary>What it was worth, as the row adds it up.</summary>
    public string PointsText => $"+{Points:N0} pts";
}

/// <summary>
/// The profile's history: everything earned, newest first, said in the same words as the achievements page.
/// </summary>
/// <remarks>
/// <para>
/// The idea is that the profile is a person rather than a form: the account is at the top, the wizards stand
/// on a shelf under it, and underneath them is what has happened lately. This file is that last part, and it
/// is deliberately built out of <see cref="ProfileBadge"/> rather than out of the profile's stored awards:
/// a badge already knows its name, its group, whether it is a rung of a ladder, what it is worth on this
/// account and what the server said about it, so there is one set of rules for the two screens.
/// </para>
/// <para>
/// Only what has been earned appears. A locked badge is not history - it is a thing still out there, and the
/// achievements page is where the whole ladder belongs. An award whose id the catalog no longer knows is
/// dropped rather than shown as a bare id: the launcher cannot say what it was, and a row that says nothing is
/// worse than one that is not there.
/// </para>
/// </remarks>
public static class ProfileTimeline {

    /// <summary>
    /// How many entries are shown before the list stops being a history and starts being the achievements page
    /// again.
    /// </summary>
    public const int Limit = 12;

    /// <summary>
    /// The earned badges as history, newest first.
    /// </summary>
    /// <param name="badges">Every badge judged for the profile, earned and locked alike.</param>
    /// <param name="now">The moment the words "Today" and "Yesterday" are measured from.</param>
    /// <param name="limit">How many entries to keep.</param>
    public static IReadOnlyList<ProfileTimelineEntry> For(
        IEnumerable<ProfileBadge> badges, DateTimeOffset now, int limit = Limit) {

        return [.. badges
            .Where(badge => badge.EarnedAt is not null)
            .OrderByDescending(badge => badge.EarnedAt)
            .ThenBy(badge => badge.Name, StringComparer.CurrentCulture)
            .Take(Math.Max(0, limit))
            .Select(badge => new ProfileTimelineEntry(
                badge.Id,
                badge.Name,
                badge.CategoryName,
                badge.Achievement.IsMilestone,
                badge.EarnedAt!.Value,
                DescribeEarned(badge.EarnedAt.Value, now),
                badge.Points,
                badge.Detail))];
    }

    /// <summary>
    /// Might the list have changed since it was worked out, which is when it is worth working out again.
    /// </summary>
    /// <remarks>
    /// A history is a list that mostly does not move: the database is read every ten seconds and almost every
    /// look learns nothing. The one thing that does move on its own is the wording of a date - "Today 20:15"
    /// becomes "Yesterday 20:15" at midnight - so the rows are compared rather than the awards.
    /// </remarks>
    public static bool HasMoved(
        IReadOnlyList<ProfileTimelineEntry> shown, IReadOnlyList<ProfileTimelineEntry> wanted)
        => !shown.SequenceEqual(wanted);

    /// <summary>
    /// What the history says above its rows: how much there is, and where the rest of it lives.
    /// </summary>
    /// <param name="total">How many things the account has earned in all.</param>
    /// <param name="shown">How many of them are in the list.</param>
    public static string Caption(int total, int shown) {
        if (total == 0) {
            return "Nothing earned yet. Achievements and milestones are written down here the moment the "
                   + "server sees them earned, so this is the first place to look after a session.";
        }

        if (total <= shown) {
            return total == 1
                ? "One thing earned so far, newest first."
                : $"{total} earned so far, newest first.";
        }

        return $"The newest {shown} of {total} earned. The rest are under Achievements.";
    }

    /// <summary>
    /// When something was earned, said the way somebody reads a history: today and yesterday by name, and
    /// anything older by its date.
    /// </summary>
    /// <remarks>
    /// Local time throughout, because these are moments somebody lived through rather than timestamps to be
    /// compared. Both sides are converted the same way, so the answer holds in whatever zone the machine is
    /// set to rather than only where the launcher was written.
    /// </remarks>
    public static string DescribeEarned(DateTimeOffset earnedAt, DateTimeOffset now) {
        var earned = earnedAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = earned.Date;

        if (day == today) {
            return $"Today {earned:HH:mm}";
        }

        if (day == today.AddDays(-1)) {
            return $"Yesterday {earned:HH:mm}";
        }

        return day.Year == today.Year
            ? earned.ToString("d MMM HH:mm")
            : earned.ToString("d MMM yyyy");
    }
}
