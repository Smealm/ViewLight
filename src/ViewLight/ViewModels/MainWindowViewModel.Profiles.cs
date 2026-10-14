using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The profile half of the launcher: who you are on this server, how long you have played it, and what you
/// have done on it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is fed by the server this launcher started, and by nothing else. That is the design
/// rather than a limitation to be worked around: a profile is a local thing, its achievements are earned
/// against a server on this machine, and joining someone else's server - or someone joining this one - is
/// not written down anywhere that follows the account around. Two launchers never talk to each other.
/// </para>
/// <para>
/// The launcher can only say what it saw, so what it cannot attribute it refuses to give away: see
/// <see cref="ProfileLedger"/>. The screen says the same thing in the same words, because a badge the user
/// cannot reproduce is worse than a badge they have not earned yet.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>
    /// How often the launcher looks at who is signed in and at what the database now says.
    /// </summary>
    /// <remarks>
    /// Ten seconds, and it is the resolution of playtime: a session is measured between the looks that saw
    /// the account signed in, so the total is accurate to within one look per session rather than invented
    /// in between.
    /// </remarks>
    public static readonly TimeSpan ProfilePollInterval = TimeSpan.FromSeconds(10);

    /// <summary>How much of the server log one look may read, so a huge file cannot stall the UI thread.</summary>
    private const int MaxLogReadBytes = 4 * 1024 * 1024;

    private readonly ProfileStore _profiles = new();
    private readonly ProfileLedger _ledger;

    private CancellationTokenSource? _profileWatch;

    /// <summary>The picture and banner currently decoded, so they are not re-read on every refresh.</summary>
    private (ulong AccountId, string? Avatar, string? Banner) _profileImages;

    /// <summary>The log the watch is reading, or null when nothing is being watched.</summary>
    private string? _profileLogPath;

    /// <summary>
    /// Where in that log the next look starts, or -1 until the file exists.
    /// </summary>
    /// <remarks>
    /// A file the server has not opened yet gets its offset taken from its length at the first look, so
    /// only what this launcher's own server run writes is counted. The log is one file per day and the
    /// server appends to it, so without that the whole of today would be folded in again on every start.
    /// </remarks>
    private long _profileLogOffset = -1;

    /// <summary>The profiles, which are the accounts plus whatever this launcher has learned about them.</summary>
    public ObservableCollection<LauncherProfile> Profiles { get; } = [];

    /// <summary>The profile the screen is showing, which is also who Play signs in as.</summary>
    [ObservableProperty] private LauncherProfile? _selectedProfile;

    /// <summary>The achievements, in catalog order: earned with their date, locked with their requirement.</summary>
    public ObservableCollection<ProfileBadge> Badges { get; } = [];

    /// <summary>
    /// The same badges, gathered under their category headings, which is how the profile shows them.
    /// </summary>
    /// <remarks>
    /// Every badge at once is a wall: there are scores of them, they run from "sign in once" to "a hundred
    /// duels", and a flat list gives no way to see what is left in the part you are working on. The groups
    /// carry their own progress and score, so the answer to "how am I doing" is readable before any badge
    /// is.
    /// </remarks>
    public ObservableCollection<ProfileBadgeGroup> BadgeGroups { get; } = [];

    public bool HasBadgeGroups => BadgeGroups.Count > 0;

    [ObservableProperty] private Bitmap? _profileAvatar;
    [ObservableProperty] private Bitmap? _profileBanner;

    /// <summary>
    /// The last thing the launcher learned about a profile, or why it is not learning anything just now.
    /// </summary>
    [ObservableProperty] private string _profileNotice = string.Empty;

    /// <summary>Set when the player database did not describe wizards the way this launcher expects.</summary>
    [ObservableProperty] private string? _profileDataProblem;

    public bool HasProfileNotice => !string.IsNullOrWhiteSpace(ProfileNotice);

    partial void OnProfileNoticeChanged(string value) => OnPropertyChanged(nameof(HasProfileNotice));

    // ---------------------------------------------------------------- what the screen reads

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasProfile => SelectedProfile is not null;

    public bool HasProfileAvatar => ProfileAvatar is not null;

    public bool HasProfileBanner => ProfileBanner is not null;

    public bool HasProfileDataProblem => !string.IsNullOrWhiteSpace(ProfileDataProblem);

    /// <summary>
    /// Profiles this machine remembers for accounts the server it is pointed at does not have.
    /// </summary>
    /// <remarks>
    /// Set while the profile list is rebuilt, and shown so a profile that is not listed is accounted for
    /// rather than silently missing.
    /// </remarks>
    private string UnlistedProfilesNotice { get; set; } = string.Empty;

    public bool HasUnlistedProfiles => UnlistedProfilesNotice.Length > 0;

    public string UnlistedProfilesText => UnlistedProfilesNotice;

    /// <summary>The profile's own name, or the account's when it has none of its own.</summary>
    public string ProfileName => SelectedProfile?.Name ?? "No profile yet";

    public string ProfileAccountLine => SelectedProfile is null
        ? "No profile selected."
        : $"Account “{SelectedProfile.Username}” · {SelectedProfile.AccountId}";

    /// <summary>
    /// The account Imlight creates for the server when it first boots, or null when this launcher has no
    /// server here, or that server does not name one yet.
    /// </summary>
    /// <remarks>
    /// It is the only account that exists before anybody has signed in, and the launcher knows about it
    /// from <c>Config/Imlight.ini</c> alone - which is what lets the screen say what a server starts with
    /// rather than showing an empty picker. It cannot be a profile yet: a profile is keyed by the account's
    /// own id, and the id is only learned from the player database once the server has run.
    /// </remarks>
    public string? StarterAccountUsername => StarterAccountName(_config?.AdminUsername, _config?.AdminPassword);

    /// <summary>
    /// The name above, with the rule in it so it can be pinned down without a config file.
    /// </summary>
    /// <remarks>
    /// An account Imlight creates needs both halves in the config. A username with no password names
    /// nothing it can sign in with, so it is treated as no account rather than half of one.
    /// </remarks>
    public static string? StarterAccountName(string? adminUsername, string? adminPassword)
        => string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword)
            ? null
            : adminUsername.Trim();

    /// <summary>Whether there is an account to show before anything has ever been played here.</summary>
    public bool HasStarterAccount => StarterAccountUsername is not null;

    /// <summary>The heading over that account, or the plain truth when there is none.</summary>
    public string StarterAccountTitle => StarterAccountUsername is { } admin
        ? $"{admin} — the account your server starts with"
        : "No profile";

    /// <summary>The letters that account is drawn under, when there is no picture to draw it with.</summary>
    public string StarterAccountInitials => InitialsOf(StarterAccountUsername ?? string.Empty);

    /// <summary>
    /// What the profile screen says when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// It leads with the truth rather than filling the gap with a guess: an account exists only once a
    /// server <i>here</i> has made one, so a launcher with no server, or with a server whose config names
    /// no admin account, has no profile to offer at all.
    /// </remarks>
    public string StarterAccountSummary
        => StarterAccountText(_config is not null, _config?.AdminUsername, _config?.AdminPassword);

    /// <summary>The same answers with nothing read off disk, so they can be tested as words.</summary>
    public static string StarterAccountText(bool serverInstalled, string? adminUsername, string? adminPassword) {
        if (!serverInstalled) {
            return "There is no server here yet, and a server is what accounts belong to: Imlight makes its "
                   + "first one when it first boots. Download the server under Settings and it appears here.";
        }

        var admin = StarterAccountName(adminUsername, adminPassword);
        if (admin is null) {
            return $"{ImlightConfig.RelativeConfigPath} does not name an admin account yet, so there is "
                   + "nothing to start with. Set one under Settings, and the server creates it the next time "
                   + "it boots.";
        }

        return $"Imlight creates “{admin}” the first time the server boots, so it is the only account that "
               + "exists before anybody has signed in. Make another under Settings, where the server's "
               + "account management is, or sign in as this one and say .account create $username $password "
               + "in the game — every account appears here as a profile once the server has been started.";
    }

    /// <summary>The letters a profile with no picture of its own is drawn under.</summary>
    public string ProfileInitials => InitialsOf(SelectedProfile?.Name ?? string.Empty);

    public string ProfilePlaytimeText => DescribeDuration(SelectedProfile?.PlaytimeSeconds ?? 0);

    public string ProfileSessionsText => (SelectedProfile?.SessionCount ?? 0) switch {
        0 => "Never seen signed in",
        1 => "Signed in once",
        var count => $"Signed in {count} times",
    };

    public string ProfileFirstPlayedText => DescribeFirstPlayed(SelectedProfile);

    public string ProfileLastPlayedText => SelectedProfile?.LastPlayedAt is { } last
        ? $"Last signed in {last.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "Not signed in on this machine yet";

    /// <summary>
    /// The standing, in the profile's own words: nothing but the game, or commands on the account.
    /// </summary>
    public string ProfileStandingText => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.IsClean
            ? "Clean"
            : $"{SelectedProfile.CommandsUsed} command(s) used";

    public string ProfileStandingDetail => SelectedProfile switch {
        null => string.Empty,
        { IsClean: true } => "The server has never logged a command against this account, so what it has "
                             + "earned here was earned in the game.",
        { LastCommand: { Length: > 0 } command, LastCommandAt: { } at } =>
            $"The server logged “{command}” most recently, at {at.ToLocalTime():yyyy-MM-dd HH:mm}.",
        _ => "The server has logged commands against this account.",
    };

    /// <summary>Whether anything about this profile does not count as clean.</summary>
    public bool ProfileIsNotClean => SelectedProfile is { IsClean: false };

    public IReadOnlyList<ProfileWizard> ProfileWizards => SelectedProfile?.Wizards ?? [];

    public bool HasProfileWizards => ProfileWizards.Count > 0;

    public string ProfileWizardsSummary => SelectedProfile is null || SelectedProfile.Wizards.Count == 0
        ? "No wizards have been read yet — they come from the player database while the server runs."
        : string.Empty;

    // ---------------------------------------------------------------- the profile's two pages

    /// <summary>
    /// Which page of the profile was asked for.
    /// </summary>
    /// <remarks>
    /// Asked for rather than showing, because the achievements screen is a page of the profile and not a
    /// section of its own: it is honoured on Profile and nowhere else, and nobody can be looking at the
    /// account's badges while looking at Home.
    /// </remarks>
    [ObservableProperty] private ProfilePage _profilePage = ProfilePage.Profile;

    /// <summary>Whether the profile's own page is the one on screen.</summary>
    public bool ShowProfilePage => IsProfileSelected && !ShowAchievementsPage;

    /// <summary>Whether the achievements page is the one on screen.</summary>
    public bool ShowAchievementsPage => ShowsAchievements(Section, ProfilePage);

    /// <summary>Whether a section and an asked-for page together mean the achievements screen.</summary>
    public static bool ShowsAchievements(LauncherSection section, ProfilePage page)
        => section == LauncherSection.Profile && page == ProfilePage.Achievements;

    partial void OnProfilePageChanged(ProfilePage value) {
        OnPropertyChanged(nameof(ShowProfilePage));
        OnPropertyChanged(nameof(ShowAchievementsPage));

        // The heading over the content says which page this is, so it moves with it.
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));
    }

    /// <summary>Opens the achievements screen, which is what the button under the score circle does.</summary>
    [RelayCommand]
    private void OpenAchievements() => ProfilePage = ProfilePage.Achievements;

    /// <summary>Goes back to the profile, which is where the achievements screen was opened from.</summary>
    [RelayCommand]
    private void CloseAchievements() => ProfilePage = ProfilePage.Profile;

    /// <summary>
    /// Takes the account section at its profile page, which is where the card in the corner leads.
    /// </summary>
    /// <remarks>
    /// Not simply a navigation: the card is asking for the account, so it comes back to the profile even
    /// when the achievements screen was the last thing read there.
    /// </remarks>
    [RelayCommand]
    private void ShowAccount() {
        GoTo(LauncherSection.Profile);
        CloseProfileCard();
    }

    // ---------------------------------------------------------------- the profile card in the header

    /// <summary>
    /// How long the open profile card waits after the pointer leaves it before folding back.
    /// </summary>
    /// <remarks>
    /// A moment's grace rather than folding the instant the pointer moves off: the card is small, and a
    /// pointer on its way somewhere else is not a request to close it.
    /// </remarks>
    public static readonly TimeSpan ProfileCardLingerDuration = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// Whether the profile card in the window's header is open, rather than just the picture and the name.
    /// </summary>
    [ObservableProperty] private bool _isProfileCardOpen;

    /// <summary>Whether the pointer is on the card, which is what the countdown to folding it back waits on.</summary>
    private bool _pointerOverProfileCard;

    private DispatcherTimer? _profileCardLinger;

    /// <summary>
    /// Opens the card. What that shows is the banner, the name and the score, and clicking it again is what
    /// takes the account section.
    /// </summary>
    [RelayCommand]
    public void OpenProfileCard() {
        CancelProfileCardLinger();
        IsProfileCardOpen = true;
    }

    /// <summary>
    /// Notes whether the pointer is on the card.
    /// </summary>
    /// <remarks>
    /// Leaving it starts the countdown to folding it back and coming back cancels it, so a pointer that
    /// merely crosses the card on its way past leaves it as it was rather than collapsing it behind the
    /// pointer.
    /// </remarks>
    public void NoteProfileCardPointer(bool over) {
        _pointerOverProfileCard = over;

        if (over) {
            CancelProfileCardLinger();
            return;
        }

        if (!IsProfileCardOpen || _profileCardLinger is not null) {
            return;
        }

        _profileCardLinger = new DispatcherTimer { Interval = ProfileCardLingerDuration };
        _profileCardLinger.Tick += (_, _) => FoldProfileCardAway();
        _profileCardLinger.Start();
    }

    /// <summary>
    /// Folds the card back to the picture and the name, which is what the shrink animation shows.
    /// </summary>
    /// <remarks>
    /// Nothing happens while the pointer is on it: the countdown can expire with the pointer back on the
    /// card - a timer that fires just as the pointer returns - and folding it up under the pointer is the
    /// one thing this must not do.
    /// </remarks>
    public void FoldProfileCardAway() {
        CancelProfileCardLinger();

        if (ProfileCardShouldFold(IsProfileCardOpen, _pointerOverProfileCard)) {
            IsProfileCardOpen = false;
        }
    }

    /// <summary>
    /// Whether the moment to fold the card back has arrived, which needs it open and the pointer off it.
    /// </summary>
    /// <remarks>
    /// The countdown can expire in the same instant the pointer arrives back on the card - a timer that
    /// fires while the pointer is on its way in - and folding it up under the hand that is reaching for it
    /// is the one thing this must never do.
    /// </remarks>
    public static bool ProfileCardShouldFold(bool isOpen, bool pointerOver) => isOpen && !pointerOver;

    /// <summary>Closes the card whatever the pointer is doing, for when the card itself has been clicked.</summary>
    public void CloseProfileCard() {
        CancelProfileCardLinger();
        IsProfileCardOpen = false;
    }

    private void CancelProfileCardLinger() {
        _profileCardLinger?.Stop();
        _profileCardLinger = null;
    }

    public bool HasBadges => Badges.Count > 0;

    /// <summary>
    /// Whether any badge is waiting on the server logging at debug level, which is what the switch below is
    /// for.
    /// </summary>
    public bool HasBadgesWaitingOnDebugLogging
        => Badges.Any(b => b.IsUnavailable && b.Achievement.NeedsDebugLog);

    public bool ShowEnableDetailedLogging => HasBadgesWaitingOnDebugLogging && _config is not null && !DebugLogging;

    /// <summary>Whether the server's config asks for debug lines, which zone and duel badges are written at.</summary>
    public bool DebugLogging => _config is not null
        && (_config.LogLevel.Trim().ToUpperInvariant() is "DEBUG" or "TRACE");

    public string ProfileScopeNote =>
        "Profiles, playtime and achievements are local to this machine and this server. They are read from "
        + "your own server while ViewLight is running it, and nothing about them is sent anywhere - so "
        + "joining someone else's server, or someone joining yours, earns nothing here.";

    // ---------------------------------------------------------------- making an account

    /// <summary>The name and password the account about to be made is made with.</summary>
    [ObservableProperty] private string _newAccountUsername = string.Empty;
    [ObservableProperty] private string _newAccountPassword = string.Empty;

    /// <summary>What the last attempt to make an account had to say.</summary>
    [ObservableProperty] private string _createAccountNotice = string.Empty;

    public bool HasCreateAccountNotice => !string.IsNullOrWhiteSpace(CreateAccountNotice);

    partial void OnCreateAccountNoticeChanged(string value)
        => OnPropertyChanged(nameof(HasCreateAccountNotice));

    partial void OnNewAccountUsernameChanged(string value) => CreateAccountCommand.NotifyCanExecuteChanged();

    partial void OnNewAccountPasswordChanged(string value) => CreateAccountCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Whether an account can be made right now, which needs a server to make it on and something to make
    /// it with.
    /// </summary>
    private bool CanCreateAccount()
        => _config is not null
           && !IsBusy
           && !string.IsNullOrWhiteSpace(NewAccountUsername)
           && !string.IsNullOrEmpty(NewAccountPassword);

    /// <summary>
    /// Makes an account in the player database, in the shape Imlight's own accounts have.
    /// </summary>
    /// <remarks>
    /// This is the launcher writing Imlight's own storage format, which is why it is fenced the way it is:
    /// the database has to read back as the layout the launcher knows - anchored on the admin account
    /// Imlight creates on boot - before a single document is written, and the whole of that document is
    /// built by <see cref="PlayerAccountDocument"/> rather than anything being left to a default.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCreateAccount))]
    private async Task CreateAccountAsync() {
        if (_config is null) {
            return;
        }

        CreateAccountNotice = string.Empty;

        // The database has to be readable before anything is written into it, and reading it is what runs
        // the guard. A server that has never been started has made neither its database nor its admin
        // account, and between the two the guard has nothing to recognise - which is said rather than
        // worked around.
        await EnsureIdleDatabaseAsync().ConfigureAwait(true);

        var port = _config.EmbeddedDatabasePort;
        var contents = await _playerDatabase
            .ReadAsync(port, AdminUsername, AdminPassword)
            .ConfigureAwait(true);

        if (!contents.IsTrustworthy) {
            CreateAccountNotice = $"Nothing was written: {contents.Problem} Start the server once and "
                                  + "its account can be made here.";
            AppendLog(LogLevel.Warning, $"An account was not created: {contents.Problem}");
            return;
        }

        var name = NewAccountUsername.Trim();

        var (result, problem) = await _playerDatabase
            .CreateAccountAsync(port, contents, name, NewAccountPassword)
            .ConfigureAwait(true);

        if (result is not AccountCreation.Created) {
            CreateAccountNotice = problem ?? "The account could not be made.";
            AppendLog(LogLevel.Warning, $"An account was not created: {CreateAccountNotice}");
            return;
        }

        NewAccountUsername = string.Empty;
        NewAccountPassword = string.Empty;
        CreateAccountNotice = $"“{name}” is in the player database. Sign in as it in the game, or pick it "
                              + "here and press Play.";

        AppendLog(LogLevel.Success, $"Created the account “{name}” in the player database.");

        await RefreshAccountsAsync().ConfigureAwait(true);

        // A new account is a new profile, and making one is asking to play as it - so it is the one on
        // screen when this is done.
        _ledger.NoteAccounts(Accounts);
        RefreshProfiles();

        SelectedProfile = Profiles.FirstOrDefault(
            p => string.Equals(p.Username, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the account form says it will do, in the words of the guard that decides whether it can.
    /// </summary>
    public string CreateAccountSummary => _config is null
        ? "Download the server first: an account needs a database to live in."
        : "The account is written straight into your server's player database, in the same shape Imlight "
          + "writes its own — so it works in the game with no restart, and the launcher needs nothing from "
          + "the server but its files. Sign in as your admin account in the game and say "
          + ".account create $username $password for the same thing, or make one here while the server is "
          + "stopped.";

    // ---------------------------------------------------------------- starting and stopping

    /// <summary>
    /// Begins watching the server's log and database, which is where everything on this screen comes from.
    /// </summary>
    /// <remarks>
    /// Started when the server starts, because the server is the source: a server started outside ViewLight
    /// is not watched, and the screen says so by having nothing to add rather than by inventing it.
    /// </remarks>
    private void StartProfileWatch(string logPath) {
        StopProfileWatch();

        _profileLogPath = logPath;
        _profileLogOffset = -1;
        _profileWatch = new CancellationTokenSource();

        _ = WatchProfilesAsync(_profileWatch.Token);
    }

    private void StopProfileWatch() {
        _profileWatch?.Cancel();
        _profileWatch?.Dispose();
        _profileWatch = null;
        _profileLogPath = null;
        _profileLogOffset = -1;
    }

    private async Task WatchProfilesAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                await Task.Delay(ProfilePollInterval, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) {
                return;
            }

            // The watch outlives the server while the launcher is holding the database itself: there is
            // still something to read there - the wizards, the command log - just nobody signed in. When
            // neither is true, there is nothing to look at and the watch ends.
            if (!_idleDatabaseIsUp && (ServerState is ServerState.Stopped or ServerState.Stopping)) {
                return;
            }

            try {
                var lines = ReadNewLogLines();
                if (lines.Count > 0) {
                    Announce(_ledger.ApplyLogLines(lines), $"the server log said {lines.Count} new thing(s)");
                }

                await ReadProfilesAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) {
                return;
            }
            catch (Exception ex) {
                // A poll that fails is not worth stopping the watch for: the server may be mid-restart, and
                // the next look will tell. It is said out loud so a persistent problem is visible.
                ProfileNotice = $"The last look at the server did not work: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// The new part of the server's log, in whole lines only.
    /// </summary>
    /// <remarks>
    /// Read as bytes and cut at the last newline on purpose: a line the server is still writing is not a
    /// line, and decoding half of one would either lose it or read it twice.
    /// </remarks>
    private IReadOnlyList<string> ReadNewLogLines() {
        var path = _profileLogPath;
        if (path is null) {
            return [];
        }

        try {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (_profileLogOffset < 0) {
                // The first look: start at the end, so an earlier run's lines in today's file are not
                // counted as this one's.
                _profileLogOffset = stream.Length;
                return [];
            }

            if (stream.Length < _profileLogOffset) {
                // A shorter file means this is not the file it was: Imlight rolls one per day, and a fresh
                // one starts at the beginning.
                _profileLogOffset = 0;
            }

            var remaining = stream.Length - _profileLogOffset;
            if (remaining <= 0) {
                return [];
            }

            var buffer = new byte[(int) Math.Min(remaining, MaxLogReadBytes)];
            stream.Seek(_profileLogOffset, SeekOrigin.Begin);

            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var lastNewline = Array.LastIndexOf(buffer, (byte) '\n', read - 1, read);
            if (lastNewline < 0) {
                return [];
            }

            _profileLogOffset += lastNewline + 1;

            var (lines, _) = ServerLogFacts.CompleteLines(Encoding.UTF8.GetString(buffer, 0, lastNewline + 1));
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return [];
        }
    }

    /// <summary>
    /// One look at the player database: who is signed in, what the wizards are, and what commands the
    /// server has logged.
    /// </summary>
    private async Task ReadProfilesAsync(CancellationToken token) {
        if (_config is null) {
            return;
        }

        var data = await _playerDatabase.ReadProfileDataAsync(_config.EmbeddedDatabasePort, token)
            .ConfigureAwait(true);

        ProfileDataProblem = data.IsUsable
            ? null
            : $"Profiles are not being updated: {data.Problem}";

        if (!data.IsUsable) {
            // The read is refused rather than half-applied, so playtime and badges stand as they are and the
            // reason is on screen.
            RefreshProfileDerived();
            return;
        }

        Announce(_ledger.ApplyDatabase(data, DateTimeOffset.Now), null);
        RefreshProfiles();
    }

    /// <summary>
    /// Tells the user about achievements that have just been earned, in the Log and on the profile screen.
    /// </summary>
    private void Announce(IReadOnlyList<ProfileAward> awards, string? quietNote) {
        if (awards.Count == 0) {
            if (quietNote is not null) {
                ProfileNotice = $"Watching: {quietNote}.";
            }

            return;
        }

        foreach (var award in awards) {
            var definition = AchievementCatalog.ById(award.Achievement.AchievementId);

            AppendLog(LogLevel.Success,
                $"Achievement — {award.Profile.Name} earned {(definition?.Name ?? award.Achievement.AchievementId)}: "
                + award.Achievement.Evidence);
        }

        ProfileNotice = awards.Count == 1
            ? $"Earned: {AchievementCatalog.ById(awards[0].Achievement.AchievementId)?.Name}."
            : $"Earned {awards.Count} achievements.";

        // The badge itself goes up as a toast with a chime, wherever the launcher happens to be: a poll
        // that earns one is not something the user is watching the profile screen for.
        AnnounceAchievements(awards);

        RefreshProfiles();
    }

    // ---------------------------------------------------------------- what is on screen

    /// <summary>
    /// Rebuilds the profile list from what the launcher has learned, keeping the selection where it was.
    /// </summary>
    private void RefreshProfiles() {
        var wanted = SelectedProfile?.AccountId ?? SelectedAccount?.AccountId ?? _store.Settings.AutoLoginAccountId;

        // Only the accounts this server actually has, and what that leaves out is said rather than lost.
        UnlistedProfilesNotice = DescribeUnlistedProfiles(UnlistedProfiles(_profiles.Profiles, Accounts));

        // The same ordinary case as the badges: most looks learn nothing, and rebuilding the picker for
        // nothing is what drops its selection and flickers it under whoever is using it.
        var offered = OfferedProfiles(_profiles.Profiles, Accounts);

        if (!Profiles.SequenceEqual(offered)) {
            Profiles.Clear();

            foreach (var profile in offered) {
                Profiles.Add(profile);
            }
        }

        // Put back only when there is a reason for it: the profile already on screen, the account Play would
        // sign in as, or the account remembered from last time. Never simply the first in the list - a
        // profile outlives the server it was made on, and opening the launcher on whoever happens to sort
        // first is how a stranger's name ends up on the profile screen, and in the corner of the window, of a
        // launcher that has no accounts at all. With nothing remembered, the screen says so instead.
        var selected = wanted is { } id
            ? Profiles.FirstOrDefault(p => p.AccountId == id)
            : null;

        if (!ReferenceEquals(selected, SelectedProfile)) {
            SelectedProfile = selected;
        }

        RefreshProfileDerived();
    }

    /// <summary>
    /// The profiles the picker may offer: the ones the accounts this launcher knows about can sign in as.
    /// </summary>
    /// <remarks>
    /// An account belongs to a server, so a profile is only ever a profile <i>of</i> one of them. A profile
    /// remembered from a server this launcher is no longer pointed at - or one whose account the server no
    /// longer holds - cannot be signed in as, and offering it would be offering an account that does not
    /// exist here. It is kept rather than deleted, so the account turning up again brings its history back.
    /// </remarks>
    public static IReadOnlyList<LauncherProfile> OfferedProfiles(
        IReadOnlyList<LauncherProfile> remembered, IReadOnlyCollection<PlayerAccount> accounts) {
        var known = KnownAccountIds(accounts);

        return [.. remembered
            .Where(p => known.Contains(p.AccountId))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The ones left out, so they can be accounted for on the screen instead of vanishing.</summary>
    public static IReadOnlyList<LauncherProfile> UnlistedProfiles(
        IReadOnlyList<LauncherProfile> remembered, IReadOnlyCollection<PlayerAccount> accounts) {
        var known = KnownAccountIds(accounts);

        return [.. remembered.Where(p => !known.Contains(p.AccountId))];
    }

    /// <summary>How the screen says which profiles it is not offering, and why.</summary>
    public static string DescribeUnlistedProfiles(IReadOnlyList<LauncherProfile> unlisted) => unlisted.Count switch {
        0 => string.Empty,
        1 => $"“{unlisted[0].Name}” is remembered on this machine, but this server has no such account, "
             + "so it is not listed.",
        var count => $"{count} profiles are remembered on this machine, but this server has no such "
                     + "accounts, so they are not listed.",
    };

    private static HashSet<ulong> KnownAccountIds(IReadOnlyCollection<PlayerAccount> accounts)
        => [.. accounts.Select(a => a.AccountId)];

    /// <summary>
    /// Rebuilds everything derived from the selected profile: its badges, its pictures, and the sentences
    /// around them.
    /// </summary>
    private void RefreshProfileDerived() {
        var profile = SelectedProfile;

        // One read, two shapes of it: the flat list is what the sentences about badges are counted from,
        // and the groups are what the screen shows.
        var badges = profile is null
            ? []
            : ProfileBadge.For(profile, DebugLogging, ProfileDataProblem is null);

        // Nothing is rebuilt when nothing about the badges has changed, which is the ordinary case: the
        // database is looked at every ten seconds and almost every look learns nothing new. Replacing the
        // collections anyway is what folded the groups back up and threw the page to the top under somebody
        // who was reading them - a poll several seconds apart is not news.
        if (!Badges.SequenceEqual(badges)) {
            Badges.Clear();

            foreach (var badge in badges) {
                Badges.Add(badge);
            }
        }

        // And when something has changed, the groups that are already on screen are kept and refilled, so
        // the ones that are open stay open.
        ProfileBadgeGroup.Sync(
            BadgeGroups,
            profile is null ? [] : ProfileBadgeGroup.For(profile, badges, _ledger.ZoneWorlds));

        OnPropertyChanged(nameof(HasBadgeGroups));
        OnPropertyChanged(nameof(ProfileMultiplierText));

        LoadProfileImages(profile);

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(HasProfileAvatar));
        OnPropertyChanged(nameof(HasProfileBanner));
        OnPropertyChanged(nameof(HasProfileDataProblem));
        OnPropertyChanged(nameof(HasUnlistedProfiles));
        OnPropertyChanged(nameof(UnlistedProfilesText));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileAccountLine));
        OnPropertyChanged(nameof(StarterAccountSummary));
        OnPropertyChanged(nameof(StarterAccountTitle));
        OnPropertyChanged(nameof(StarterAccountUsername));
        OnPropertyChanged(nameof(StarterAccountInitials));
        OnPropertyChanged(nameof(HasStarterAccount));
        OnPropertyChanged(nameof(ProfileInitials));
        OnPropertyChanged(nameof(ProfilePlaytimeText));
        OnPropertyChanged(nameof(ProfileSessionsText));
        OnPropertyChanged(nameof(ProfileFirstPlayedText));
        OnPropertyChanged(nameof(ProfileLastPlayedText));
        OnPropertyChanged(nameof(ProfileStandingText));
        OnPropertyChanged(nameof(ProfileStandingDetail));
        OnPropertyChanged(nameof(ProfileIsNotClean));
        OnPropertyChanged(nameof(ProfileWizards));
        OnPropertyChanged(nameof(HasProfileWizards));
        OnPropertyChanged(nameof(ProfileWizardsSummary));
        OnPropertyChanged(nameof(ProfileBadgeCountText));
        OnPropertyChanged(nameof(ProfileScoreCircleText));
        OnPropertyChanged(nameof(HasBadges));
        OnPropertyChanged(nameof(DebugLogging));
        OnPropertyChanged(nameof(HasBadgesWaitingOnDebugLogging));
        OnPropertyChanged(nameof(ShowEnableDetailedLogging));
        OnPropertyChanged(nameof(HasProfileNotice));
        OnPropertyChanged(nameof(ProfileScopeNote));
        OnPropertyChanged(nameof(ProfileScore));
        OnPropertyChanged(nameof(ProfileScoreText));
        OnPropertyChanged(nameof(HighestScore));
        OnPropertyChanged(nameof(HighestScoreText));
        OnPropertyChanged(nameof(ProfileBossesDefeated));
        OnPropertyChanged(nameof(ProfileBossesText));

        OnPropertyChanged(nameof(CreateAccountSummary));
        CreateAccountCommand.NotifyCanExecuteChanged();
        ClearProfilePictureCommand.NotifyCanExecuteChanged();
        ClearProfileBannerCommand.NotifyCanExecuteChanged();
        EnableDetailedLoggingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Decodes a profile's picture and banner, once per pair of files.
    /// </summary>
    /// <remarks>
    /// The paths are the cache key, so a screen refresh that changes nothing does not read two image files
    /// off disk again - which matters because this is called from every derived-state refresh.
    /// </remarks>
    private void LoadProfileImages(LauncherProfile? profile) {
        var key = (profile?.AccountId ?? 0,
            ProfileStore.ImagePath(profile?.AccountId ?? 0, profile?.AvatarFile),
            ProfileStore.ImagePath(profile?.AccountId ?? 0, profile?.BannerFile));

        if (key == _profileImages) {
            return;
        }

        _profileImages = key;
        _ = DecodeProfileImagesAsync(key).ConfigureAwait(true);
    }

    private async Task DecodeProfileImagesAsync((ulong AccountId, string? Avatar, string? Banner) key) {
        var avatar = await DecodeAsync(key.Avatar).ConfigureAwait(true);
        var banner = await DecodeAsync(key.Banner).ConfigureAwait(true);

        // Only if nothing has been picked since the files were read.
        if (_profileImages == key) {
            ProfileAvatar = avatar;
            ProfileBanner = banner;
        }
    }

    /// <summary>
    /// Picking a profile is picking who the client is handed over as, so it drives Play as well.
    /// </summary>
    partial void OnSelectedProfileChanged(LauncherProfile? value) {
        var account = value is null
            ? null
            : Accounts.FirstOrDefault(a => a.AccountId == value.AccountId);

        if (!Equals(SelectedAccount, account)) {
            SelectedAccount = account;
        }

        RefreshProfileDerived();
    }

    /// <summary>Opens a picture picker and copies what it returns into the profile's own folder.</summary>
    [RelayCommand]
    private async Task ChooseProfilePictureAsync() {
        if (PickImageAsync is null || SelectedProfile is null) {
            return;
        }

        var picked = await PickImageAsync(false);
        if (picked is null) {
            return;
        }

        if (_profiles.AdoptImage(SelectedProfile, picked, banner: false) is null) {
            AppendLog(LogLevel.Error, $"Could not copy {picked} into the profile's folder.");
            return;
        }

        AppendLog(LogLevel.Info, $"Profile picture set from {picked}.");
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task ChooseProfileBannerAsync() {
        if (PickImageAsync is null || SelectedProfile is null) {
            return;
        }

        var picked = await PickImageAsync(true);
        if (picked is null) {
            return;
        }

        if (_profiles.AdoptImage(SelectedProfile, picked, banner: true) is null) {
            AppendLog(LogLevel.Error, $"Could not copy {picked} into the profile's folder.");
            return;
        }

        AppendLog(LogLevel.Info, $"Profile banner set from {picked}.");
        RefreshProfiles();
    }

    private bool CanClearProfilePicture() => SelectedProfile?.AvatarFile is not null;

    [RelayCommand(CanExecute = nameof(CanClearProfilePicture))]
    private void ClearProfilePicture() {
        if (SelectedProfile is null) {
            return;
        }

        _profiles.ClearImage(SelectedProfile, banner: false);
        RefreshProfiles();
    }

    private bool CanClearProfileBanner() => SelectedProfile?.BannerFile is not null;

    [RelayCommand(CanExecute = nameof(CanClearProfileBanner))]
    private void ClearProfileBanner() {
        if (SelectedProfile is null) {
            return;
        }

        _profiles.ClearImage(SelectedProfile, banner: true);
        RefreshProfiles();
    }

    /// <summary>
    /// Writes <c>Logging.LogLevel = DEBUG</c> into the server's config, which is what the badges about zones
    /// and duels need: Imlight writes those lines at debug level.
    /// </summary>
    /// <remarks>
    /// Offered as a button next to the badges that are waiting for it rather than switched on quietly: a
    /// debug log is a much bigger file, and it is the user's server config that changes.
    /// </remarks>
    private bool CanEnableDetailedLogging() => _config is not null && !DebugLogging;

    [RelayCommand(CanExecute = nameof(CanEnableDetailedLogging))]
    private void EnableDetailedLogging() {
        if (_config is null) {
            return;
        }

        _config.LogLevel = "DEBUG";
        _config.Save(ServerPath);
        SelectedLogLevel = "DEBUG";

        AppendLog(LogLevel.Success,
            $"Logging.LogLevel set to DEBUG in {ImlightConfig.RelativeConfigPath}. Restart the server and the "
            + "badges about where you went and what you won can be earned.");

        RefreshProfileDerived();
    }

    // ---------------------------------------------------------------- words

    /// <summary>Playtime in the largest unit that still says something useful.</summary>
    public static string DescribeDuration(long seconds) {
        if (seconds <= 0) {
            return "No time recorded yet";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalHours >= 1) {
            return $"{(int) span.TotalHours}h {span.Minutes}m";
        }

        return span.TotalMinutes >= 1 ? $"{span.Minutes}m" : $"{span.Seconds}s";
    }

    private static string DescribeFirstPlayed(LauncherProfile? profile)
        => profile?.FirstPlayedAt is { } first
            ? $"First signed in {first.ToLocalTime():yyyy-MM-dd}"
            : "Not signed in on this machine yet";

    /// <summary>One or two letters for a profile with no picture, so an empty circle is still a face.</summary>
    public static string InitialsOf(string name) {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length switch {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
        };
    }
}
