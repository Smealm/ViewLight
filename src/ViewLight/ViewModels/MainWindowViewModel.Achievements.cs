using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ViewLight.Models;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// Achievements as the launcher announces them: a toast that slides in, with the badge, what earned it and
/// what it was worth, and a chime to go with it.
/// </summary>
/// <remarks>
/// <para>
/// Badges are earned by a background watch - the log being read, the database being polled - so the
/// announcement has to work from wherever that lands. Everything about it is marshalled onto the UI thread
/// here, and one toast is shown at a time: a poll that turns up three badges queues three, rather than
/// stacking them on top of each other where only the last could be read.
/// </para>
/// <para>
/// The score is deliberately derived from the catalog rather than stored, so the numbers on screen and the
/// numbers in the rules cannot drift apart.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>How long a toast stays up before the next one - or nothing - takes its place.</summary>
    public static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(5);

    /// <summary>One queued announcement.</summary>
    /// <param name="Title">The badge's name.</param>
    /// <param name="Detail">What earned it, in the server's own words.</param>
    /// <param name="Points">What it was worth, already written out.</param>
    private sealed record Toast(string Title, string Detail, string Points);

    private readonly Queue<Toast> _toastQueue = new();
    private DispatcherTimer? _toastTimer;

    /// <summary>Whether a toast is on screen.</summary>
    [ObservableProperty] private bool _isToastShown;

    [ObservableProperty] private string _toastTitle = string.Empty;
    [ObservableProperty] private string _toastDetail = string.Empty;
    [ObservableProperty] private string _toastPoints = string.Empty;

    /// <summary>
    /// Whether milestones are announced, kept in step with the launcher's own settings file.
    /// </summary>
    [ObservableProperty] private bool _showMilestoneToasts = true;

    partial void OnShowMilestoneToastsChanged(bool value) {
        _store.Settings.ShowMilestoneToasts = value;
        _store.Save();
    }

    /// <summary>
    /// Queues an announcement for every achievement just earned, and plays the chime once however many
    /// there are - one sound per poll rather than per badge, so a busy poll does not turn into a fanfare.
    /// </summary>
    public void AnnounceAchievements(IReadOnlyList<ProfileAward> awards) {
        var announced = 0;

        foreach (var award in awards) {
            var definition = AchievementCatalog.ById(award.Achievement.AchievementId);

            // A milestone is one rung of a ladder that somebody may be climbing quickly, so a player who
            // has asked not to be told about them is told about nothing here. The badge is still earned and
            // still worth its points - this is the announcement being switched off, not the achievement.
            if (definition is { IsMilestone: true } && !ShowMilestoneToasts) {
                continue;
            }

            var name = definition?.Name ?? award.Achievement.AchievementId;

            _toastQueue.Enqueue(new Toast(
                $"{award.Profile.Name} — {name}",
                AwardLine(award),
                definition is null ? string.Empty : $"+{definition.PointsFor(award.Profile)} points"));

            announced++;
        }

        if (announced == 0) {
            return;
        }

        AchievementSound.Play();

        Post(ShowNextToast);
    }

    /// <summary>
    /// What the toast says the badge was for.
    /// </summary>
    /// <remarks>
    /// The requirement - "Win ten duels." - rather than the evidence a badge keeps. A badge in the profile
    /// quotes the server's own line so that it can be checked afterwards; a toast is on screen for a few
    /// seconds and has to be understood in one read, so it says what was done in the catalog's own plain
    /// words. A badge whose id is no longer in the catalog has no words to use and falls back to the line.
    /// </remarks>
    private static string AwardLine(ProfileAward award)
        => AchievementCatalog.ById(award.Achievement.AchievementId)?.Requirement
           ?? AchievementEvidence.ForDisplay(award.Achievement.Evidence);

    /// <summary>Shows the next queued toast, or clears the screen when there is none.</summary>
    private void ShowNextToast() {
        _toastTimer?.Stop();
        _toastTimer = null;

        if (_toastQueue.Count == 0) {
            // Hidden rather than emptied, so the slide-out is animated with the last toast's words still in
            // it rather than the card collapsing as it leaves.
            IsToastShown = false;
            return;
        }

        var toast = _toastQueue.Dequeue();
        ToastTitle = toast.Title;
        ToastDetail = toast.Detail;
        ToastPoints = toast.Points;
        IsToastShown = true;

        _toastTimer = new DispatcherTimer { Interval = ToastDuration };
        _toastTimer.Tick += (_, _) => ShowNextToast();
        _toastTimer.Start();
    }

    /// <summary>Dismisses the toast on screen and everything waiting behind it.</summary>
    public void DismissToasts() {
        _toastQueue.Clear();
        ShowNextToast();
    }

    // ---------------------------------------------------------------- the score

    /// <summary>What the selected profile has scored with the achievements it holds.</summary>
    public int ProfileScore => SelectedProfile is { } profile ? AchievementScores.Of(profile) : 0;

    /// <summary>
    /// The best score any profile on this machine has reached - the number to beat, which is what makes a
    /// score worth having at all.
    /// </summary>
    public int HighestScore => _profiles.Profiles.Count == 0
        ? 0
        : _profiles.Profiles.Max(AchievementScores.Of);

    /// <summary>
    /// What the selected profile's score is multiplied by, and why.
    /// </summary>
    /// <remarks>
    /// Shown beside the score rather than left implicit: a doubled total that nothing on screen explains
    /// is a total the badges underneath cannot account for.
    /// </remarks>
    public string ProfileMultiplierText => AchievementScores.MultiplierNote(SelectedProfile);

    /// <summary>The selected profile's score, leading with the badge count so both halves are visible.</summary>
    public string ProfileScoreText => SelectedProfile is null
        ? string.Empty
        : $"{ProfileScore} points · {SelectedProfile.Achievements.Count} of {AchievementCatalog.All.Count} badges";

    /// <summary>
    /// The score alone, as the circle that stands for it shows it.
    /// </summary>
    /// <remarks>
    /// The number and nothing else, because the circle is where the score is read at a glance: the words
    /// that explain it - what it was scored from, and what it is doubled by - are the line beside it.
    /// </remarks>
    public string ProfileScoreCircleText => SelectedProfile is null
        ? "–"
        : ProfileScore.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How much of the catalog the profile holds, said the way the card says it.
    /// </summary>
    /// <remarks>
    /// The score's own circle carries the number, so this is the half of it a circle cannot show: how many
    /// of the badges that number was scored from are held.
    /// </remarks>
    public string ProfileBadgeCountText => SelectedProfile is null
        ? string.Empty
        : $"{SelectedProfile.Achievements.Count} of {AchievementCatalog.All.Count} badges earned";

    /// <summary>The best score on this machine, said as a comparison when it is not the selected one.</summary>
    public string HighestScoreText => SelectedProfile is not null && ProfileScore >= HighestScore
        ? "Best on this machine"
        : $"Best on this machine: {HighestScore} points";

    // ---------------------------------------------------------------- bosses

    /// <summary>
    /// The server's creature list, once it has been read. Null until then, and null for a server folder
    /// that has no manifests.
    /// </summary>
    private ImlightManifests? _manifests;

    /// <summary>How many of the game's bosses the selected profile has beaten.</summary>
    /// <remarks>
    /// Counted against the server's own creature list rather than against what the profile remembers, so
    /// the total is the game's and does not drift: a boss beaten is one of the manifest's bosses by ID, and
    /// a creature the manifest does not list is not counted at all.
    /// </remarks>
    public int ProfileBossesDefeated => _manifests is null || SelectedProfile is null
        ? 0
        : _manifests.Bosses.Count(b => SelectedProfile.HasBeaten(b.TemplateId));

    /// <summary>
    /// What the profile screen says about bosses, including why it is not saying anything when the
    /// server's creature list is missing.
    /// </summary>
    public string ProfileBossesText => SelectedProfile is null
        ? string.Empty
        : _manifests is not { HasCreatures: true } manifests
            ? "Bosses cannot be counted yet: the server has no "
              + $"{ImlightManifestReader.RelativeDirectory}{Path.DirectorySeparatorChar}"
              + $"{ImlightManifestReader.CreatureFileName}, and that list is what says which of the "
              + "creatures a duel was against was a boss. The creatures themselves are counted anyway, "
              + "from the duels your account wins."
            : $"{ProfileBossesDefeated} of {manifests.Bosses.Count:N0} bosses beaten"
              + (ProfileBossesDefeated == 0
                  ? " — a creature counts as a boss once a duel names it and the server's own list has it "
                    + "as one."
                  : string.Empty);

    /// <summary>
    /// Takes the server's creature list, once it has been read off disk.
    /// </summary>
    /// <remarks>
    /// The list is also what tells a boss apart from an ordinary creature, so the profiles are re-counted
    /// against it here rather than only when a duel is won: the list is a file that can turn up after
    /// somebody has already beaten a boss, and a badge that depends on which of the two was read first
    /// would be no badge at all.
    /// </remarks>
    private void UseManifests(ImlightManifests manifests) {
        _manifests = manifests;

        if (manifests.HasCreatures) {
            // The whole list goes to the ledger: a duel names the creatures it was against and never says
            // which of them was a boss, and the list is where that answer lives.
            var earned = _ledger.UseCreatureRoster(manifests.Creatures, DateTimeOffset.Now);

            if (earned.Count > 0) {
                RefreshProfiles();
                AnnounceAchievements(earned);
            }
        }

        OnPropertyChanged(nameof(ProfileBossesDefeated));
        OnPropertyChanged(nameof(ProfileBossesText));
    }

}
