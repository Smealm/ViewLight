using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One achievement as the profile shows it: earned with the date and the line it came from, or still
/// locked with what would earn it.
/// </summary>
/// <param name="Achievement">The catalog entry this badge is.</param>
/// <param name="EarnedAt">When it was given, or null while it is still out there.</param>
/// <param name="Evidence">What the server said that made it true, empty while it is unearned.</param>
/// <param name="Unavailable">
/// Why this badge cannot be earned at all as things stand - the source it needs is switched off - or null
/// when nothing is in the way. A badge that is simply hard is not unavailable.
/// </param>
/// <param name="Multiplier">
/// What this badge counts for on the profile it belongs to: one, or more for an account that has never had
/// a command used on it.
/// </param>
public sealed record ProfileBadge(
    AchievementDefinition Achievement,
    DateTimeOffset? EarnedAt,
    string Evidence,
    string? Unavailable,
    int Multiplier = 1) {

    public string Id => Achievement.Id;

    public string Name => Achievement.Name;

    public bool IsEarned => EarnedAt is not null;

    public bool IsLocked => !IsEarned;

    public bool IsUnavailable => !IsEarned && Unavailable is not null;

    /// <summary>The category, as the group heading the badge sits under.</summary>
    public string CategoryName => AchievementHeadings.Of(Achievement.Category);

    /// <summary>
    /// What the badge is worth on this profile, and how hard it is, so the score is never a mystery.
    /// </summary>
    /// <remarks>
    /// The multiplied figure, because that is what the profile's total is made of - a badge worth fifty
    /// that counts double is a hundred points towards the score above it, and showing fifty there would
    /// leave the card unable to account for itself.
    /// </remarks>
    public string ScoreText
        => $"{Points} pts"
           + (Multiplier > 1 ? $" ({Achievement.Score} × {Multiplier})" : string.Empty)
           + $" · {AchievementCatalog.DifficultyName(Achievement.Difficulty)}";

    /// <summary>What this badge adds to the profile's score.</summary>
    public int Points => Achievement.Score * Multiplier;

    public string SourceName => Achievement.Source switch {
        AchievementSource.GameSession => "Counted while ViewLight runs your server",
        AchievementSource.ServerLog => "Read from your server's own log",
        _ => "Read from your player database",
    };

    /// <summary>
    /// When it was earned, as a line of its own.
    /// </summary>
    /// <remarks>
    /// The date and the evidence are two different answers - when, and why - and running them together
    /// with a separator is what made an earned badge read as one long unbroken string. Shown in local
    /// time, because it is a date somebody lived through rather than a timestamp to be compared.
    /// </remarks>
    public string UnlockedText => EarnedAt is { } earned ? $"Unlocked {earned.ToLocalTime():d MMMM yyyy}" : string.Empty;

    /// <summary>
    /// What the badge says under its name: what it takes while it is still out there, or what the server
    /// said at the moment it was earned.
    /// </summary>
    /// <remarks>
    /// The earned case quotes the server with the log preamble taken off - see <see cref="AchievementEvidence"/>.
    /// When there is nothing to quote it says what the badge takes, which is at least true of it: a badge
    /// earned by what the account holds rather than by a line the server wrote has no line of its own, and
    /// borrowing one from another badge is what made earned badges read as nonsense.
    /// </remarks>
    public string Detail => EarnedAt is null
        ? Unavailable ?? Achievement.Requirement
        : ServerLineAboutThisBadge ?? Achievement.Requirement;

    /// <summary>
    /// The line the server wrote about this badge, or null when there is none that is about it.
    /// </summary>
    private string? ServerLineAboutThisBadge => AchievementEvidence.IsSweepSummary(Evidence)
        ? null
        : AchievementEvidence.ForDisplay(Evidence) is { Length: > 0 } said
            ? said
            : null;

    /// <summary>
    /// Every achievement, judged for one profile.
    /// </summary>
    /// <remarks>
    /// The order is the catalog's, which runs by category, so the screen reads as groups without the view
    /// having to group anything. Availability is judged from what is switched on right now rather than
    /// stored, because it changes with the server and with <c>Logging.LogLevel</c>.
    /// </remarks>
    /// <param name="debugLogging">Whether the server is logging at debug level at the moment.</param>
    /// <param name="databaseAnswers">Whether the player database answered on the last look.</param>
    public static IReadOnlyList<ProfileBadge> For(
        LauncherProfile profile, bool debugLogging, bool databaseAnswers)
        => [.. AchievementCatalog.All.Select(achievement => {
            var earned = profile.Achievements.FirstOrDefault(a => a.AchievementId == achievement.Id);

            return new ProfileBadge(
                achievement,
                earned?.EarnedAt,
                earned?.Evidence ?? string.Empty,
                AchievementCatalog.UnavailableBecause(achievement, debugLogging, databaseAnswers),
                AchievementScores.MultiplierFor(profile));
        })];
}

/// <summary>
/// The heading each category of achievements is shown under.
/// </summary>
/// <remarks>
/// One place rather than one per shape of achievement, because a ladder's card has to land in the same
/// group as the badges beside it: two mappings is how a card appears under a heading of its own.
/// </remarks>
public static class AchievementHeadings {

    /// <summary>The heading for a category.</summary>
    public static string Of(AchievementCategory category) => category switch {
        AchievementCategory.Playtime => "Time on the server",
        AchievementCategory.Exploration => "Discovery",
        AchievementCategory.Battle => "Battles",
        AchievementCategory.Collectathon => "What you have collected",
        AchievementCategory.Account => "Your wizards",
        AchievementCategory.Spells => "Spells learned",
        AchievementCategory.Progress => "How far you have got",
        _ => "How you played it",
    };
}

/// <summary>
/// One ladder as the profile shows it: where the account has got to on it, what the next milestone is, and
/// what that is worth.
/// </summary>
/// <remarks>
/// The rungs behind it are badges like any other - this is the card in front of them. A school's ladder is
/// forty rungs at the stock level cap and the creature ladder is one per twenty-five of the server's
/// creatures, so a tile each is how a profile turns into a wall of them; one card, a bar and a next
/// milestone is the same information in one read.
/// </remarks>
/// <param name="Track">The ladder this is.</param>
/// <param name="Reached">How far along it the account is, in the ladder's own units.</param>
/// <param name="Passed">How many rungs have been earned.</param>
/// <param name="Total">How many rungs the ladder has.</param>
/// <param name="Next">The next rung up, or null when the top has been reached.</param>
/// <param name="Points">What the earned rungs are worth on this profile, bonus included.</param>
/// <param name="NextPoints">What the next rung is worth when it is reached, bonus included.</param>
public sealed record ProfileMilestone(
    MilestoneTrack Track,
    int Reached,
    int Passed,
    int Total,
    int? Next,
    int Points,
    int NextPoints) {

    public string Id => Track.Id;

    public string Name => Track.Name;

    public string CategoryName => AchievementHeadings.Of(Track.Category);

    /// <summary>Where the account is on the ladder now.</summary>
    public string CurrentText => $"Now {Track.Describe(Reached)}";

    /// <summary>What the next milestone is, in the ladder's own words.</summary>
    /// <remarks>
    /// Named rather than counted, because "next milestone: level 15" is what a player can act on and
    /// "38 more levels" is arithmetic they would have to do themselves.
    /// </remarks>
    public string NextText => Next is { } next
        ? $"Next milestone: {Track.Describe(next)}"
        : "Every milestone reached";

    /// <summary>How far along the ladder the account is, as the number of rungs taken.</summary>
    public string RungsText => $"{Passed} of {Total} milestones";

    /// <summary>How far apart the ladder's rungs are, so the card explains its own shape.</summary>
    public string StepText => $"A milestone {Track.StepText}";

    /// <summary>What the ladder has scored so far.</summary>
    public string PointsText => Points == 1 ? "1 point earned here" : $"{Points} points earned here";

    /// <summary>What the next rung is worth, or nothing when there is no next rung.</summary>
    public string NextPointsText => Next is null ? string.Empty : $"+{NextPoints} at the next";

    public bool HasNext => Next is not null;

    public bool IsComplete => Next is null;

    /// <summary>
    /// How far it is from the last rung passed to the next one.
    /// </summary>
    /// <remarks>
    /// Measured between the two rungs rather than from zero, so the bar answers the question the card asks -
    /// how close the next milestone is. A wizard at level 12 on a five-level ladder is four fifths of the way
    /// from the rung at ten to the rung at fifteen, which is what the bar is for; a fraction of the whole
    /// ladder would say eight percent and mean nothing.
    /// </remarks>
    public double Fraction {
        get {
            if (Next is not { } next) {
                return 1;
            }

            var previous = Track.Rungs.LastOrDefault(rung => rung <= Reached);

            return next <= previous ? 0 : (double) (Reached - previous) / (next - previous);
        }
    }
}

/// <summary>
/// One world an account has been in, with the zones of it the account has found.
/// </summary>
/// <remarks>
/// This is Discovery's sub-grouping: the world is the heading and the zones are what has been found in it.
/// Only worlds that have actually been reached appear - a world nobody has been to is not a discovery, and
/// the badge for it is already on the screen, locked, saying what it would take.
/// </remarks>
/// <param name="World">The world's label: the first segment of the server's own zone path.</param>
/// <param name="Zones">
/// The named zones of it this account has been in, in the order they were visited. A zone the server only
/// numbered is in the account's list but not here, because there is nothing to call it.
/// </param>
public sealed record ProfileWorld(string World, IReadOnlyList<string> Zones) {

    /// <summary>The world, said the way the game says it.</summary>
    public string Name => AchievementCatalog.WorldName(World);

    /// <summary>How many of its zones have been found.</summary>
    public string ZonesText => Zones.Count switch {
        0 => "no zones named yet",
        1 => "1 zone",
        var count => $"{count} zones",
    };

    /// <summary>The zones themselves, on one line.</summary>
    public string ZoneList => string.Join(" · ", Zones);

    public bool HasZones => Zones.Count > 0;

    /// <summary>
    /// Two of these are the same world when they name the same world and hold the same zones.
    /// </summary>
    /// <remarks>
    /// Written out because the zones are a list, and a record compares a list by reference: two worlds
    /// saying the same thing would come out different, and the group would be rebuilt - and the page put back
    /// to the top - every time the profile was looked at again.
    /// </remarks>
    public bool Equals(ProfileWorld? other)
        => other is not null
           && string.Equals(World, other.World, StringComparison.Ordinal)
           && Zones.SequenceEqual(other.Zones, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(World, Zones.Count);
}

/// <summary>
/// One category of achievements as the profile shows it: the heading, how much of it is unlocked, what it
/// has scored so far, and whether its badges are on screen.
/// </summary>
/// <remarks>
/// <para>
/// The groups come out of the catalog's own categories rather than from a list kept beside them, so a
/// badge added to the catalog turns up under its heading with no second place to update it - and a group
/// can never hold a badge the catalog does not define.
/// </para>
/// <para>
/// A group can be put away, because the catalog is long enough that its headings are what make it
/// readable: with every badge of every category open at once, the list is back to being the wall the
/// groupings were meant to break up.
/// </para>
/// </remarks>
public sealed partial class ProfileBadgeGroup : ObservableObject {

    /// <param name="category">The category of achievements this group is.</param>
    /// <param name="badges">The badges in it, in the catalog's order.</param>
    /// <param name="milestones">The ladders shown in it, in the catalog's order.</param>
    /// <param name="worlds">The worlds discovered in it, in the order they were discovered.</param>
    public ProfileBadgeGroup(
        AchievementCategory category,
        IReadOnlyList<ProfileBadge> badges,
        IReadOnlyList<ProfileMilestone>? milestones = null,
        IReadOnlyList<ProfileWorld>? worlds = null) {
        Category = category;
        Badges = [.. badges];
        Milestones = [.. milestones ?? []];
        Worlds = [.. worlds ?? []];
    }

    /// <summary>Which category of achievements this is.</summary>
    public AchievementCategory Category { get; }

    /// <summary>The heading, in the words the screen uses for that category.</summary>
    public string Name => AchievementHeadings.Of(Category);

    /// <summary>
    /// The badges in this category that are badges of their own, in the catalog's order.
    /// </summary>
    /// <remarks>
    /// A collection rather than a list, because the group is kept on screen when the profile is looked at
    /// again: replacing its contents in place is what leaves an open group open and the page where it was.
    /// A ladder's rungs are not in here - they are counted in <see cref="Milestones"/> instead, which is
    /// what the screen shows for them.
    /// </remarks>
    public ObservableCollection<ProfileBadge> Badges { get; }

    /// <summary>The ladders in this category, as one card each.</summary>
    public ObservableCollection<ProfileMilestone> Milestones { get; }

    public bool HasMilestones => Milestones.Count > 0;

    /// <summary>
    /// The worlds discovered in this category, each with the zones found in it.
    /// </summary>
    /// <remarks>
    /// Only Discovery has any, and the sub-grouping is the point of it: a world is what a zone is part of,
    /// and the zones are what somebody went looking for.
    /// </remarks>
    public ObservableCollection<ProfileWorld> Worlds { get; }

    public bool HasWorlds => Worlds.Count > 0;

    /// <summary>
    /// Makes this group's contents the ones given, without replacing the group itself.
    /// </summary>
    /// <remarks>
    /// Nothing is touched when the contents are already the same. That is the ordinary case - most looks
    /// at the player database learn nothing new - and doing it in place is what stops a poll a few seconds
    /// apart from folding the groups back up under somebody who is reading them.
    /// </remarks>
    /// <param name="badges">Its badges, without the rungs of the ladders it holds.</param>
    /// <param name="milestones">Its ladders, with where the account is on each of them.</param>
    /// <param name="worlds">Its discovered worlds, with the zones found in each.</param>
    public void Replace(
        IReadOnlyList<ProfileBadge> badges,
        IReadOnlyList<ProfileMilestone> milestones,
        IReadOnlyList<ProfileWorld> worlds) {

        if (!Badges.SequenceEqual(badges)) {
            Badges.Clear();

            foreach (var badge in badges) {
                Badges.Add(badge);
            }
        }

        if (!Milestones.SequenceEqual(milestones)) {
            Milestones.Clear();

            foreach (var milestone in milestones) {
                Milestones.Add(milestone);
            }
        }

        if (!Worlds.SequenceEqual(worlds)) {
            Worlds.Clear();

            foreach (var world in worlds) {
                Worlds.Add(world);
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="into"/> hold exactly the groups in <paramref name="from"/>, in that order.
    /// </summary>
    /// <remarks>
    /// The groups that are already there are kept and only refilled, so a category the reader has opened
    /// stays open and the scroll position survives a badge being earned. Only a category the catalog no
    /// longer defines is dropped, and only a new one is built - which is also the only case where a group
    /// arrives at all, and arrives shut.
    /// </remarks>
    public static void Sync(
        ObservableCollection<ProfileBadgeGroup> into, IReadOnlyList<ProfileBadgeGroup> from) {

        var wanted = from.Select(g => g.Category).ToHashSet();

        for (var index = into.Count - 1; index >= 0; index--) {
            if (!wanted.Contains(into[index].Category)) {
                into.RemoveAt(index);
            }
        }

        for (var index = 0; index < from.Count; index++) {
            var source = from[index];
            var existing = into.FirstOrDefault(g => g.Category == source.Category);

            if (existing is null) {
                into.Insert(index, source);
                continue;
            }

            existing.Replace(source.Badges, source.Milestones, source.Worlds);

            // The catalog decides the order, so a group can move when a category is added or removed - but
            // it keeps its identity, and with it whether it was open.
            var at = into.IndexOf(existing);
            if (at != index) {
                into.Move(at, index);
            }
        }
    }

    /// <summary>
    /// Whether this group's badges are on screen.
    /// </summary>
    /// <remarks>
    /// Shut to begin with, because the catalog is long: forty rungs per school at the stock level cap, plus
    /// a battle ladder, and a screen that opens with seven categories fully unrolled is a wall of badges
    /// with the headings that make sense of them lost in the middle. Shut, the screen is a list of
    /// categories and what each has scored, which is the question a profile is opened to answer - the
    /// badges are one click away.
    /// </remarks>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The arrow beside the heading, which says which way the toggle will go.</summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    /// <summary>Opens or closes the group, which is what the heading does when it is clicked.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>
    /// How many of the group's badges have been earned, rungs of its ladders included.
    /// </summary>
    /// <remarks>
    /// The rungs count here even though they are not drawn as tiles: the heading is what a reader checks the
    /// score against, and a total that left them out would not add up to the profile's own.
    /// </remarks>
    public int Unlocked => Badges.Count(b => b.IsEarned) + Milestones.Sum(m => m.Passed);

    public int Total => Badges.Count + Milestones.Sum(m => m.Total);

    /// <summary>
    /// What this group's earned badges are worth.
    /// </summary>
    /// <remarks>
    /// Summed from what each badge is worth on this profile - bonus included - rather than divided out of
    /// the profile's total, so the groups add up to the score on the card above them and a group can be
    /// read on its own.
    /// </remarks>
    public int Points
        => Badges.Where(b => b.IsEarned).Sum(b => b.Points) + Milestones.Sum(m => m.Points);

    /// <summary>How much of the group is done.</summary>
    public string ProgressText => $"Unlocked {Unlocked} of {Total}";

    public string PointsText => Points == 1 ? "1 point" : $"{Points} points";

    /// <summary>The heading's own line: how much of the group is done, and what it has scored.</summary>
    public string Summary => $"{ProgressText} · {PointsText}";

    /// <summary>How far through the group is, for the bar under the heading.</summary>
    public double Fraction => Total == 0 ? 0 : (double) Unlocked / Total;

    /// <summary>Whether anything in the group has been earned yet.</summary>
    public bool HasUnlocked => Unlocked > 0;

    /// <summary>
    /// The badges, gathered under the heading of the category each belongs to, with each category's ladders
    /// gathered into one card apiece.
    /// </summary>
    /// <remarks>
    /// The groups keep the order they are first met in, and the catalog runs by category - so the screen
    /// reads in the catalog's own order without either side holding a list of headings.
    /// </remarks>
    /// <param name="profile">Whose badges these are, which is what the ladders and worlds are read off.</param>
    /// <param name="badges">Every badge, in the catalog's order.</param>
    /// <param name="zoneWorlds">Which world each zone belongs to, as far as the launcher has learned.</param>
    public static IReadOnlyList<ProfileBadgeGroup> For(
        LauncherProfile profile,
        IReadOnlyList<ProfileBadge> badges,
        IReadOnlyDictionary<string, string>? zoneWorlds = null)

        => [.. badges.GroupBy(b => b.Achievement.Category).Select(group => new ProfileBadgeGroup(
            group.Key,
            [.. group.Where(b => b.Achievement.TrackId is null)],
            [.. Ladders(profile, group)],
            group.Key == AchievementCategory.Exploration
                ? [.. Discovered(profile, zoneWorlds ?? NoZones)]
                : []))];

    /// <summary>No zone-world pairings at all, for a caller with nothing to pair them from.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoZones =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The worlds the account has been in, each with the zones of it that have been found.
    /// </summary>
    /// <remarks>
    /// Worked out from the zone list the profile keeps and the world each zone belongs to, which is what the
    /// ledger learns from the server's log. A zone whose world is not known yet is not guessed at - it simply
    /// appears under no world - and the worlds come in the order they were discovered, which is how somebody
    /// would tell the story of their own map.
    /// </remarks>
    private static IEnumerable<ProfileWorld> Discovered(
        LauncherProfile profile, IReadOnlyDictionary<string, string> zoneWorlds) {

        foreach (var world in profile.WorldsVisited) {
            var zones = new List<string>();

            foreach (var zone in profile.ZonesVisited) {
                if (!zoneWorlds.TryGetValue(zone, out var belongs)
                    || !string.Equals(belongs, world, StringComparison.OrdinalIgnoreCase)
                    || ZoneNames.NameOf(zone) is not { Length: > 0 } name
                    || zones.Contains(name, StringComparer.Ordinal)) {
                    continue;
                }

                zones.Add(name);
            }

            yield return new ProfileWorld(world, zones);
        }
    }

    /// <summary>
    /// The ladders among a category's badges, each as one card.
    /// </summary>
    /// <remarks>
    /// Read out of the badges rather than recomputed, so the card and the rungs behind it can never disagree
    /// about what has been earned: how many rungs have been taken and what they were worth is what the
    /// profile actually holds, and only the position on the ladder - which comes off the account, not off the
    /// badges - is asked of the track.
    /// </remarks>
    private static IEnumerable<ProfileMilestone> Ladders(
        LauncherProfile profile, IEnumerable<ProfileBadge> category) {

        var byTrack = category
            .Where(b => b.Achievement.TrackId is not null)
            .GroupBy(b => b.Achievement.TrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var track in AchievementCatalog.Tracks) {
            if (!byTrack.TryGetValue(track.Id, out var rungs)) {
                continue;
            }

            var reached = track.Reached(profile);
            var next = track.Next(reached);
            var multiplier = rungs[0].Multiplier;

            yield return new ProfileMilestone(
                track,
                reached,
                rungs.Count(b => b.IsEarned),
                rungs.Count,
                next,
                rungs.Where(b => b.IsEarned).Sum(b => b.Points),
                next is { } at ? AchievementCatalog.ById($"{track.Id}-{at}")!.Score * multiplier : 0);
        }
    }
}
