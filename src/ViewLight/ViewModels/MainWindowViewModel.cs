using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;
using ViewLight.Services;

namespace ViewLight.ViewModels;

public enum LauncherSection {
    Home,

    /// <summary>Who you are on this server: playtime, wizards, and what you have done on it.</summary>
    Profile,

    Settings,
    Logs,
    About,
}

/// <summary>
/// Which of the profile's two pages is on screen: the profile itself, or what it has earned.
/// </summary>
/// <remarks>
/// Achievements are a page of their own rather than a card under the profile, because the catalog is long -
/// seven categories of badges with a ladder card apiece - and reading it is a different thing from reading
/// the profile. Both are pages of Profile, and the score circle on the profile is the way in, so the
/// achievements page is reached from the profile rather than being a section of its own beside it.
/// </remarks>
public enum ProfilePage {

    /// <summary>The profile itself: whose it is, what they look like, and how they stand on this server.</summary>
    Profile,

    /// <summary>Everything the account has earned on this server, grouped by category.</summary>
    Achievements,
}

/// <summary>
/// Where Play has got to, in the launcher's own words rather than the server's.
/// </summary>
/// <remarks>
/// <see cref="Idle"/> is the state a launcher is in almost all of the time, and Home renders nothing at
/// all for it. A server that is simply not running yet is not news: the launcher starts it when Play is
/// pressed, so showing "stopped" would only invite the user to worry about something they are not
/// meant to have to manage. Every other value exists because something is happening, or because
/// something went wrong and has to be said out loud.
/// </remarks>
public enum LauncherPhase {

    /// <summary>Nothing to report. Home shows the game and the Play button, and nothing else.</summary>
    Idle,

    /// <summary>Imlight.Director has been started and is booting.</summary>
    StartingServer,

    /// <summary>The server is up but not yet accepting players - loading world data and opening ports.</summary>
    WarmingServer,

    /// <summary>The server is up and listening, with no game started against it yet.</summary>
    ServerReady,

    /// <summary>Wizard101 has been started and is on its way to the login screen.</summary>
    StartingClient,

    /// <summary>The game is running against the server this launcher started.</summary>
    Playing,

    /// <summary>Something went wrong, and the user has to know about it.</summary>
    Failed,
}

/// <summary>
/// Drives the whole launcher.
/// </summary>
/// <remarks>
/// A single view model owns every piece of state on purpose: the game folder, the server folder, the
/// parsed <c>Imlight.ini</c>, the release being installed and the running server all influence each
/// other, and splitting them up would mean a lot of cross-notification for a proof of concept.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject {

    /// <summary>The comment block Imlight's stock config carries above <c>DefaultItems</c>.</summary>
    private static readonly string[] StarterKitComments = [
        "Starter kit template IDs, comma-separated. Granted in the tutorial, or on first login when TutorialDisabled.",
        "Do not exceed 16 items: RavenDB has a batch limit of 16.",
        "Wand + deck only. Removed: ResistAll_Hat/Robe (QA/resist gear) 1317124-1317133,",
        "QA_SpeedBoots_100 1317234, Mount-Gryphon-Basic-001 284071.",
        "87256 = Wand-T1-016 (Tier1 wand), 126983 = Deck-Tutorial-001.",
    ];

    /// <summary>The kit the starter preset applies: a Tier 1 wand and the tutorial deck, nothing else.</summary>
    private const string StarterPresetItems = "87256,126983";

    private readonly LauncherSettingsStore _store;
    private readonly GithubReleaseService _releaseService;
    private readonly ServerInstallService _installService;
    private readonly BleedingEdgeServer _edgeService;
    private readonly RootWadItemService _itemService;
    private readonly ServerRunner _server = new();
    private readonly ClientSession _client;

    /// <summary>
    /// True while the constructor is filling in the saved state.
    /// </summary>
    /// <remarks>
    /// The setters for the paths do their own work when a person changes them - the game folder is read, the
    /// server's config is parsed and written back - and doing all of that here would do it twice, once for the
    /// assignment and once for the explicit start below. It is settled once instead, after the state is in.
    /// </remarks>
    private bool _startingUp = true;

    private ImlightConfig? _config;

    /// <summary>
    /// What the last read of the server's config found, kept so the requirement check does not read the
    /// same file a second time at every start.
    /// </summary>
    private ImlightConfigRead _configRead = new(null, false);

    private IReadOnlyList<ItemTemplate> _allCatalogItems = [];
    private ImlightRelease? _latestRelease;

    /// <summary>The last look at the source checkout, or <c>null</c> before one.</summary>
    private EdgeSourceState? _latestEdge;

    /// <summary>The check in flight, so a channel change replaces it rather than races it.</summary>
    /// <remarks>
    /// A check is a network round trip, and a person can change channels while one is running. Without
    /// this the older answer can land last and be shown as the newer channel's - a stable tag offered on
    /// the prerelease channel is the case that matters, because pressing the button would install it.
    /// </remarks>
    private CancellationTokenSource? _checkCancellation;

    private CancellationTokenSource? _installCancellation;

    private readonly PlayerDatabaseClient _playerDatabase = new();
    private PlayerDatabaseContents? _database;
    private CancellationTokenSource? _machineIdWatch;

    /// <summary>Whether the accounts on screen came from the saved list rather than a live read.</summary>
    private bool _accountsAreRemembered;

    private readonly LanServerDiscovery _discovery = new();
    private CancellationTokenSource? _scanCancellation;

    private readonly GameClientCopyService _clientCopy = new();
    private CancellationTokenSource? _copyGameCancellation;

    /// <summary>
    /// True while Play is waiting on the server. Readiness normally means "the game is starting next",
    /// which is only true when this is set - a server started by hand from Settings just becomes ready.
    /// </summary>
    private bool _playInFlight;

    public MainWindowViewModel() : this(new LauncherSettingsStore()) {
    }

    /// <param name="client">
    /// The client session to follow the game with. Passed in only so the state the launcher reports can
    /// be driven without a real Wizard101 install.
    /// </param>
    /// <param name="fetchArt">
    /// Whether to go and get the game's art. Off in a test, which is a launcher that is not having anything
    /// downloaded behind it: this is the one thing in the start-up path that reaches off this machine.
    /// </param>
    public MainWindowViewModel(LauncherSettingsStore store, ClientSession? client = null, bool fetchArt = true) {
        _store = store;
        _ledger = new ProfileLedger(_profiles);
        _client = client ?? new ClientSession();
        _releaseService = new GithubReleaseService();
        _installService = new ServerInstallService();
        _edgeService = new BleedingEdgeServer(_installService);
        _itemService = new RootWadItemService();

        _server.Output += (_, entry) => AppendLog(entry);
        _server.StateChanged += (_, state) => Post(() => {
            ServerState = state;
            SyncPhaseWithServer();
            RefreshDerived();
        });
        _server.Ready += (_, reason) => Post(() => {
            // The technical reason goes to the Log; the status rows say it in plain words.
            NoteAboutServer(LogLevel.Success, $"The server is ready — {reason}");
            SyncPhaseWithServer();
            RefreshDerived();

            // The player database only answers while the server is up, so this is the moment it can be
            // read: the accounts to sign in as, and the session key the server is holding.
            _ = RefreshAccountsAsync();
        });
        _server.Exited += (_, code) => Post(() => OnServerExited(code));
        _client.StateChanged += (_, state) => Post(() => OnClientStateChanged(state));

        // The patch server is the third process the launcher runs, and it goes through exactly the same
        // three channels: its output to the Log, its state to the rows on Home, and its exit to a report.
        _patch.Output += (_, entry) => AppendLog(entry);
        _patch.StateChanged += (_, state) => Post(() => {
            PatchServerState = state;
            RefreshDerived();

            // Watching the fetch hangs off the process rather than off Play: a patch server started from
            // Settings is fetching the same revision, and what it has fetched is worth the same line.
            SyncFetchWatch();

            // A patch server that has just come up has written the revision's own file list, which is what
            // a copy into it is planned against - so the card under Settings can say what there is to do
            // without the user having to change something to make it look.
            if (PatchServerIsRunning) {
                _ = RefreshPreloadPlanAsync();
            }
        });
        _patch.Ready += (_, reason) => Post(() => {
            NoteAboutPatchServer(LogLevel.Success, $"The patch server is ready — {reason}");
            RefreshDerived();
        });
        _patch.Exited += (_, code) => Post(() => OnPatchServerExited(code));

        // The window keeps no variant of its own, so this is the one place the saved theme takes hold.
        ApplyTheme();

        ClientPath = _store.Settings.ClientPath;
        ServerPath = _store.Settings.ServerPath;
        PatchServerPath = _store.Settings.PatchServerPath;

        // The channel is saved state like the folders, so it is filled in before the start is settled -
        // the setter's work is the checking and saving a person's change causes, and neither belongs on
        // the way into a window.
        ServerChannel = ServerChannelChoices.Parse(_store.Settings.ServerChannel);
        ShowMilestoneToasts = _store.Settings.ShowMilestoneToasts;
        // Where to land is a setting rather than a memory: a launcher left on Settings should still open
        // ready to play, so only an explicit "default page" moves it off Home.
        Section = ParseSection(_store.Settings.DefaultSection);

        // The start of the run, said once: reading the saved state is not an edit, so the setters above did
        // nothing by themselves. What they would have done is done here, in the order it belongs in.
        _startingUp = false;

        RefreshClientState();
        LoadServerConfig();

        // The starter-kit catalogue is not on the way to anything the window shows at first: it is the
        // Settings page's picker, and reading it opens the client's own archive. Kicked here so it arrives
        // while the window is already up.
        _ = LoadItemCatalogAsync();

        SeedAccountsFromCache();
        RefreshPatchFolderStatus();
        RefreshPatchInstalledText();

        // The install the patch server's files would be copied from, and what it is missing - asked in the
        // background, because answering it reads the revision's own file list. Written to the field rather
        // than the property: nothing has changed yet, so there is nothing to save or to ask again.
        _preloadSourcePath = _store.Settings.PatchServerPreloadSource;
        _ = RefreshPreloadPlanAsync();

        // And, when the revisions are looked after automatically, the tidy-up that setting is about. It only
        // touches a revision nothing asks for and nothing is writing into, so the usual answer on the usual
        // machine is that there is nothing to do - which is why it is safe for it to be here rather than
        // behind a button.
        if (CleansUpRevisionsAutomatically) {
            _ = LookAfterPatchServerRevisionsAsync();
        }

        // Profiles come from the accounts this launcher already knows; what each of them has done is
        // collected while its server runs. Only this server's accounts count: a name remembered from
        // another server is not evidence that anyone here has ever played.
        _ledger.NoteAccounts(RememberedAccounts);
        RefreshProfiles();

        if (IsPortable) {
            // A portable launcher starts with no game in its own folder, so where to copy one from is the
            // first thing it needs - and the install is usually findable without asking. A launcher that has
            // copied one in before remembers where from, and that remembered install is the one the copy is
            // compared against as well: it is where an update would come from.
            var copiedFrom = _store.Settings.PortableGameSourcePath;

            PortableGameSource = !string.IsNullOrWhiteSpace(copiedFrom)
                ? copiedFrom
                : GameFolderLocator.Detect()?.Path ?? string.Empty;
        }

        RefreshDerived();

        // Where the launcher keeps its own files is a real question when something looks forgotten, so it
        // is answered here rather than only in a document.
        AppendLog(LogLevel.Info, $"ViewLight keeps its own settings in {_store.FilePath}");

        if (_store.LoadProblem is { } settingsProblem) {
            // Said rather than swallowed: this is how a launcher comes up having "forgotten" the folders it
            // was pointed at, and the reason is the only thing that makes that fixable.
            AppendLog(LogLevel.Warning,
                $"ViewLight's own settings could not be read, so the defaults are in use. {settingsProblem}");
        }

        if (RequiresSetup) {
            AppendLog(LogLevel.Info, "Welcome. Choose your game folder and a server folder to continue.");
        }
        else {
            AppendLog(LogLevel.Info, "ViewLight ready.");

            // Said into the Log as well as onto the screen: what the launcher is waiting for is the thing
            // somebody will come to the Log to find out, even though the Log is behind this screen.
            if (RequiresImlight) {
                AppendLog(LogLevel.Warning, _imlightRequirement.Missing);
            }

            if (_store.Settings.CheckForUpdatesOnStartup) {
                _ = CheckForUpdatesAsync();

                // The patch server is checked too, so its card can say whether there is a newer build -
                // and so Play does not have to reach GitHub while the user is waiting to play.
                if (PatchServerInstalled) {
                    _ = CheckForPatchServerUpdatesAsync();
                }
            }
        }

        // Cached art is instant; a first run fetches it in the background while Home is usable.
        if (fetchArt) {
            _ = LoadGameArtAsync();
        }

        // And so is the player database: the server's own RavenDB is started while the launcher is idle, so
        // the accounts and profiles are already there instead of arriving after a warm-up.
        _ = EnsureIdleDatabaseAsync();

        // And the launcher's own database, which is the other way round: not borrowed while Imlight is
        // idle, but the launcher's for the whole run. It is started here rather than on first use so a
        // profile is never read from a database that has to be started first.
        _ = EnsureLauncherDatabaseAsync();
    }

    /// <summary>Set by the window so the view model can open a folder picker without knowing about views.</summary>
    public Func<string?, Task<string?>>? PickFolderAsync { get; set; }

    // ---------------------------------------------------------------- first run

    /// <summary>
    /// Both locations have to be known before Home makes any sense, so the launcher asks for them
    /// instead of showing a home screen that cannot do anything.
    /// </summary>
    public bool RequiresSetup
        => string.IsNullOrWhiteSpace(ClientPath) || string.IsNullOrWhiteSpace(ServerPath);

    /// <summary>
    /// The launcher itself, which nothing gets to see until all three first screens are satisfied: where
    /// the folders are, that there is an Imlight in the server folder to work with, and whose patch server
    /// a session's files will come from.
    /// </summary>
    public bool ShowMainInterface => !RequiresSetup && !RequiresImlight && !RequiresPatchServerChoice;

    public bool CanContinueSetup => !RequiresSetup;

    [RelayCommand(CanExecute = nameof(CanContinueSetup))]
    private void CompleteSetup() {
        AppendLog(LogLevel.Success, $"Game folder: {ClientPath}");
        AppendLog(LogLevel.Success, $"Server folder: {ServerPath}");
        Section = LauncherSection.Home;

        if (_store.Settings.CheckForUpdatesOnStartup) {
            _ = CheckForUpdatesAsync();

            if (PatchServerInstalled) {
                _ = CheckForPatchServerUpdatesAsync();
            }
        }
    }

    /// <summary>Fills the game folder from the Steam registry or a conventional install location.</summary>
    [RelayCommand]
    private void DetectGameFolder() {
        var detected = GameFolderLocator.Detect();
        if (detected is null) {
            AppendLog(LogLevel.Warning,
                "No Wizard101 install was found automatically. Use Browse… to point at the game folder.");
            return;
        }

        ClientPath = detected.Path;
        AppendLog(LogLevel.Success, $"Found the game folder via {detected.Source}: {detected.Path}");
    }

    [RelayCommand]
    private void UseRecommendedServerFolder() {
        ServerPath = LauncherPaths.DefaultServerPath;
        AppendLog(LogLevel.Info, $"Server folder set to {ServerPath}");
    }

    // ---------------------------------------------------------------- navigation

    [ObservableProperty] private LauncherSection _section = LauncherSection.Home;

    public bool IsHomeSelected => Section == LauncherSection.Home;
    public bool IsProfileSelected => Section == LauncherSection.Profile;
    public bool IsSettingsSelected => Section == LauncherSection.Settings;
    public bool IsLogsSelected => Section == LauncherSection.Logs;
    public bool IsAboutSelected => Section == LauncherSection.About;

    public string SectionTitle => TitleFor(Section, ProfilePage);

    public string SectionSubtitle => SubtitleFor(Section, ProfilePage);

    /// <summary>The heading over the page on screen, which is the profile's own when it is showing its second.</summary>
    public static string TitleFor(LauncherSection section, ProfilePage page) => section switch {
        LauncherSection.Home => "Home",
        LauncherSection.Profile => page == ProfilePage.Achievements ? "Achievements" : "Profile",
        LauncherSection.Settings => "Settings",
        LauncherSection.Logs => "Log",
        LauncherSection.About => "About",
        _ => "ViewLight",
    };

    /// <summary>The line under it, saying what the page is for.</summary>
    public static string SubtitleFor(LauncherSection section, ProfilePage page) => section switch {
        LauncherSection.Home => "Press Play and the rest takes care of itself.",
        LauncherSection.Profile => page == ProfilePage.Achievements
            ? "Everything this account has earned on this server, and what the rest of it would take."
            : "Who you are on this server: how long you have played, and what you have done.",
        LauncherSection.Settings => "Your folders, your server and your data, in four pages - and every value "
                                    + "ViewLight writes into Config/Imlight.ini.",
        LauncherSection.Logs => "Everything the launcher, the server and the patch server have reported this session.",
        LauncherSection.About => "What ViewLight is, and where it is up to.",
        _ => string.Empty,
    };

    /// <summary>
    /// Opens a section, which always opens on its first page.
    /// </summary>
    /// <remarks>
    /// The achievements screen belongs to Profile and is asked for from it rather than landed on, so
    /// arriving anywhere - Profile included, when the launcher has been elsewhere - starts on the profile.
    /// A page is somewhere you are rather than a preference, which is the same rule that keeps the launcher
    /// from reopening on the page it was last left on.
    /// </remarks>
    private void GoTo(LauncherSection section) {
        // Set as well as in OnSectionChanged, because that one only runs when the section actually changes:
        // clicking Profile while the achievements screen is open is a section that stays put, and it is
        // still a request for the account.
        ProfilePage = ProfilePage.Profile;
        Section = section;
    }

    partial void OnSectionChanged(LauncherSection value) {
        // Nothing is written here on purpose: moving between pages is not a preference, and remembering
        // it is what made the launcher reopen on the last page instead of on Home.
        ProfilePage = ProfilePage.Profile;

        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));
        RefreshDerived();
    }

    [RelayCommand]
    private void Navigate(string? section) {
        if (!Enum.TryParse<LauncherSection>(section, ignoreCase: true, out var parsed)) {
            return;
        }

        // Clicking the profile card is one of the ways here, and it has been clicked for - so it folds back
        // to the picture and the name rather than hanging open over the page it just opened.
        CloseProfileCard();

        GoTo(parsed);

        // Coming back to Home is the natural moment to retry art that a first, offline run could not get.
        if (parsed == LauncherSection.Home && !HasHeroArt && !HasLogoArt) {
            _ = LoadGameArtAsync();
        }
    }

    // ---------------------------------------------------------------- log

    /// <summary>
    /// The Log itself: the lines it is holding, which of the three sources are being shown, and which runs
    /// of the same line arriving again are folded up. The view draws <see cref="LogDocument.Blocks"/>.
    /// </summary>
    public LogDocument Log { get; } = new();

    [ObservableProperty] private bool _autoScrollLogs = true;

    /// <summary>What the line under the Log says: how much is here, folded, and filtered out.</summary>
    [ObservableProperty] private string _logSummary = string.Empty;

    // The three shown-or-not boxes. All three are on to begin with: the point of the Log is that everything
    // is in one place, and the boxes are for quietening one of the three down rather than for hiding two.
    [ObservableProperty] private bool _showLauncherLog = true;
    [ObservableProperty] private bool _showServerLog = true;
    [ObservableProperty] private bool _showPatchServerLog = true;

    /// <summary>
    /// Whether the launcher's own lines about the two programs are shown.
    /// </summary>
    /// <remarks>
    /// On to begin with, because they are part of the running account the Log is: a patch server does not
    /// log which file it is on, and the launcher saying so is the only way to watch a fetch. Turning it off
    /// is for the other use the Log has - pasting one program's lines to the people who wrote it, without
    /// the launcher's notes on them arriving too.
    /// </remarks>
    [ObservableProperty] private bool _showLauncherNotes = true;

    partial void OnShowLauncherLogChanged(bool value) => ApplyLogFilters();
    partial void OnShowServerLogChanged(bool value) => ApplyLogFilters();
    partial void OnShowPatchServerLogChanged(bool value) => ApplyLogFilters();
    partial void OnShowLauncherNotesChanged(bool value) => ApplyLogFilters();

    private void ApplyLogFilters() {
        Log.SetVisible(LogSource.Launcher, ShowLauncherLog);
        Log.SetVisible(LogSource.Server, ShowServerLog);
        Log.SetVisible(LogSource.Patcher, ShowPatchServerLog);
        Log.SetDerivedVisible(ShowLauncherNotes);
        RefreshLogSummary();
    }

    /// <summary>
    /// One of the launcher's own lines about a program, filed under that program.
    /// </summary>
    /// <remarks>
    /// The launcher's commentary goes where the program's own output goes, so a box for one of the three is
    /// about one thing rather than about who typed the sentence - and so the notes can be hidden on their
    /// own, which is the one difference that matters when a program's lines are being handed to whoever
    /// maintains it.
    /// </remarks>
    public void AppendDerivedLog(LogLevel level, string message, LogSource source)
        => AppendLog(new LogEntry(DateTimeOffset.Now, level, message, source, Derived: true));

    /// <summary>The launcher's own note about the patch server, filed with the patch server's lines.</summary>
    private void NoteAboutPatchServer(LogLevel level, string message)
        => AppendDerivedLog(level, message, LogSource.Patcher);

    /// <summary>The launcher's own note about the server, filed with the server's lines.</summary>
    private void NoteAboutServer(LogLevel level, string message)
        => AppendDerivedLog(level, message, LogSource.Server);

    /// <summary>Opens a folded run of the same line so it can be read and copied, or folds it back up.</summary>
    public void ToggleLogRepeat(LogBlock block) {
        Log.Toggle(block);
        RefreshLogSummary();
    }

    public void AppendLog(LogLevel level, string message)
        => AppendLog(new LogEntry(DateTimeOffset.Now, level, message));

    public void AppendLog(LogEntry entry) {
        if (Dispatcher.UIThread.CheckAccess()) {
            AddLogEntry(entry);
        }
        else {
            Dispatcher.UIThread.Post(() => AddLogEntry(entry));
        }
    }

    private void AddLogEntry(LogEntry entry) {
        Log.Append(entry);
        RefreshLogSummary();
    }

    private void RefreshLogSummary() => LogSummary = Log.Summary;

    [RelayCommand]
    private void ClearLogs() {
        Log.Clear();
        RefreshLogSummary();
    }

    private static void Post(Action action) {
        if (Dispatcher.UIThread.CheckAccess()) {
            action();
        }
        else {
            Dispatcher.UIThread.Post(action);
        }
    }

    // ---------------------------------------------------------------- locations

    [ObservableProperty] private string _clientPath = string.Empty;
    [ObservableProperty] private string _serverPath = string.Empty;
    [ObservableProperty] private string _gameFolderStatus = string.Empty;
    [ObservableProperty] private string _serverFolderStatus = string.Empty;
    [ObservableProperty] private string _rootWadStatus = string.Empty;

    partial void OnClientPathChanged(string value) {
        if (_startingUp) {
            return;
        }

        _store.Settings.ClientPath = value;
        _store.Save();
        RefreshClientState();
        _ = LoadItemCatalogAsync();
    }

    partial void OnServerPathChanged(string value) {
        if (_startingUp) {
            return;
        }

        _store.Settings.ServerPath = value;
        _store.Save();
        LoadServerConfig();
    }

    [RelayCommand]
    private async Task BrowseClientFolderAsync() {
        if (PickFolderAsync is null) {
            return;
        }

        var picked = await PickFolderAsync(ClientPath);
        if (!string.IsNullOrWhiteSpace(picked)) {
            ClientPath = picked;
            AppendLog(LogLevel.Info, $"Game folder set to {picked}");
        }
    }

    [RelayCommand]
    private async Task BrowseServerFolderAsync() {
        if (PickFolderAsync is null) {
            return;
        }

        var picked = await PickFolderAsync(ServerPath);
        if (!string.IsNullOrWhiteSpace(picked)) {
            ServerPath = picked;
            AppendLog(LogLevel.Info, $"Server folder set to {picked}");
        }
    }

    /// <summary>
    /// <c>&lt;gameFolder&gt;\bin\WizardGraphicalClient.exe</c> - everything is resolved relative to the
    /// game folder the user picked.
    /// </summary>
    public string? ClientExecutablePath => ClientLauncher.FindExecutable(ClientPath);

    private void RefreshClientState() {
        var executable = ClientExecutablePath;
        var rootWad = RootWadItemService.FindRootWad(ClientPath);

        // Read rather than remembered: the client states its own revision, and it changes the moment the
        // game is patched.
        ClientBuild = ClientBuildReader.Read(ClientPath);

        // The game's own name tables live in this folder's archive, so this is where a wizard's stored name
        // key can start being turned into a name. Nothing is read here - the table opens the archive the
        // first time a character actually needs a name.
        _ledger.UseNameTable(WizardNameTable.For(ClientPath));

        RootWadStatus = rootWad is null
            ? $"{GameFolderLocator.RelativeRootWad}: not found"
            : $"Found {Path.GetRelativePath(SafeDirectory(ClientPath), rootWad)}";

        GameFolderStatus = (executable, rootWad) switch {
            (null, null) => $"Neither {GameFolderLocator.RelativeClientExecutable} nor "
                            + $"{GameFolderLocator.RelativeRootWad} was found here.",
            (not null, null) => $"Found the game client, but {GameFolderLocator.RelativeRootWad} is missing.",
            (null, not null) => $"Found {GameFolderLocator.RelativeRootWad}, but "
                                + $"{GameFolderLocator.RelativeClientExecutable} is missing.",
            _ => $"Ready — {GameFolderLocator.RelativeClientExecutable} and "
                 + $"{GameFolderLocator.RelativeRootWad} both found.",
        };

        // A portable launcher plays the game out of its own folder, so whether that copy is still the build
        // this machine has is a question about the folder that just changed - see MainWindowViewModel.ClientUpdate.
        RefreshPortableClientUpdate();

        // Everything derived from the executable path is rebuilt here, so the screens that read it - the
        // portable prompt included - change the moment the folder does.
        RefreshDerived();
    }

    private static string SafeDirectory(string path)
        => string.IsNullOrWhiteSpace(path) ? "." : path;

    // ---------------------------------------------------------------- portable mode

    /// <summary>Whether this run keeps everything in the launcher's own folder.</summary>
    public bool IsPortable => PortableMode.IsOn;

    /// <summary>The folder everything lives in when portable, or the profile it normally uses.</summary>
    public string LauncherRoot => LauncherPaths.DataRoot;

    /// <summary>Where portable mode keeps the game, and where Play looks for it.</summary>
    public string PortableGameFolder => LauncherPaths.GameDirectory;

    /// <summary>
    /// True when portable mode is on but the game has not been copied in yet - the state where the
    /// launcher asks where the client is instead of assuming it is there.
    /// </summary>
    public bool PortableNeedsClient => IsPortable && ClientExecutablePath is null;

    /// <summary>The install the game would be copied from.</summary>
    [ObservableProperty] private string _portableGameSource = string.Empty;

    [ObservableProperty] private bool _isCopyingGame;
    [ObservableProperty] private double _copyGameProgress;
    [ObservableProperty] private string _copyGameStage = "Idle";
    [ObservableProperty] private string _copyGameMessage = string.Empty;

    public bool HasCopyGameMessage => !string.IsNullOrWhiteSpace(CopyGameMessage);

    /// <summary>
    /// Whether the card has anything to ask: nothing in the folder to play from, or a copy of one being
    /// made right now.
    /// </summary>
    public bool ShowPortableClientPrompt => PortableNeedsClient || IsCopyingGame;

    /// <summary>The card on Home appears only while it has something to do or say.</summary>
    public bool ShowPortableClientCard => IsPortable && (ShowPortableClientPrompt || HasCopyGameMessage);

    /// <summary>
    /// What the card is called: the question while there is one, and what happened once there is not.
    /// </summary>
    /// <remarks>
    /// It changes because the card outlives the work it describes. A finished copy leaves a game in the
    /// folder and one line saying so, and a card still headed <i>copy your game into this folder</i> would
    /// be offering to do the thing it has just done - which reads as a launcher that did not notice its own
    /// work. A copy that has fallen behind the machine's install is a different card rather than this one:
    /// that is a game which is here and wants replacing, not one that is missing.
    /// </remarks>
    public string PortableClientCardTitle => ShowPortableClientPrompt
        ? "Copy your game into this folder"
        : "Your game is already in this folder";

    /// <summary>
    /// Whether the chosen folder is a Steam copy - one that may need Steam, at KingsIsle's discretion, and
    /// that comes with the game's own cached patch files either way.
    /// </summary>
    public bool HasPortableSteamWarning => GameFolderLocator.IsSteamInstall(PortableGameSource);

    public string PortableSteamWarning
        => HasPortableSteamWarning ? GameClientCopyService.SteamCaveat : string.Empty;

    partial void OnPortableGameSourceChanged(string value) {
        CopyGameMessage = string.Empty;
        RefreshDerived();
    }

    partial void OnIsCopyingGameChanged(bool value) => RefreshDerived();

    partial void OnCopyGameMessageChanged(string value) => RefreshDerived();

    /// <summary>Whether the marker is on disk - which is what the next start will run as.</summary>
    public bool PortableMarkerOn => PortableMode.MarkerExists();

    /// <summary>A mode change only takes effect on the next start, so a restart is always owed.</summary>
    public bool PortableNeedsRestart => PortableMarkerOn != IsPortable;

    public string PortableRunText => IsPortable
        ? "Portable — settings, server and game all live in this folder"
        : "Normal — settings live in your user profile";

    public string PortableToggleLabel => PortableMarkerOn
        ? "Turn portable mode off (next restart)"
        : "Turn portable mode on (next restart)";

    public string PortableRestartNote => PortableMarkerOn
        ? $"The next time ViewLight starts it will keep everything in {PortableMode.ExecutableDirectory}."
        : $"The next time ViewLight starts it will keep its settings in {LauncherPaths.NormalDataRoot} again.";

    /// <summary>
    /// Writes or removes the marker beside the executable.
    /// </summary>
    /// <remarks>
    /// Deliberately not a switch that takes effect immediately: where the settings are is settled when the
    /// launcher starts, so the change is owed a restart and the UI says so rather than half-applying it.
    /// </remarks>
    [RelayCommand]
    private void TogglePortableMode() {
        var wanted = !PortableMarkerOn;

        if (!PortableMode.SetMarker(wanted)) {
            AppendLog(LogLevel.Error,
                $"Could not {(wanted ? "create" : "remove")} {PortableMode.MarkerPath}. The folder may be "
                + "read-only - a launcher installed under Program Files has to be moved somewhere writable "
                + "before it can be portable.");
            return;
        }

        AppendLog(LogLevel.Success, wanted
            ? $"Portable mode turned on. Restart ViewLight and it will keep everything in {PortableMode.ExecutableDirectory}."
            : $"Portable mode turned off. Restart ViewLight and it will keep its settings in {LauncherPaths.NormalDataRoot} again.");

        RefreshDerived();
    }

    /// <summary>Fills the copy source from the usual places, so it rarely has to be typed.</summary>
    [RelayCommand]
    private void DetectPortableGameSource() {
        var detected = GameFolderLocator.Detect();
        if (detected is null) {
            AppendLog(LogLevel.Warning,
                "No Wizard101 install was found automatically. Use Browse… to point at the game folder.");
            return;
        }

        PortableGameSource = detected.Path;
        AppendLog(LogLevel.Success, $"Found a game folder via {detected.Source}: {detected.Path}");
    }

    [RelayCommand]
    private async Task BrowsePortableGameSourceAsync() {
        if (PickFolderAsync is null) {
            return;
        }

        var picked = await PickFolderAsync(PortableGameSource);
        if (!string.IsNullOrWhiteSpace(picked)) {
            PortableGameSource = picked;
        }
    }

    private bool CanCopyGame()
        => IsPortable && !IsCopyingGame && !string.IsNullOrWhiteSpace(PortableGameSource);

    /// <summary>
    /// Copies the chosen install into the launcher's own folder and points Play at the copy.
    /// </summary>
    /// <remarks>
    /// The copy is a real one - several gigabytes - so it reports where it is and can be cancelled, and
    /// the source install is left untouched so a failure costs nothing but the time.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCopyGame))]
    private async Task CopyGameIntoPortableAsync() {
        var source = PortableGameSource;
        if (string.IsNullOrWhiteSpace(source)) {
            return;
        }

        var destination = LauncherPaths.GameDirectory;

        IsCopyingGame = true;
        CopyGameMessage = string.Empty;
        _copyGameCancellation = new CancellationTokenSource();

        var progress = new Progress<ClientCopyProgress>(p => {
            CopyGameProgress = p.Percent;
            CopyGameStage = $"{p.Stage} · {p.Detail}";
        });

        AppendLog(LogLevel.Info, $"Copying the game from {source} into {destination}…");

        try {
            var result = await _clientCopy
                .CopyAsync(source, destination, progress, _copyGameCancellation.Token)
                .ConfigureAwait(true);

            CopyGameMessage = result.Warning is { Length: > 0 } ? result.Warning : result.Message;

            if (result.Success) {
                // Where it came from is written down, because that install is what the copy is compared
                // against from now on - and it is the one an update would come from.
                _store.Settings.PortableGameSourcePath = GameFolderLocator.NormaliseGameFolder(source);
                _store.Save();

                // Play points at the copy, because a launcher's own folder being the source of everything
                // is the whole point of the mode. Stored relative in portable mode, so a move keeps it.
                ClientPath = result.Destination;

                // Read again rather than left to that assignment. In portable mode the launcher is already
                // pointed at its own game folder, so writing the same path back is no change at all and
                // nothing re-reads the folder - which is how a finished copy went on being reported as a
                // missing game, in Settings and on Home alike, until the folder was set by hand.
                RefreshClientState();

                AppendLog(LogLevel.Success, result.Message);
            }
            else {
                AppendLog(LogLevel.Error, result.Message);
            }

            if (result.Warning is { Length: > 0 }) {
                AppendLog(LogLevel.Warning, result.Warning);
            }
        }
        catch (Exception ex) {
            CopyGameMessage = $"Copying the game failed: {ex.Message}";
            AppendLog(LogLevel.Error, CopyGameMessage);
        }
        finally {
            _copyGameCancellation?.Dispose();
            _copyGameCancellation = null;
            IsCopyingGame = false;
            CopyGameStage = "Idle";
            CopyGameProgress = 0;
            RefreshDerived();
        }
    }

    [RelayCommand]
    private void CancelCopyGame() => _copyGameCancellation?.Cancel();

    // ---------------------------------------------------------------- server config

    [ObservableProperty] private bool _serverInstalled;
    [ObservableProperty] private string _gameRevision = string.Empty;
    [ObservableProperty] private bool _enforceRevision;

    [ObservableProperty] private string _adminUsername = string.Empty;
    [ObservableProperty] private string _adminPassword = string.Empty;

    /// <summary>
    /// Told once when the launcher finds the admin account on another password and puts the config's on it,
    /// so a password that has been changed under a running server is a thing the user is told rather than
    /// something they discover at the game's login screen.
    /// </summary>
    [ObservableProperty] private string _adminPasswordNotice = string.Empty;

    public bool HasAdminPasswordNotice => AdminPasswordNotice.Length > 0;

    partial void OnAdminPasswordNoticeChanged(string value)
        => OnPropertyChanged(nameof(HasAdminPasswordNotice));
    [ObservableProperty] private bool _tutorialDisabled;
    [ObservableProperty] private string _defaultItemsText = string.Empty;
    [ObservableProperty] private int _startingLevel = 1;
    [ObservableProperty] private string _startingZone = string.Empty;
    [ObservableProperty] private int _baseGoldPouch = 1_000_000;
    [ObservableProperty] private string _gameServerIp = "127.0.0.1";
    [ObservableProperty] private int _loginServerPort = 12000;
    [ObservableProperty] private int _gameServerPort = 12333;
    [ObservableProperty] private int _gameServerCount = 3;
    [ObservableProperty] private string _realmNames = string.Empty;
    [ObservableProperty] private string _patchServerUrl = string.Empty;
    [ObservableProperty] private bool _spiralDbAutoFetch = true;
    [ObservableProperty] private string _selectedLogLevel = "DEBUG";
    [ObservableProperty] private int _embeddedDatabasePort = ImlightConfig.DefaultEmbeddedDatabasePort;

    // ---------------------------------------------------------------- accounts and automatic sign-in

    /// <summary>The accounts in the player database, as they were last read.</summary>
    public ObservableCollection<PlayerAccount> Accounts { get; } = [];

    /// <summary>The account Play signs in as. Only ever the account - characters stay the game's.</summary>
    [ObservableProperty] private PlayerAccount? _selectedAccount;

    /// <summary>
    /// A picture picker, set by the window so the view model can ask for an image without knowing about
    /// views. The flag says which slot it is for, so the dialog can be titled for it.
    /// </summary>
    public Func<bool, Task<string?>>? PickImageAsync { get; set; }

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>
    /// True while the picker is showing what the last read found rather than a live one, which is only
    /// ever the case between the launcher opening and the server first becoming ready.
    /// </summary>
    public bool AccountsAreRemembered => _accountsAreRemembered && Accounts.Count > 0;

    /// <summary>
    /// What Play will do about signing in, in plain words - including when it will not, and why. A
    /// launcher that silently gives up on the login screen would be worse than one that never had it.
    /// </summary>
    public string AutoLoginSummary {
        get {
            // Signing in needs a session key the server issued, and only this launcher's own server
            // ever does. Said plainly rather than leaving the account picker looking broken.
            if (IsRemoteSession) {
                return IsOfficialServer
                    ? "This is Wizard101's own server, so Wizard101 will ask for your Wizard101 account - "
                      + "ViewLight has nothing to sign you in with there."
                    : "Automatic sign-in only works on the server this launcher runs, so Wizard101 will ask "
                      + "you to sign in here.";
            }

            if (_database is null) {
                // Nothing has been read yet this session. With a remembered list the picker is usable and
                // the sign-in is simply not promised until the server can confirm it.
                if (Accounts.Count == 0) {
                    return "Accounts appear here once the server is running — the player database only answers then.";
                }

                return SelectedAccount is null
                    ? "These accounts are remembered from the last time the server ran. Play confirms them once it is up."
                    : $"Remembered from the last time the server ran. Play confirms them and signs in as "
                      + $"{SelectedAccount.Username} once the server is up.";
            }

            if (!_database.IsTrustworthy) {
                return _database.Problem!;
            }

            if (Accounts.Count == 0) {
                return "There are no accounts in the player database yet.";
            }

            if (SelectedAccount is null) {
                return "Pick an account to sign in as, without Wizard101's login screen.";
            }

            var key = _database.SessionKeyFor(SelectedAccount.AccountId);
            if (key is null) {
                return $"Wizard101 will ask you to sign in as {SelectedAccount.Username} once; Play signs you "
                       + "in from then on.";
            }

            var machineId = _store.Settings.MachineId;
            if (machineId is not null && key.MachineId != machineId.Value) {
                return $"The stored session key is another machine's, so Wizard101 will ask you to sign in as "
                       + $"{SelectedAccount.Username}.";
            }

            if (key.Expires is { } expiry && expiry <= DateTimeOffset.UtcNow) {
                return $"The stored session key has expired, so Wizard101 will ask you to sign in as "
                       + $"{SelectedAccount.Username} once.";
            }

            return $"Play signs in as {SelectedAccount.Username} — no login screen.";
        }
    }

    /// <summary>
    /// Remembers the account the player picked. Only the id is kept: that is what the client is signed
    /// in with, and a username is only ever presented.
    /// </summary>
    partial void OnSelectedAccountChanged(PlayerAccount? value) {
        // Signing out is not forgetting. The account this machine signs in as is kept when the selection is
        // cleared, so the sign-in screen has somebody to fall back to and the next start still knows who was
        // playing here; only picking an account changes it.
        if (value is not null) {
            _store.Settings.AutoLoginAccountId = value.AccountId;
            _store.Save();
        }

        OnPropertyChanged(nameof(AutoLoginSummary));

        // The account and the profile are two views of the same thing - the account is the server's half,
        // the profile is this machine's - so picking one picks the other.
        var profile = Profiles.FirstOrDefault(p => p.AccountId == value?.AccountId);
        if (!ReferenceEquals(profile, SelectedProfile)) {
            SelectedProfile = profile;
        }
    }

    public string[] LogLevels { get; } = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];

    private void LoadServerConfig() {
        var read = ImlightConfig.Read(ServerPath);

        _configRead = read;
        _config = read.Config;
        ServerInstalled = _config is not null;

        // Whether the launcher can work at all is a question about the folder and this machine rather than
        // about the config, so it is asked here - on every path, including the one with no config at all.
        RefreshImlightRequirement();

        if (_config is null) {
            // Three answers rather than two, for the same reason the requirement screen has three: a config
            // that is there and would not read is not a server that is not installed, and saying so was a
            // report of its own. The folder being looked in is named, because a path that resolves somewhere
            // else than the user thinks is the other way this looks like a missing install.
            ServerFolderStatus = read.FileIsThere
                ? $"{ImlightConfig.ConfigPathFor(ServerPath)} is there, but it could not be read just now - "
                  + "something else has it open for the moment. Press Reload settings in a moment."
                : Directory.Exists(ServerPath)
                    ? $"No server installed here yet ({ImlightConfig.ConfigPathFor(ServerPath)} not found)."
                    : "Folder does not exist yet — it is created when the server is downloaded.";
            SyncStarterKitFromIds([]);
            RefreshDerived();
            return;
        }

        AdminUsername = _config.AdminUsername;
        AdminPassword = _config.AdminPassword;
        TutorialDisabled = _config.TutorialDisabled;
        DefaultItemsText = _config.DefaultItems;
        StartingLevel = _config.StartingLevel;
        StartingZone = _config.StartingZone;
        BaseGoldPouch = _config.BaseGoldPouch;
        GameServerIp = _config.GameServerIp;
        LoginServerPort = _config.LoginServerPort;
        GameServerPort = _config.GameServerPort;
        GameServerCount = _config.GameServerCount;
        RealmNames = _config.RealmNames;
        PatchServerUrl = _config.PatchServerUrl;
        SpiralDbAutoFetch = _config.SpiralDbAutoFetch;
        SelectedLogLevel = _config.LogLevel;
        GameRevision = _config.GameRevision;
        EnforceRevision = _config.EnforceRevision;
        EmbeddedDatabasePort = _config.EmbeddedDatabasePort;

        // Imlight's own patch listener, which is a different thing from the patch server it downloads
        // from - and the port the launcher's patch server must not take.
        ImlightPatchServerPort = _config.PatchServerPort;

        ServerFolderStatus = File.Exists(ImlightConfig.ExecutablePathFor(ServerPath))
            ? $"Server installed — {ImlightConfig.RelativeConfigPath} found."
            : $"{ImlightConfig.RelativeConfigPath} found, but {ImlightConfig.ServerExecutableName} is missing.";

        // The level ladders are generated up to the cap this server actually has, so they are rebuilt here
        // - before anything reads them - rather than assumed to be the stock 200.
        AchievementCatalog.UseMaxLevel(_config.MaxLevel);

        SyncStarterKitFromIds(_config.DefaultItemsIds);
        RefreshDerived();
    }

    [RelayCommand]
    private void ReloadSettings() {
        LoadServerConfig();
        AppendLog(LogLevel.Info, "Reloaded settings from Imlight.ini.");
    }

    /// <summary>Writes every field on the Settings tab back into <c>Imlight.ini</c>.</summary>
    [RelayCommand]
    private void SaveSettings() {
        if (_config is null) {
            AppendLog(LogLevel.Warning, "No server is installed yet, so there is no Imlight.ini to write.");
            return;
        }

        // The starter kit is validated first: writing a kit the client cannot tolerate would brick
        // every new character, so nothing else is saved either until it is fixed.
        var ids = ItemList.Parse(DefaultItemsText);
        var kitError = ImlightConfig.ValidateStarterKit(ids.Count);
        if (kitError is not null) {
            StarterKitError = kitError;
            AppendLog(LogLevel.Error, $"Starter items: {kitError} Nothing was saved.");
            return;
        }

        StarterKitError = string.Empty;

        try {
            _config.AdminUsername = AdminUsername.Trim();
            _config.AdminPassword = AdminPassword;
            _config.TutorialDisabled = TutorialDisabled;
            _config.DefaultItems = ItemList.Format(ids);
            _config.StartingLevel = StartingLevel;
            _config.StartingZone = StartingZone.Trim();
            _config.BaseGoldPouch = BaseGoldPouch;
            _config.GameServerIp = GameServerIp.Trim();
            _config.LoginServerPort = LoginServerPort;
            _config.GameServerPort = GameServerPort;
            _config.GameServerCount = GameServerCount;
            _config.RealmNames = RealmNames.Trim();
            _config.PatchServerUrl = PatchServerUrl;
            _config.SpiralDbAutoFetch = SpiralDbAutoFetch;
            _config.LogLevel = SelectedLogLevel;

            // The revision box is only editable while the launcher is not setting it from the patch
            // server, and while it is greyed out the value it shows is the patch server's own.
            _config.GameRevision = GameRevision.Trim();
            _config.EnforceRevision = EnforceRevision;
            _config.EmbeddedDatabasePort = EmbeddedDatabasePort;

            _config.Save(ServerPath);

            DefaultItemsText = _config.DefaultItems;
            SyncStarterKitFromIds(_config.DefaultItemsIds);
            AppendLog(LogLevel.Success, $"Saved settings to {ImlightConfig.RelativeConfigPath}.");
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Could not save Imlight.ini: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the recommended configuration in one click: change the admin credentials and narrow the
    /// starter kit to a Tier 1 wand plus the tutorial deck.
    /// </summary>
    /// <remarks>
    /// It deliberately does not touch <c>TutorialDisabled</c>. Skipping the tutorial is a choice the game
    /// itself offers - a button on the character-creation screen - so leaving a new character in it is not
    /// a lesser experience, while turning the tutorial off by default costs the character its school's
    /// first spell and its equipped starter kit. The tick box for it is right below, which is where the
    /// decision belongs.
    /// </remarks>
    [RelayCommand]
    private void ApplyStarterPreset() {
        if (_config is null) {
            AppendLog(LogLevel.Warning, "Install the server first - there is no Imlight.ini to write yet.");
            return;
        }

        try {
            DefaultItemsText = StarterPresetItems;

            _config.DefaultItems = StarterPresetItems;

            // Keep the explanatory comment above DefaultItems in sync with the values we wrote,
            // otherwise the file would document the old starter kit forever.
            _config.Ini.SetCommentAbove(
                ImlightConfig.SectionCharacter, "DefaultItems", StarterKitComments);

            if (!string.IsNullOrWhiteSpace(AdminUsername)) {
                _config.AdminUsername = AdminUsername.Trim();
            }

            if (!string.IsNullOrEmpty(AdminPassword)) {
                _config.AdminPassword = AdminPassword;
            }

            _config.Save(ServerPath);
            LoadServerConfig();

            AppendLog(LogLevel.Success, "Applied recommended settings:");
            AppendLog(LogLevel.Success, $"  DefaultItems = {StarterPresetItems}  (starter kit)");
            AppendLog(LogLevel.Success, $"  AdminAccountUsername = {_config.AdminUsername}");
            AppendLog(LogLevel.Info,
                "Skipping the tutorial is left as it is; the tick box under New characters is where that is set.");

            if (string.Equals(_config.AdminPassword, "compsci", StringComparison.Ordinal)) {
                AppendLog(LogLevel.Warning,
                    "The admin password is still the stock value 'compsci' - change it before exposing this server.");
            }
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Could not apply the recommended settings: {ex.Message}");
        }
    }

    [RelayCommand]
    private void GenerateAdminPassword() {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        AdminPassword = string.Create(16, alphabet, (span, chars) => {
            for (var i = 0; i < span.Length; i++) {
                span[i] = chars[random.Next(chars.Length)];
            }
        });

        AppendLog(LogLevel.Info, "Generated a new admin password. Save settings to apply it.");
    }

    // ---------------------------------------------------------------- starter items

    public ObservableCollection<ItemTemplate> FilteredCatalog { get; } = [];
    public ObservableCollection<ItemTemplate> StarterKit { get; } = [];

    [ObservableProperty] private string _itemSearch = string.Empty;
    [ObservableProperty] private ItemTemplate? _selectedCatalogItem;
    [ObservableProperty] private ItemTemplate? _selectedStarterItem;
    [ObservableProperty] private string _itemsNotice = string.Empty;
    [ObservableProperty] private bool _itemsAreStubbed;
    [ObservableProperty] private string _rootWadProbeText = string.Empty;

    /// <summary>
    /// What the client's own template manifest turned out to hold: the one part of the game's data the
    /// launcher can actually read, as opposed to the item names it still cannot.
    /// </summary>
    [ObservableProperty] private string _rootWadManifestText = string.Empty;
    [ObservableProperty] private string _starterKitError = string.Empty;

    public bool HasStarterKitError => !string.IsNullOrEmpty(StarterKitError);

    public string StarterKitSummary => $"{StarterKit.Count} of {ImlightConfig.MaxDefaultItems} items";

    public string StarterKitRequirement
        => $"The client needs at least {ImlightConfig.MinDefaultItems} items and Imlight can grant at most "
           + $"{ImlightConfig.MaxDefaultItems}.";

    public bool IsStarterKitOverLimit => StarterKit.Count > ImlightConfig.MaxDefaultItems;

    public bool CanRemoveStarterItem => StarterKit.Count > ImlightConfig.MinDefaultItems;

    public bool CanAddStarterItem => StarterKit.Count < ImlightConfig.MaxDefaultItems;

    partial void OnItemSearchChanged(string value) => ApplyCatalogFilter();

    /// <summary>
    /// How many catalogue rows the list is allowed to hold at once.
    /// </summary>
    /// <remarks>
    /// The server's item list is around eighty thousand items, and a list that size is not something a
    /// user scrolls - it is something they search. Showing the first slice and saying how many are behind
    /// the search box keeps the picker usable and still honest about what is there.
    /// </remarks>
    public const int MaxCatalogShown = 300;

    /// <summary>How many items the list is showing, and how many there are to search.</summary>
    [ObservableProperty] private string _catalogCountText = string.Empty;

    private async Task LoadItemCatalogAsync() {
        try {
            var catalog = await _itemService.LoadCatalogAsync(ClientPath, ServerPath).ConfigureAwait(true);
            _allCatalogItems = catalog.Items;
            ItemsAreStubbed = catalog.IsStubbed;
            ItemsNotice = catalog.Notice;

            var probe = _itemService.Probe(ClientPath);
            RootWadProbeText = probe.Found
                ? $"{Path.GetFileName(probe.Path)} · revision {probe.Version} · {probe.FileCount:N0} files"
                  + $" · {probe.SizeDisplay}"
                : probe.Error ?? "root.wad not found.";

            // Reading the manifest means unwrapping eleven megabytes, so it is done off the UI thread and
            // its answer is only ever a sentence: nothing here is on the way to anything else working.
            var manifest = await Task.Run(() => RootWadTemplateManifest.Read(ClientPath)).ConfigureAwait(true);
            RootWadManifestText = DescribeManifest(manifest);

            // The server's own lists, which are what the boss count is measured against. Read here rather
            // than on every profile refresh, because they are files on disk that change when the server
            // does, not while it runs.
            var manifests = await Task.Run(() => ImlightManifestReader.Read(ServerPath)).ConfigureAwait(true);
            UseManifests(manifests);

            ApplyCatalogFilter();
            SyncStarterKitFromIds(_config?.DefaultItemsIds ?? ItemList.Parse(DefaultItemsText));
        }
        catch (Exception ex) {
            ItemsNotice = $"Could not read the game's items: {ex.Message}";
        }
    }

    /// <summary>
    /// What to say about the template manifest, in the one line the settings screen has room for.
    /// </summary>
    /// <remarks>
    /// It leads with what was read and ends with what was not, because the manifest naming a template is
    /// not the same as the launcher being able to use it: the game's own item list is written in numeric
    /// template IDs, and those are the part of the file this cannot recover. Saying only the first half
    /// would read as everything being solved.
    /// </remarks>
    public static string DescribeManifest(TemplateManifest manifest) {
        if (!manifest.Found || manifest.TemplatePaths == 0) {
            return manifest.Problem is { Length: > 0 } problem
                ? problem
                : $"{RootWadTemplateManifest.FileName} named no templates this could read.";
        }

        var counts = $"{manifest.TemplatePaths:N0} templates";
        if (manifest.BossCount > 0) {
            counts += $", {manifest.BossCount:N0} of them bosses";
        }

        if (manifest.CreatureCount > 0) {
            counts += $", {manifest.CreatureCount:N0} creatures";
        }

        return manifest.Decoded
            ? $"Read out of the game's own template manifest: {counts}. The numeric template IDs the "
              + "starter list is written in are not in it in a form this can read."
            : $"The template manifest is present but not in a form this can decode: {counts} read by eye.";
    }

    private void ApplyCatalogFilter() {
        var term = ItemSearch.Trim();
        var matching = string.IsNullOrEmpty(term)
            ? _allCatalogItems
            : _allCatalogItems
                .Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                            || i.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                            || i.TemplateId.ToString().Contains(term, StringComparison.Ordinal))
                .ToList();

        FilteredCatalog.Clear();
        foreach (var item in matching.Take(MaxCatalogShown)) {
            FilteredCatalog.Add(item);
        }

        CatalogCountText = DescribeCatalog(matching.Count, _allCatalogItems.Count, FilteredCatalog.Count, term);
    }

    /// <summary>What the item list says about what it is showing.</summary>
    public static string DescribeCatalog(int matching, int total, int shown, string term) => (matching, shown) switch {
        (0, _) when total == 0 => "No items to show yet.",
        (0, _) => $"Nothing matches “{term}”. Try a name, a kind of item, or a template ID.",
        (_, var s) when s < matching =>
            $"Showing the first {s} of {matching:N0} matches — keep typing to narrow it down.",
        (_, var s) when s == total => $"All {s:N0} items.",
        (_, var s) => $"{s:N0} match{(s == 1 ? "" : "es")}.",
    };

    private void SyncStarterKitFromIds(IReadOnlyList<uint> ids) {
        StarterKit.Clear();
        foreach (var id in ids) {
            var known = _allCatalogItems.FirstOrDefault(i => i.TemplateId == id);
            StarterKit.Add(known ?? new ItemTemplate(id, $"Unknown item #{id}", "Not in catalogue"));
        }

        DefaultItemsText = ItemList.Format(ids);

        // Surface a hand-edited config that is already too small, rather than only complaining on save.
        StarterKitError = ImlightConfig.ValidateStarterKit(ids.Count) is { } problem && ids.Count > 0
            ? problem
            : string.Empty;

        RefreshDerived();
    }

    [RelayCommand]
    private void AddSelectedItemToKit() {
        if (SelectedCatalogItem is null) {
            return;
        }

        if (StarterKit.Count >= ImlightConfig.MaxDefaultItems) {
            StarterKitError = $"Imlight cannot grant more than {ImlightConfig.MaxDefaultItems} items.";
            return;
        }

        if (StarterKit.Any(i => i.TemplateId == SelectedCatalogItem.TemplateId)) {
            return;
        }

        StarterKit.Add(SelectedCatalogItem);
        StarterKitError = string.Empty;
        RefreshDefaultItemsText();
        RefreshDerived();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveStarterItem))]
    private void RemoveSelectedItemFromKit() {
        if (SelectedStarterItem is null) {
            return;
        }

        // Hard lock: below two items the client bricks the character, so removal stops here.
        if (StarterKit.Count <= ImlightConfig.MinDefaultItems) {
            StarterKitError = ImlightConfig.TooFewStarterItemsMessage;
            AppendLog(LogLevel.Warning,
                $"Starter items: {ImlightConfig.TooFewStarterItemsMessage} New characters would be broken.");
            return;
        }

        StarterKit.Remove(SelectedStarterItem);
        SelectedStarterItem = null;
        StarterKitError = string.Empty;
        RefreshDefaultItemsText();
        RefreshDerived();
    }

    [RelayCommand]
    private void AddAllFilteredToKit() {
        foreach (var item in FilteredCatalog.ToList()) {
            if (StarterKit.Count >= ImlightConfig.MaxDefaultItems) {
                break;
            }

            if (StarterKit.All(i => i.TemplateId != item.TemplateId)) {
                StarterKit.Add(item);
            }
        }

        StarterKitError = string.Empty;
        RefreshDefaultItemsText();
        RefreshDerived();
    }

    /// <summary>Replaces the kit with the wand + deck pair, which is the smallest valid kit.</summary>
    [RelayCommand]
    private void ResetStarterKit() {
        SyncStarterKitFromIds(ItemList.Parse(StarterPresetItems));
        AppendLog(LogLevel.Info, $"Starter items reset to {StarterPresetItems}.");
    }

    [RelayCommand]
    private void SaveStarterKit() {
        if (_config is null) {
            AppendLog(LogLevel.Warning, "Install the server first - there is no Imlight.ini to write yet.");
            return;
        }

        var error = ImlightConfig.ValidateStarterKit(StarterKit.Count);
        if (error is not null) {
            StarterKitError = error;
            AppendLog(LogLevel.Error, $"Starter items: {error} Nothing was saved.");
            return;
        }

        _config.DefaultItems = ItemList.Format(StarterKit.Select(i => i.TemplateId));
        _config.Save(ServerPath);
        DefaultItemsText = _config.DefaultItems;
        StarterKitError = string.Empty;
        AppendLog(LogLevel.Success, $"Starter items saved ({StarterKit.Count} items).");
        RefreshDerived();
    }

    private void RefreshDefaultItemsText()
        => DefaultItemsText = ItemList.Format(StarterKit.Select(i => i.TemplateId));

    // ---------------------------------------------------------------- releases & install

    [ObservableProperty] private string _latestReleaseText = "Not checked yet.";
    [ObservableProperty] private string _installedReleaseText = "Not downloaded";
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _installStage = "Idle";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _releaseCheckError = string.Empty;

    /// <summary>Which builds the launcher installs: see <see cref="ServerChannelChoices"/>.</summary>
    [ObservableProperty] private string _serverChannel = ServerChannelChoices.Prerelease;

    // The three radio buttons are one setting seen from three sides. They are written out rather than
    // bound to the string because a radio group needs one bool per option, and an option whose setter did
    // nothing when unchecked would leave the group unable to move.
    public bool IsStableChannel {
        get => ServerChannel == ServerChannelChoices.Stable;
        set { if (value) ServerChannel = ServerChannelChoices.Stable; }
    }

    public bool IsPrereleaseChannel {
        get => ServerChannel == ServerChannelChoices.Prerelease;
        set { if (value) ServerChannel = ServerChannelChoices.Prerelease; }
    }

    public bool IsBleedingEdgeChannel {
        get => ServerChannel == ServerChannelChoices.BleedingEdge;
        set { if (value) ServerChannel = ServerChannelChoices.BleedingEdge; }
    }

    public bool IsBleedingEdge => ServerChannel == ServerChannelChoices.BleedingEdge;

    /// <summary>What the channel being on means, in a sentence. Shown under the choice.</summary>
    public string ChannelNote => ServerChannel switch {
        ServerChannelChoices.Stable =>
            "Only builds Imlight publishes as a full release. There are none yet, so this says so rather "
            + "than quietly installing a prerelease.",
        ServerChannelChoices.BleedingEdge =>
            "Imlight's current source, cloned and built on this machine. Needs git and the .NET SDK - not "
            + "just the runtime - and can fail where those cannot build it. Bugs are the source's.",
        _ =>
            "The newest build on GitHub, prerelease or not. This is what Imlight publishes today, and the "
            + "default.",
    };

    /// <summary>
    /// The warning a bleeding-edge build carries, said once on the screen that offers it.
    /// </summary>
    /// <remarks>
    /// It is not decoration. A build from current source is not a build anybody tested or published, and
    /// reporting its bugs to the project as if it were a release sends people looking for a regression in
    /// code that never shipped - so the disclosure is part of choosing it, not a footnote in a document.
    /// </remarks>
    public string BleedingEdgeWarning =>
        "This is not a build anybody published or tested. Expect it not to work. If you report a bug to "
        + "Imlight's developers, say first that you are running a build made from their source rather than "
        + "a release - otherwise they will look for a fault in code that was never released.";

    /// <summary>The button that installs a server, named for what it will actually do.</summary>
    public string InstallButtonLabel => IsBleedingEdge ? "Build from source" : "Download latest";

    /// <summary>The same button on the first screen, which asks for a server rather than an update.</summary>
    public string InstallImlightLabel => IsBleedingEdge ? "Build Imlight" : "Download Imlight";

    /// <summary>What that button will do, for the card that offers it.</summary>
    public string InstallDescription => IsBleedingEdge
        ? "Clones Imlight's current source, builds it on this machine and installs the result into the "
          + "folder above. Anything already configured there is kept. Needs git, the .NET 10 SDK and "
          + "Python 3.12; the type and message files Imlight does not publish are read from your game."
        : "Downloads the newest build from GitHub and unpacks it into the folder above. Anything already "
          + "configured in that folder is kept.";

    /// <summary>Where a bleeding-edge build looks for the type dump Imcodec needs, for the note beside it.</summary>
    public string EdgeTypeDumpPath => _edgeService.TypeDumpPath;

    public string EdgeTypeDumpNote
        => "Imlight's Imcodec needs a WizWalker type dump and the game's message definitions, neither of "
           + "which the repository publishes. ViewLight makes them from your Wizard101 install: wiztype "
           + "reads the types from your client (Python 3.12 needed) and the message XML comes out of "
           + $"Root.wad. A dump you put at {EdgeTypeDumpPath} is used as it is instead.";

    public bool HasReleaseCheckError => !string.IsNullOrEmpty(ReleaseCheckError);

    private bool CanCheckForUpdates() => !IsBusy;

    /// <summary>
    /// Whether the install button can run. A bleeding-edge build needs no release to have been found:
    /// the thing it installs is the checkout, and finding that out is part of pressing the button.
    /// </summary>
    private bool CanInstall() => !IsBusy && (IsBleedingEdge || _latestRelease is not null);

    private bool CanPrimaryAction() => !IsBusy;

    /// <summary>
    /// One button, two jobs: get the server when it is missing, otherwise play.
    /// </summary>
    public string PrimaryActionLabel => ServerInstalled
        ? "▶  PLAY"
        : IsBleedingEdge ? "Build server" : "Download server";

    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private async Task PrimaryActionAsync() {
        if (!ServerInstalled) {
            await InstallImlightAsync();
            return;
        }

        await PlayAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync() {
        // Only cancelled, not disposed: the check it replaces is still unwinding, and disposing the
        // token underneath a process or an HTTP handler it is waiting in is how a clean cancel becomes an
        // ObjectDisposedException. Each check disposes its own when its work is done.
        _checkCancellation?.Cancel();
        var check = _checkCancellation = new CancellationTokenSource();

        if (IsBleedingEdge) {
            try {
                await CheckForSourceUpdatesAsync(check.Token).ConfigureAwait(true);
            }
            finally {
                if (ReferenceEquals(_checkCancellation, check)) {
                    _checkCancellation = null;
                }

                check.Dispose();
            }

            return;
        }

        ReleaseCheckError = string.Empty;
        LatestReleaseText = "Checking GitHub…";

        try {
            _latestRelease = await _releaseService
                .GetLatestWindowsReleaseAsync(ServerChannel, check.Token)
                .ConfigureAwait(true);
            LatestReleaseText = _latestRelease.Tag;
            AppendLog(LogLevel.Info, $"Newest Imlight build: {_latestRelease.Tag} ({_latestRelease.AssetName}).");
            RefreshInstalledReleaseText();
        }
        catch (OperationCanceledException) {
            // A newer check replaced this one; its answer is the one to show.
            return;
        }
        catch (Exception ex) {
            _latestRelease = null;
            LatestReleaseText = ServerChannel == ServerChannelChoices.Stable
                ? "No stable release yet."
                : "Could not reach GitHub.";
            ReleaseCheckError = ex.Message;
            AppendLog(LogLevel.Error, $"Release check failed: {ex.Message}");
        }
        finally {
            if (ReferenceEquals(_checkCancellation, check)) {
                _checkCancellation = null;
                RefreshDerived();
            }

            check.Dispose();
        }
    }

    /// <summary>
    /// Asks the source checkout what it is and whether GitHub has moved on from it.
    /// </summary>
    /// <remarks>
    /// A separate path from the release check because there is no release to look up: the answer is a
    /// commit, and the only two questions are what it is and whether the checkout is behind. A checkout
    /// that has never been cloned is not an error - it is the state the install button exists for.
    /// </remarks>
    private async Task CheckForSourceUpdatesAsync(CancellationToken cancellationToken) {
        ReleaseCheckError = string.Empty;
        LatestReleaseText = "Checking the source…";

        try {
            var state = await _edgeService.CheckAsync(cancellationToken).ConfigureAwait(true);
            _latestEdge = state;

            LatestReleaseText = state.IsCloned ? state.Summary : "Not built yet.";

            if (state.Problem is { Length: > 0 } problem) {
                ReleaseCheckError = problem;
                AppendLog(LogLevel.Error, $"Source check failed: {problem}");
            }
            else if (state.IsCloned) {
                AppendLog(LogLevel.Info, $"Current Imlight source: {state.Summary}.");
            }

            RefreshInstalledReleaseText();
        }
        catch (OperationCanceledException) {
            // A newer check replaced this one.
            return;
        }
        catch (Exception ex) {
            _latestEdge = null;
            LatestReleaseText = "Could not read the source.";
            ReleaseCheckError = ex.Message;
            AppendLog(LogLevel.Error, $"Source check failed: {ex.Message}");
        }
        finally {
            RefreshDerived();
        }
    }

    /// <summary>
    /// The channel changed: remember it, drop what the last channel found, and ask the new one.
    /// </summary>
    /// <remarks>
    /// Nothing installed is touched. A person trying a build from source and going back to a published
    /// one keeps the server they have until they install something else - the channel says what to
    /// install next, not what is on disk.
    /// </remarks>
    partial void OnServerChannelChanged(string value) {
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPrereleaseChannel));
        OnPropertyChanged(nameof(IsBleedingEdgeChannel));
        OnPropertyChanged(nameof(IsBleedingEdge));
        OnPropertyChanged(nameof(ChannelNote));
        OnPropertyChanged(nameof(BleedingEdgeWarning));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(InstallImlightLabel));
        OnPropertyChanged(nameof(InstallDescription));
        OnPropertyChanged(nameof(EdgeTypeDumpPath));
        OnPropertyChanged(nameof(PrimaryActionLabel));

        if (_startingUp) {
            return;
        }

        _store.Settings.ServerChannel = value;
        _store.Save();

        _latestRelease = null;
        _latestEdge = null;
        ReleaseCheckError = string.Empty;
        LatestReleaseText = "Not checked yet.";

        AppendLog(LogLevel.Info, $"Server builds: {value}.");
        RefreshDerived();
        _ = CheckForUpdatesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallOrUpdateAsync() {
        if (IsBleedingEdge) {
            await InstallFromSourceAsync().ConfigureAwait(true);
            return;
        }

        if (_latestRelease is null) {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerPath)) {
            AppendLog(LogLevel.Error, "Choose a server folder first.");
            return;
        }

        var updating = ServerInstalled;
        var release = _latestRelease;

        IsBusy = true;
        RefreshDerived();

        _installCancellation = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p => {
            InstallProgress = p.Percent;
            InstallStage = $"{p.Stage} · {p.Detail}";
        });

        AppendLog(LogLevel.Info, updating
            ? $"Updating server to {release.Tag}. Existing settings will be kept."
            : $"Downloading server {release.Tag} ({release.AssetSizeDisplay}).");

        try {
            // The databases are let go inside the install, at the last moment before the writing starts: a
            // download that fails is not worth having stopped them for.
            var result = await _installService
                .InstallAsync(release, ServerPath, progress, ReleaseDatabasesForInstallAsync,
                    _installCancellation.Token)
                .ConfigureAwait(true);

            if (result.Success) {
                _store.Settings.InstalledTag = release.Tag;
                _store.Settings.InstalledAssetUrl = release.AssetUrl;
                _store.Settings.InstalledChannel = ServerChannel;
                _store.Settings.InstalledEdgeCommit = null;
                _store.Settings.InstalledAt = DateTimeOffset.Now;
                _store.Save();

                AppendLog(LogLevel.Success,
                    $"{result.Message} ({result.FilesWritten} files written, {result.SettingsRetained} settings retained.)");

                if (result.Warning is { Length: > 0 }) {
                    AppendLog(LogLevel.Warning, result.Warning);
                }

                LogRepair(new ConfigRepair(result.EmbeddedDatabasePort, result.PatchServerUrl));
                AnnounceInstalledBuild(result.Tag);

                // A fresh install is what the launcher may have been waiting for, and a new build is what the
                // databases should be running - so the config is read again here, and the databases come back
                // below with the new build's binaries rather than the ones they were started from.
                LoadServerConfig();

                // An install is what brings a new engine, and the launcher's own copy follows it - here, and
                // not after the databases have been started again, because replacing the copy is replacing a
                // folder a running database would have open.
                await MakeOwnEngineAsync().ConfigureAwait(true);
            }
            else {
                AppendLog(LogLevel.Error, result.Message);
            }

            RefreshInstalledReleaseText();
            RefreshDerived();
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Install failed: {ex.Message}");
        }
        finally {
            EndInstall();
        }
    }

    /// <summary>
    /// Clones, updates, builds and installs Imlight's current source.
    /// </summary>
    /// <remarks>
    /// The same handover as an archive install - a running server is stopped and the databases are let go
    /// only when the written files are about to be touched - because a build that fails is exactly a
    /// download that failed, and neither is worth having stopped anything for. What is different is the
    /// time it takes: a clone and a publish are minutes, so every stage says where it is and Cancel ends
    /// the tool rather than only the wait.
    /// </remarks>
    private async Task InstallFromSourceAsync() {
        if (string.IsNullOrWhiteSpace(ServerPath)) {
            AppendLog(LogLevel.Error, "Choose a server folder first.");
            return;
        }

        var updating = ServerInstalled;

        IsBusy = true;
        RefreshDerived();

        _installCancellation = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p => {
            InstallProgress = p.Percent;
            InstallStage = $"{p.Stage} · {p.Detail}";
        });

        AppendLog(LogLevel.Info, updating
            ? "Building Imlight's current source. The build will be installed over the existing server."
            : "Cloning Imlight's source and building it. This takes a few minutes the first time.");

        try {
            var result = await _edgeService
                .InstallAsync(ServerPath, ClientPath, progress, ReleaseDatabasesForInstallAsync,
                    _installCancellation.Token)
                .ConfigureAwait(true);

            if (result.Success) {
                _store.Settings.InstalledTag = result.Tag;
                _store.Settings.InstalledAssetUrl = null;
                _store.Settings.InstalledChannel = ServerChannelChoices.BleedingEdge;
                _store.Settings.InstalledEdgeCommit = result.Commit;
                _store.Settings.InstalledAt = DateTimeOffset.Now;
                _store.Save();

                // What was just built is what the checkout now holds, so the card stops offering an
                // update the moment the install finishes rather than keeping the answer from before it.
                var shortCommit = BleedingEdgeServer.IsEdgeTag(result.Tag)
                    ? result.Tag[BleedingEdgeServer.TagPrefix.Length..]
                    : result.Tag;

                _latestEdge = _latestEdge is null
                    ? new EdgeSourceState(true, result.Commit, shortCommit, string.Empty, null, 0, false, null)
                    : _latestEdge with { Commit = result.Commit, ShortCommit = shortCommit, Behind = 0 };

                AppendLog(LogLevel.Success,
                    $"{result.Message} ({result.FilesWritten} files written, {result.SettingsRetained} settings retained.)");
                AppendLog(LogLevel.Warning, BleedingEdgeWarning);

                if (result.Warning is { Length: > 0 }) {
                    AppendLog(LogLevel.Warning, result.Warning);
                }

                LogRepair(new ConfigRepair(result.EmbeddedDatabasePort, result.PatchServerUrl));
                AnnounceInstalledBuild(result.Tag);
                LoadServerConfig();
                await MakeOwnEngineAsync().ConfigureAwait(true);
            }
            else {
                AppendLog(LogLevel.Error, result.Message);
            }

            RefreshInstalledReleaseText();
            RefreshDerived();
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Build failed: {ex.Message}");
        }
        finally {
            EndInstall();
        }
    }

    /// <summary>
    /// The end of any install, whichever way it went.
    /// </summary>
    /// <remarks>
    /// The launcher's databases come back here - a failed install is worth less than the profiles and
    /// accounts they are read from, and half an overlay is a real thing. In the background rather than
    /// awaited: a RavenDB start is tens of seconds, and the button that started this is not the place to
    /// spend them. Both opens are single-flight, so a second ask is answered by the one already running
    /// rather than by a second database on the same port.
    /// </remarks>
    private void EndInstall() {
        _ = EnsureLauncherDatabaseAsync();
        _ = EnsureIdleDatabaseAsync();

        _installCancellation?.Dispose();
        _installCancellation = null;
        IsBusy = false;
        InstallStage = "Idle";
        InstallProgress = 0;
        RefreshDerived();
    }

    /// <summary>Picks a port for the embedded RavenDB that is free right now.</summary>
    [RelayCommand]
    private void FindFreeEmbeddedDatabasePort() {
        var port = PortAllocator.FindFreePort();
        if (port is null) {
            AppendLog(LogLevel.Error, "Could not find a free port for the server's database.");
            return;
        }

        EmbeddedDatabasePort = port.Value;
        AppendLog(LogLevel.Info, $"Database port set to {port.Value}. Save settings to apply it.");
    }

    /// <summary>Points the patch server back at the one the Revive101 team runs.</summary>
    [RelayCommand]
    private void UsePublicPatchServer() {
        PatchServerUrl = ImlightConfig.PublicPatchServerUrl;
        AppendLog(LogLevel.Info,
            $"Patch server set to {ImlightConfig.PublicPatchServerUrl}. Save settings to apply it.");
    }

    /// <summary>
    /// Reports what a <see cref="ServerConfigRepair"/> run changed and brings the Settings panel in
    /// line with what is now on disk.
    /// </summary>
    private void LogRepair(ConfigRepair repair) {
        if (repair.EmbeddedDatabasePort is { } databasePort) {
            EmbeddedDatabasePort = databasePort;
            AppendLog(LogLevel.Info,
                $"The server's database was moved to port {databasePort}, because "
                + $"{ImlightConfig.DefaultEmbeddedDatabasePort} is already in use and Imlight will not start on a port it cannot have.");
        }

        if (repair.PatchServerUrl is { } patchServer) {
            PatchServerUrl = patchServer;
            AppendLog(LogLevel.Info,
                $"Patch server set to {patchServer}. Imlight loads Root.wad from a patch server, and it "
                + "cannot boot while that address points at nothing.");
        }
    }

    [RelayCommand]
    private void CancelInstall() {
        _installCancellation?.Cancel();
        AppendLog(LogLevel.Warning,
            IsBleedingEdge ? "Cancelling the build…" : "Cancelling the download…");
    }

    /// <summary>
    /// Says what the server folder now holds, in the terms the next run will see it.
    /// </summary>
    /// <remarks>
    /// The managed assembly's timestamp is the build's own time, copied along with the file, so it is
    /// evidence that the folder changed rather than a restatement of the tag the caller already had. This
    /// is what makes "installed" a fact about disk instead of a claim about settings.
    /// </remarks>
    private void AnnounceInstalledBuild(string tag) {
        var assembly = Path.Combine(ServerPath, "Imlight.Director.dll");

        if (!File.Exists(assembly)) {
            return;
        }

        AppendLog(LogLevel.Success,
            $"The server folder now holds {tag}; Imlight.Director.dll is dated "
            + $"{File.GetLastWriteTime(assembly):d MMM yyyy HH:mm:ss}.");
    }

    private void RefreshInstalledReleaseText() {
        var settings = _store.Settings;
        var date = settings.InstalledAt?.LocalDateTime.ToString("d MMM yyyy");

        if (BleedingEdgeServer.IsEdgeTag(settings.InstalledTag)) {
            var commit = settings.InstalledTag![BleedingEdgeServer.TagPrefix.Length..];
            var head = $"Bleeding edge {commit}";
            InstalledReleaseText = date is null ? head : $"{head} · {date}";
            return;
        }

        InstalledReleaseText = settings.InstalledTag is null
            ? "Not downloaded"
            : date is null ? settings.InstalledTag : $"{settings.InstalledTag} · {date}";
    }

    public string InstalledTag => _store.Settings.InstalledTag ?? "—";

    /// <summary>
    /// Whether the channel has something newer than what is in the server folder.
    /// </summary>
    /// <remarks>
    /// A build from source is compared by commit rather than tag: the tag carries only the short hash, and
    /// two short hashes that collide are not the same build. A channel with no answer yet - a release check
    /// that failed, a source checkout never looked at - is not an update, so the button keeps saying what
    /// it said rather than offering to reinstall the same thing.
    /// </remarks>
    public bool IsUpdateAvailable {
        get {
            if (!ServerInstalled) {
                return false;
            }

            if (IsBleedingEdge) {
                return _latestEdge is { IsCloned: true, Commit.Length: > 0 } edge
                       && !string.Equals(_store.Settings.InstalledEdgeCommit, edge.Commit,
                           StringComparison.Ordinal);
            }

            return _latestRelease is not null
                   && !string.Equals(_store.Settings.InstalledTag, _latestRelease.Tag, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the two builds

    /// <summary>The build in the server folder, as it was recorded when it was installed.</summary>
    public string ServerBuildText {
        get {
            if (!ServerInstalled) {
                return "Not installed";
            }

            if (BleedingEdgeServer.IsEdgeTag(_store.Settings.InstalledTag)) {
                return $"Bleeding edge {_store.Settings.InstalledTag![BleedingEdgeServer.TagPrefix.Length..]}";
            }

            return _store.Settings.InstalledTag
                   ?? (string.IsNullOrWhiteSpace(GameRevision) ? "Unknown" : GameRevision);
        }
    }

    /// <summary>
    /// The client's own revision, read from the game folder rather than remembered - see
    /// <see cref="ClientBuildReader"/>.
    /// </summary>
    [ObservableProperty] private string? _clientBuild;

    public string ClientBuildText => ClientBuild ?? "Not found";

    public bool HasClientBuild => ClientBuild is not null;

    /// <summary>
    /// Shown only when there is no client build to show, because it says how to get one.
    /// </summary>
    /// <remarks>
    /// A complete install ships <c>bin\revision.dat</c> already - it is there before the game has ever
    /// run - so this appears when the folder is not one, or when neither source can be read. Starting the
    /// game once is still the answer either way, because the client then writes the same revision into
    /// its own log, which is the fallback <see cref="ClientBuildReader"/> reads. The wording names the file
    /// that actually gets written, rather than <c>revision.dat</c>, which does not.
    /// </remarks>
    public string ClientBuildMissingHint
        => $"Start the game once and it will write its revision into {ClientBuildReader.RelativeClientLog}, "
           + $"which ViewLight reads. A complete install also ships {ClientBuildReader.RelativeRevisionFile}.";

    /// <summary>The revision this server folder accepts. Blank GameRevision falls back to the tag.</summary>
    /// <remarks>
    /// A build from source has no release tag to fall back to - <c>edge-abc1234</c> is a commit, not a
    /// game revision - so it falls back to nothing rather than to a value the client could never match.
    /// </remarks>
    public string? ExpectedClientBuild => !ServerInstalled
        ? null
        : !string.IsNullOrWhiteSpace(GameRevision)
            ? GameRevision
            : BleedingEdgeServer.IsEdgeTag(_store.Settings.InstalledTag)
                ? null
                : _store.Settings.InstalledTag;

    /// <summary>
    /// The client, when the launcher can see that it is not the revision the server is set to accept.
    /// This is read from the client itself, so it catches a game that has been updated under an older
    /// server - which is exactly the case that is otherwise reported as a failed login.
    /// </summary>
    public string? ClientBuildMismatch
        => DescribeBuildMismatch(ClientBuild, ExpectedClientBuild, EnforceRevision);

    public bool HasClientBuildMismatch => ClientBuildMismatch is not null;

    // Turning the revision check on or off changes what the mismatch means, so the sentence is rebuilt.
    partial void OnEnforceRevisionChanged(bool value) => RefreshDerived();
    partial void OnGameRevisionChanged(string value) {
        RefreshDerived();

        // A copy into the patch server is planned against the revision the server asks it for, so the card
        // under Settings is worked out again rather than left describing the value the config held before -
        // the sync writing a new one is the case that matters.
        RefreshPreloadPlanIfTheRevisionMoved();
    }

    /// <summary>The one line under the Play button. About the game, not about the server.</summary>
    public string HomeHint => IsRemoteSession
        // The dropdown has pointed Play somewhere else, so the usual line about the server being
        // started here would be a lie.
        ? JoinedEndpoint is null
            ? "Play starts Wizard101 against the server chosen in the dropdown."
            : $"Play starts Wizard101 against {JoinedEndpoint}."
        : !ServerInstalled
            ? "The Imlight server is downloaded once - about 260 MB - and then Play starts everything."
            : IsUpdateAvailable
                ? $"A newer server build is available ({_latestRelease!.Tag}). Update it under Settings, then Play."
                : "Play starts the server and Wizard101 for you.";

    /// <summary>
    /// The credentials Play is about to use. Only ever said for this launcher's own server: an Imlight
    /// server somewhere else has accounts the launcher knows nothing about, and Wizard101's own server
    /// has the player's Wizard101 account rather than the local admin one.
    /// </summary>
    public string LoginHint => !IsRemoteSession && ServerInstalled && !string.IsNullOrWhiteSpace(AdminUsername)
        ? $"Log in as {AdminUsername}"
        : string.Empty;

    public bool HasLoginHint => !string.IsNullOrWhiteSpace(LoginHint);

    [RelayCommand]
    private static void OpenLink(string? url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return;
        }

        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) {
            // Opening a browser is best effort.
        }
    }

    // ---------------------------------------------------------------- where play points

    /// <summary>Which server the game is being started against.</summary>
    [ObservableProperty] private PlayServerKind _playServer = PlayServerKind.Local;

    /// <summary>The address the game is being pointed at, when that is not this launcher's own server.</summary>
    [ObservableProperty] private ServerEndpoint? _joinedEndpoint;

    /// <summary>True when Play is not starting this launcher's own server.</summary>
    public bool IsRemoteSession => PlayServer != PlayServerKind.Local;

    /// <summary>True when the address was Wizard101's own, which is said differently from a private one.</summary>
    public bool IsOfficialServer => PlayServer == PlayServerKind.Official;

    // The server row and the login line both change meaning when the server is not this one's, so they
    // are rebuilt rather than left saying "Server: Stopped" about a server that was never ours.
    partial void OnPlayServerChanged(PlayServerKind value) => RefreshDerived();
    partial void OnJoinedEndpointChanged(ServerEndpoint? value) => RefreshDerived();

    /// <summary>The label beside the status dot that describes which server Play is using.</summary>
    public string ServerRowLabel => IsRemoteSession ? "Remote server" : "Server";

    /// <summary>What that row says: the local server's state, or the address being joined.</summary>
    public string ServerRowState => IsRemoteSession
        ? JoinedEndpoint?.ToString() ?? "Not chosen"
        : ServerStateText;

    // ---------------------------------------------------------------- joining another server

    /// <summary>Whether the join card is open on Home.</summary>
    [ObservableProperty] private bool _isJoinPanelVisible;

    /// <summary>The address typed into the join box.</summary>
    [ObservableProperty] private string _remoteHost = string.Empty;

    /// <summary>The port typed into the join box, kept as text so a half-typed number is not thrown away.</summary>
    [ObservableProperty] private string _remotePortText = ServerEndpoint.DefaultPort.ToString();

    /// <summary>True while a network scan is running.</summary>
    [ObservableProperty] private bool _isScanning;

    /// <summary>What the join card has to say: what was found, or why an address was refused.</summary>
    [ObservableProperty] private string _joinStatus = string.Empty;

    public bool HasJoinStatus => !string.IsNullOrWhiteSpace(JoinStatus);

    /// <summary>What a scan turned up, each row carrying its own Join.</summary>
    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = [];

    public bool HasDiscoveredServers => DiscoveredServers.Count > 0;

    /// <summary>Opens the join card with the last address the player used, if there was one.</summary>
    [RelayCommand]
    private void ShowJoinPanel() {
        RemoteHost = _store.Settings.LastRemoteHost;
        RemotePortText = _store.Settings.LastRemotePort.ToString();
        IsJoinPanelVisible = true;
        RefreshDerived();
    }

    [RelayCommand]
    private void HideJoinPanel() {
        IsJoinPanelVisible = false;
        RefreshDerived();
    }

    /// <summary>Joins the address in the join box, after checking that it is one.</summary>
    [RelayCommand]
    private async Task JoinRemoteAsync() {
        var port = int.TryParse(RemotePortText?.Trim(), out var parsed)
            ? parsed
            : ServerEndpoint.DefaultPort;

        var endpoint = ServerEndpoint.Parse(RemoteHost, port);
        if (endpoint is null) {
            JoinStatus = "Type the address of a server, for example 192.168.1.20 or 192.168.1.20:12000.";
            RefreshDerived();
            return;
        }

        await JoinServerAsync(endpoint, PlayServerKind.Remote).ConfigureAwait(true);
    }

    /// <summary>Joins Wizard101's own login server, whose accounts are Wizard101's rather than a local server's.</summary>
    /// <remarks>
    /// The Steam split is not cosmetic. A Steam-linked Wizard101 account cannot sign in without
    /// <c>-ST</c>, and a standalone account is not sent it, so guessing wrong produces a login that is
    /// refused with nothing on screen explaining why - hence two menu entries instead of one.
    /// </remarks>
    [RelayCommand]
    private Task JoinOfficialStandaloneAsync() => JoinOfficialAsync(steam: false);

    [RelayCommand]
    private Task JoinOfficialSteamAsync() => JoinOfficialAsync(steam: true);

    private async Task JoinOfficialAsync(bool steam) {
        _store.Settings.OfficialUsesSteam = steam;
        _store.Save();
        await JoinServerAsync(OfficialServer.Endpoint, PlayServerKind.Official).ConfigureAwait(true);
    }

    /// <summary>
    /// Whether a launch has to carry <c>-ST</c>.
    /// </summary>
    /// <remarks>
    /// Only Wizard101's own server is ever signed in to with a Wizard101 account: a private Imlight
    /// server has accounts of its own in its own database, so the flag is never sent to one, whatever
    /// was chosen for the official server last time. Stated as a rule so the wiring can be tested
    /// without starting a client.
    /// </remarks>
    public static bool SteamRequiredFor(PlayServerKind kind, bool officialUsesSteam)
        => kind == PlayServerKind.Official && officialUsesSteam;

    /// <summary>Sweeps this machine's subnets for anything answering on the login port.</summary>
    [RelayCommand]
    private async Task ScanForServersAsync() {
        if (IsScanning) {
            return;
        }

        IsJoinPanelVisible = true;
        IsScanning = true;
        JoinStatus = "Looking for servers on your network…";
        DiscoveredServers.Clear();
        RefreshDerived();

        _scanCancellation?.Dispose();
        var scan = new CancellationTokenSource();
        _scanCancellation = scan;

        try {
            var found = await _discovery
                .ScanAsync(ServerEndpoint.DefaultPort, null, scan.Token)
                .ConfigureAwait(true);

            foreach (var endpoint in found) {
                DiscoveredServers.Add(new DiscoveredServer(endpoint, JoinDiscoveredAsync));
            }

            JoinStatus = found.Count switch {
                0 => "Nothing answered on your network. A server on another network has to be joined by "
                     + "its address instead.",
                1 => "One device is listening. Press Join if it is the server you are after.",
                _ => $"{found.Count} devices are listening. Press Join on the one you are after.",
            };
        }
        catch (OperationCanceledException) {
            JoinStatus = "The search was cancelled.";
        }
        finally {
            IsScanning = false;
            _scanCancellation = null;
            scan.Dispose();
            RefreshDerived();
        }
    }

    private Task JoinDiscoveredAsync(ServerEndpoint endpoint) {
        RemoteHost = endpoint.Host;
        RemotePortText = endpoint.Port.ToString();
        return JoinServerAsync(endpoint, PlayServerKind.Remote);
    }

    /// <summary>
    /// Points the game at a server this launcher does not run. No server is started, and there is no
    /// automatic sign-in: a session key means nothing on a server that never issued it.
    /// </summary>
    private async Task JoinServerAsync(ServerEndpoint endpoint, PlayServerKind kind) {
        if (GameClientState is ClientState.Starting or ClientState.Running) {
            JoinStatus = "Wizard101 is already running. Close it before joining another server.";
            RefreshDerived();
            return;
        }

        if (ClientExecutablePath is null) {
            JoinStatus = $"Wizard101 was not found. Set the game folder under Settings.";
            FailureHeadline = "Wizard101 was not found";
            FailureDetail = $"Expected {GameFolderLocator.RelativeClientExecutable} inside the game folder. "
                           + "Set the game folder under Settings.";
            Phase = LauncherPhase.Failed;
            AppendLog(LogLevel.Error, "No game client to start, so there is nothing to join with.");
            RefreshDerived();
            return;
        }

        if (kind == PlayServerKind.Remote) {
            _store.Settings.LastRemoteHost = endpoint.Host;
            _store.Settings.LastRemotePort = endpoint.Port;
            _store.Save();
        }

        PlayServer = kind;
        JoinedEndpoint = endpoint;
        Phase = LauncherPhase.StartingClient;
        JoinStatus = string.Empty;

        var steamRequired = SteamRequiredFor(kind, _store.Settings.OfficialUsesSteam);

        try {
            await LaunchClientAsync(endpoint, allowAutoLogin: false, steamRequired: steamRequired)
                .ConfigureAwait(true);
        }
        finally {
            RefreshDerived();
        }
    }

    // ---------------------------------------------------------------- server process

    [ObservableProperty] private ServerState _serverState = ServerState.Stopped;

    // The server's own line, the dot beside it and the Stop button all read from this.
    partial void OnServerStateChanged(ServerState value) => RefreshDerived();

    /// <summary>The server in one word, for the row beside its status dot.</summary>
    public string ServerStateText => DescribeServerState(ServerState);

    /// <summary>
    /// A server's state in one word. Shared with the patch server, which moves through the same five
    /// states and is read on screen the same way.
    /// </summary>
    public static string DescribeServerState(ServerState state) => state switch {
        ServerState.Stopped => "Stopped",
        ServerState.Starting => "Starting",
        ServerState.WarmingUp => "Warming up",
        ServerState.Ready => "Running",
        ServerState.Stopping => "Stopping",
        ServerState.Faulted => "Stopped unexpectedly",
        _ => state.ToString(),
    };

    /// <summary>True while a server process the launcher started is still alive.</summary>
    public bool ServerIsRunning => _server.IsRunning;

    // ---------------------------------------------------------------- what the launcher is doing

    /// <summary>
    /// Where Play has got to. Home and the sidebar render nothing at all while this is
    /// <see cref="LauncherPhase.Idle"/>: a server that is not running yet is not news, because the
    /// launcher starts it when Play is pressed.
    /// </summary>
    [ObservableProperty] private LauncherPhase _phase = LauncherPhase.Idle;

    // Every headline, every row and every indicator on Home is derived from the phase, so moving it is
    // what redraws them. Doing it here rather than at each call site means a phase set anywhere is shown.
    partial void OnPhaseChanged(LauncherPhase value) => RefreshDerived();

    /// <summary>Whether there is anything to report at all.</summary>
    public bool IsStatusVisible => IsStatusShown(Phase);

    /// <summary>True while a step is under way, which is when the status animates.</summary>
    public bool IsStatusWorking => IsPhaseWorking(Phase);

    public bool IsStatusGood => IsPhaseGood(Phase);

    public bool IsStatusFailed => Phase == LauncherPhase.Failed;

    /// <summary>
    /// Whether a phase is worth a word. Idle is the only one that is not, and that is the whole of the
    /// "the launcher is not doing anything, so it says nothing" rule.
    /// </summary>
    public static bool IsStatusShown(LauncherPhase phase) => phase != LauncherPhase.Idle;

    /// <summary>A step is in progress, so the status animates.</summary>
    public static bool IsPhaseWorking(LauncherPhase phase)
        => phase is LauncherPhase.StartingServer or LauncherPhase.WarmingServer or LauncherPhase.StartingClient;

    /// <summary>A step finished well, so the status reads as success.</summary>
    public static bool IsPhaseGood(LauncherPhase phase)
        => phase is LauncherPhase.ServerReady or LauncherPhase.Playing;

    /// <summary>
    /// The phase a server state change leads to. Stated as a rule rather than left inside the event
    /// handler, so the transitions can be tested without a server.
    /// </summary>
    /// <param name="current">The phase the launcher is in now.</param>
    /// <param name="state">The state the server just moved to.</param>
    /// <param name="playInFlight">
    /// Whether Play is waiting on this server, in which case readiness means the game starts next and
    /// the launcher must not settle into "the server is ready" on the way. A server started by hand
    /// from Settings does settle there.
    /// </param>
    public static LauncherPhase PhaseForServer(LauncherPhase current, ServerState state, bool playInFlight)
        => state switch {
            ServerState.Starting when current == LauncherPhase.Idle => LauncherPhase.StartingServer,
            ServerState.WarmingUp when current is LauncherPhase.Idle or LauncherPhase.StartingServer
                => LauncherPhase.WarmingServer,
            ServerState.Ready when !playInFlight
                                   && current is LauncherPhase.Idle or LauncherPhase.StartingServer
                                       or LauncherPhase.WarmingServer
                => LauncherPhase.ServerReady,
            ServerState.Faulted => LauncherPhase.Failed,

            // A server that is not running is not reported, whether it was stopped by the user or simply
            // never started.
            ServerState.Stopped => LauncherPhase.Idle,

            // Stopping keeps whatever it was: the game may still be up, and it is about to go down.
            _ => current,
        };

    /// <summary>
    /// The phase a client state change leads to. A closed game leaves the server's own state standing,
    /// because the launcher still has a server running and the user may want back in.
    /// </summary>
    /// <remarks>
    /// A failure outranks all of this. When a server goes down it takes the game with it, so the client
    /// exit lands right after the crash - and a failure the user never got to read is worse than no
    /// message at all. It stays up until something is actually done about it, i.e. until Play.
    /// </remarks>
    public static LauncherPhase PhaseForClient(LauncherPhase current, ClientState client, ServerState server)
        => current == LauncherPhase.Failed
            ? LauncherPhase.Failed
            : client switch {
                ClientState.Starting => LauncherPhase.StartingClient,
                ClientState.Running => LauncherPhase.Playing,
                ClientState.Closed => server == ServerState.Ready
                    ? LauncherPhase.ServerReady
                    : LauncherPhase.Idle,
                _ => current,
            };

    /// <summary>
    /// The sentence shown when the client is not the revision the server accepts, or null when they
    /// agree or when either is unknown. Stated once so it can be tested without a client or a server.
    /// </summary>
    public static string? DescribeBuildMismatch(
        string? clientBuild, string? expectedBuild, bool enforceRevision) {
        if (clientBuild is null || expectedBuild is null
            || ClientBuildReader.AreSame(clientBuild, expectedBuild)) {
            return null;
        }

        // Which way round the two are decides what the fix is, so the sentence names the one that is out of
        // date instead of leaving the user to compare revision numbers themselves.
        var which = ClientBuildReader.Compare(clientBuild, expectedBuild) switch {
            < 0 => $"Your Wizard101 client is outdated: it is {clientBuild}, and this server is built for "
                   + $"{expectedBuild}. Update the game.",
            > 0 => $"Your Wizard101 client is newer than this server is built for: it is {clientBuild}, and "
                   + $"the server expects {expectedBuild}. Update the server.",
            _ => $"Your Wizard101 client is {clientBuild}, while this server expects {expectedBuild}.",
        };

        return enforceRevision
            ? $"{which} The revision check is on, so it will turn the client away."
            : $"{which} The revision check is off, so the game may still get in, but nothing should be "
              + "reported as a bug until the two match.";
    }

    /// <summary>The line Home leads with.</summary>
    public string StatusHeadline => HeadlineFor(Phase, GameClientState, FailureHeadline);

    /// <summary>
    /// The sentence under the headline. When the game is being pointed at a server that is not this
    /// one's, it names the address instead of talking about starting a local server.
    /// </summary>
    public string StatusDetail => IsRemoteSession && Phase == LauncherPhase.StartingClient
        ? IsOfficialServer
            ? $"Pointing Wizard101 at {OfficialServer.Endpoint}. Sign in with your Wizard101 account."
            : $"Pointing Wizard101 at {JoinedEndpoint}. Sign in when the game asks."
        : DetailFor(Phase, GameClientState, FailureDetail);

    /// <summary>
    /// What Home leads with while a phase is on screen.
    /// </summary>
    /// <remarks>
    /// Written about getting into the game, not about the server. The launcher is doing the server work
    /// on the user's behalf, so which server is starting, whether it is listening and what its state is
    /// belong in the detail line and in the rows underneath - see <see cref="DetailFor"/> - rather than
    /// in the headline the user reads first. Stated as a rule so the wording itself can be tested.
    /// </remarks>
    public static string HeadlineFor(LauncherPhase phase, ClientState client, string failureHeadline)
        => phase switch {
            LauncherPhase.StartingServer => "Getting ready…",
            LauncherPhase.WarmingServer => "Warming up…",
            LauncherPhase.ServerReady => client == ClientState.Closed
                ? "Wizard101 was closed"
                : "Ready to play",
            LauncherPhase.StartingClient => "Starting Wizard101…",
            LauncherPhase.Playing => "Wizard101 is running",

            // A failure says what actually failed: "the server stopped", "Wizard101 was not found".
            LauncherPhase.Failed => failureHeadline,
            _ => string.Empty,
        };

    /// <summary>
    /// The sentence under the headline, which is where the server's own progress is named.
    /// </summary>
    public static string DetailFor(LauncherPhase phase, ClientState client, string failureDetail)
        => phase switch {
            LauncherPhase.StartingServer => "Starting the Imlight server. Nothing to do yet.",
            LauncherPhase.WarmingServer => "The server is loading world data and opening the game ports. "
                                           + "A first run takes a few minutes.",
            LauncherPhase.ServerReady => client == ClientState.Closed
                ? "The server is still running, so press Play to go back in."
                : "Press Play and the game will start.",
            LauncherPhase.StartingClient => "Handing Wizard101 the address of your server.",
            LauncherPhase.Playing => "Close the launcher when you are done and the server stops with it.",
            LauncherPhase.Failed => failureDetail,
            _ => string.Empty,
        };

    /// <summary>What went wrong, said plainly. Only shown while the phase is failed.</summary>
    [ObservableProperty] private string _failureHeadline = "Something went wrong";
    [ObservableProperty] private string _failureDetail = string.Empty;

    // The headline is built from these, so a failure written up in one order or another is always the
    // one the screen ends up showing.
    partial void OnFailureHeadlineChanged(string value) => RefreshDerived();
    partial void OnFailureDetailChanged(string value) => RefreshDerived();

    // ---------------------------------------------------------------- the game client

    /// <summary>What the client the launcher started is doing.</summary>
    public ClientState GameClientState => _client.State;

    public bool ClientIsStarting => _client.State == ClientState.Starting;

    public bool ClientIsRunning => _client.State == ClientState.Running;

    public bool ClientIsClosed => _client.State == ClientState.Closed;

    /// <summary>The game in one word, for the row beside its status dot.</summary>
    public string ClientStateText => _client.State switch {
        ClientState.NotRunning => "Not started",
        ClientState.Starting => "Starting",
        ClientState.Running => "Running",
        ClientState.Closed => "Closed",
        _ => _client.State.ToString(),
    };

    /// <summary>The game in one sentence.</summary>
    public string ClientSentence => _client.State switch {
        ClientState.NotRunning => "Playing has not started it yet.",
        ClientState.Starting => "Starting up — this takes a moment.",
        ClientState.Running => "You are in the game.",
        ClientState.Closed => "The client the launcher started has exited.",
        _ => string.Empty,
    };

    /// <summary>
    /// Moves the phase on when the server changes state, for the changes the launcher is not driving
    /// itself. Play sets its own phases; this is what covers a server started by hand from Settings.
    /// </summary>
    private void SyncPhaseWithServer() => Phase = PhaseForServer(Phase, ServerState, _playInFlight);

    private void OnClientStateChanged(ClientState state) {
        Phase = PhaseForClient(Phase, state, ServerState);

        switch (state) {
            case ClientState.Running:
                AppendLog(LogLevel.Success, "Wizard101 is up. You can play.");
                break;

            case ClientState.Closed:
                AppendLog(LogLevel.Info, "Wizard101 was closed.");

                // The game is over, so the session is too: what has been played is added up now rather than
                // left to a poll that will never come, and nothing else is credited to that profile until
                // Play starts another one.
                EndPlayingSession();
                break;
        }

        RefreshDerived();
    }

    /// <summary>
    /// Ends the playing session, if one is on: the game went away, so the time it was up is added up and
    /// the next Play starts a new sign-in.
    /// </summary>
    /// <remarks>
    /// Idempotent, and called from every way a session can end - the client exiting, the server stopping, the
    /// window closing - because more than one of them usually happens and none of them should be able to
    /// credit the same minute twice.
    /// </remarks>
    private void EndPlayingSession() {
        if (!_ledger.SessionIsOn) {
            return;
        }

        _ledger.EndSession(DateTimeOffset.Now);
        RefreshProfiles();
    }

    /// <summary>
    /// The server went down on its own - a deliberate stop never reaches here, because the runner only
    /// reports an exit it did not ask for. The exit code is kept in the failure line because support
    /// asks for it, and the server's own error lines are already quoted into the Log.
    /// </summary>
    private void OnServerExited(int exitCode) {
        FailureHeadline = "The server stopped unexpectedly";
        FailureDetail = exitCode == 0
            ? "It stopped before you asked it to. The Log has the last thing it said."
            : $"It exited with code {exitCode}. The Log has the last thing it said.";

        // The server nobody is playing on any more is not a session: the game cannot be in a world whose
        // server has gone, so playtime stops here rather than at the next look that will never happen.
        EndPlayingSession();

        Phase = LauncherPhase.Failed;
        RefreshDerived();

        // A server that died has stopped holding its database, so the launcher takes it back.
        _ = EnsureIdleDatabaseAsync();
    }

    /// <summary>Which colour the status dot takes: busy, up, or broken. Idle is the default.</summary>
    public bool IsServerWorking => ServerState is ServerState.Starting or ServerState.WarmingUp or ServerState.Stopping;

    public bool IsServerReady => ServerState == ServerState.Ready;

    /// <summary>
    /// The dot beside the server row. A server this launcher does not run is neither starting nor up as
    /// far as it knows - it is simply the one being joined, so the row reads as ready rather than grey.
    /// </summary>
    public bool IsServerRowReady => IsRemoteSession || IsServerReady;

    public bool IsServerFaulted => ServerState == ServerState.Faulted;

    /// <summary>
    /// Whether closing the window has to ask first. Closing stops the server, so a running one is worth
    /// a confirmation - and a stopped one never is.
    /// </summary>
    public bool ShouldWarnBeforeClosing
        => WarnBeforeClosing(ConfirmCloseWhenServerRunning, ServerIsRunning || PatchServerIsRunning);

    /// <summary>
    /// The rule behind <see cref="ShouldWarnBeforeClosing"/>, stated once so it can be tested without a
    /// window or a running server.
    /// </summary>
    public static bool WarnBeforeClosing(bool askBeforeClosing, bool serverIsRunning)
        => askBeforeClosing && serverIsRunning;

    /// <summary>
    /// What the close prompt says about what is running, so it describes the real state rather than
    /// asking about a server that happens to be idle behind a patch server that is not.
    /// </summary>
    public string CloseWarningDetail {
        get {
            var parts = new List<string>();

            if (ServerIsRunning) {
                parts.Add($"The server is currently {ServerStateText.ToLowerInvariant()}.");
            }

            if (PatchServerIsRunning) {
                parts.Add($"The patch server is currently {PatchStateText.ToLowerInvariant()}.");
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// The persisted "ask before closing" preference. The close dialog's "Don't ask me again" turns it
    /// off; the checkbox in Settings is the only way back on.
    /// </summary>
    public bool ConfirmCloseWhenServerRunning {
        get => _store.Settings.ConfirmCloseWhenServerRunning;
        set {
            if (_store.Settings.ConfirmCloseWhenServerRunning == value) {
                return;
            }

            _store.Settings.ConfirmCloseWhenServerRunning = value;
            _store.Save();

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShouldWarnBeforeClosing));
        }
    }

    // ---------------------------------------------------------------- theme

    /// <summary>The choices the theme picker offers.</summary>
    public string[] Themes { get; } = [ThemeChoices.System, ThemeChoices.Light, ThemeChoices.Dark];

    /// <summary>The pages that may be landed on, in sidebar order, Home first.</summary>
    public string[] LandingPages { get; } = [.. LauncherSections.All];

    /// <summary>
    /// Which page the launcher opens on. Home unless changed, and changed under Settings - never by
    /// simply visiting a page, so a launcher cannot end up opening somewhere the user never chose.
    /// </summary>
    public string DefaultLandingPage {
        get => NormaliseLandingPage(_store.Settings.DefaultSection);
        set {
            var normalised = NormaliseLandingPage(value);
            if (string.Equals(_store.Settings.DefaultSection, normalised, StringComparison.Ordinal)) {
                return;
            }

            _store.Settings.DefaultSection = normalised;
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Reads a stored or chosen page, falling back to Home.</summary>
    public static string NormaliseLandingPage(string? page)
        => LauncherSections.All.FirstOrDefault(
               s => string.Equals(s, page?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? LauncherSections.Home;

    /// <summary>The section a stored name means, or Home when it means nothing the launcher knows.</summary>
    public static LauncherSection ParseSection(string? stored)
        => Enum.TryParse<LauncherSection>(NormaliseLandingPage(stored), ignoreCase: true, out var section)
            ? section
            : LauncherSection.Home;

    /// <summary>
    /// Which theme to use, applied the moment it changes. Nothing on the window pins a variant, so the
    /// palettes are resolved per variant by the framework and follow the desktop when the choice is
    /// <see cref="ThemeChoices.System"/>.
    /// </summary>
    public string Theme {
        get => _store.Settings.Theme;
        set {
            var normalised = NormaliseTheme(value);
            if (string.Equals(_store.Settings.Theme, normalised, StringComparison.Ordinal)) {
                return;
            }

            _store.Settings.Theme = normalised;
            _store.Save();

            OnPropertyChanged();
            ApplyTheme();
        }
    }

    /// <summary>Reads a stored or chosen theme, falling back to following the desktop.</summary>
    public static string NormaliseTheme(string? theme)
        => theme?.Trim() switch {
            var t when string.Equals(t, ThemeChoices.Light, StringComparison.OrdinalIgnoreCase) => ThemeChoices.Light,
            var t when string.Equals(t, ThemeChoices.Dark, StringComparison.OrdinalIgnoreCase) => ThemeChoices.Dark,
            _ => ThemeChoices.System,
        };

    /// <summary>The Avalonia variant a theme choice means. "System" is the framework's default.</summary>
    public static ThemeVariant VariantFor(string? theme)
        => NormaliseTheme(theme) switch {
            ThemeChoices.Light => ThemeVariant.Light,
            ThemeChoices.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

    private void ApplyTheme() {
        if (Application.Current is { } application) {
            application.RequestedThemeVariant = VariantFor(Theme);
        }
    }

    // ---------------------------------------------------------------- verbose mode

    /// <summary>
    /// Whether the launcher shows the explanation under each control.
    /// </summary>
    /// <remarks>
    /// The notes are comments on the screen rather than part of it, so they are hidden by default and the
    /// window carries a <c>verbose</c> class when this is on - see <c>LauncherStyles.axaml</c>. Saved the
    /// moment it is ticked, because it is a way of reading the launcher rather than a change to anything
    /// the launcher does.
    /// </remarks>
    public bool VerboseMode {
        get => _store.Settings.VerboseMode;
        set {
            if (_store.Settings.VerboseMode == value) {
                return;
            }

            _store.Settings.VerboseMode = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- the game's own art

    private readonly GameArtCache _artCache = new();
    private bool _artLoadInFlight;

    /// <summary>The banner scene, cached on disk. Null until it has been fetched and decoded.</summary>
    [ObservableProperty] private Bitmap? _heroArt;

    /// <summary>The wordmark, cached on disk. Null until it has been fetched and decoded.</summary>
    [ObservableProperty] private Bitmap? _logoArt;

    public bool HasHeroArt => HeroArt is not null;

    public bool HasLogoArt => LogoArt is not null;

    /// <summary>Whether the plain wordmark stands in for the game's own logo.</summary>
    public bool UsesWordmarkFallback => !HasLogoArt;

    partial void OnHeroArtChanged(Bitmap? value) {
        OnPropertyChanged(nameof(HasHeroArt));
    }

    partial void OnLogoArtChanged(Bitmap? value) {
        OnPropertyChanged(nameof(HasLogoArt));
        OnPropertyChanged(nameof(UsesWordmarkFallback));
    }

    /// <summary>
    /// Brings the game's art into the local cache and decodes it.
    /// </summary>
    /// <remarks>
    /// Nothing of the game's is shipped with the launcher - see <see cref="GameArtCache"/> - so this is
    /// either a cached file, a download, or nothing at all, in which case Home keeps its plain banner
    /// rather than showing an empty hole. Failing is not an error worth alarming anyone about.
    /// </remarks>
    private async Task LoadGameArtAsync() {
        if (_artLoadInFlight) {
            return;
        }

        _artLoadInFlight = true;

        try {
            var fetched = new List<string>();

            var heroPath = await _artCache
                .LoadAsync(GameArt.Hero, () => fetched.Add("the banner scene"))
                .ConfigureAwait(true);
            var logoPath = await _artCache
                .LoadAsync(GameArt.Logo, () => fetched.Add("the Wizard101 wordmark"))
                .ConfigureAwait(true);

            if (fetched.Count > 0) {
                AppendLog(LogLevel.Info,
                    $"Downloaded and cached {string.Join(" and ", fetched)} from {GameArtCache.UrlFor(GameArt.Hero)}"
                    + " for the Home banner.");
            }

            var hero = await DecodeAsync(heroPath).ConfigureAwait(true);
            var logo = await DecodeAsync(logoPath).ConfigureAwait(true);

            if (hero is null && logo is null) {
                AppendLog(LogLevel.Info,
                    "Home is using its plain banner: the game's art is not cached yet and could not be "
                    + "downloaded.");
            }

            Post(() => {
                HeroArt = hero;
                LogoArt = logo;
            });
        }
        finally {
            _artLoadInFlight = false;
        }
    }

    /// <summary>Decodes a cached file off the UI thread; a broken file is simply no art.</summary>
    /// <summary>Decodes an image file - the cached game art - or gives up quietly.</summary>
    private static async Task<Bitmap?> DecodeAsync(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        try {
            return await Task.Run(() => new Bitmap(path)).ConfigureAwait(true);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Decodes a picture the launcher's own database handed back, or gives up quietly.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a path, because that is what a profile's picture is now: an attachment on the
    /// profile's own document, which is what makes it part of a backup. A picture that will not decode is a
    /// profile showing the initial-letter circle instead, not a screen that fails.
    /// </remarks>
    private static async Task<Bitmap?> DecodeAsync(byte[]? content) {
        if (content is null || content.Length == 0) {
            return null;
        }

        try {
            return await Task.Run(() => new Bitmap(new MemoryStream(content))).ConfigureAwait(true);
        }
        catch (Exception) {
            return null;
        }
    }

    private bool CanStartServer()
        => !IsBusy
           && ServerState is ServerState.Stopped or ServerState.Faulted
           && File.Exists(ImlightConfig.ExecutablePathFor(ServerPath));

    private bool CanStopServer() => CanStopSomething(ServerState, PatchServerIsRunning);

    /// <summary>
    /// The rule behind the Stop button, which now covers both processes the launcher runs.
    /// </summary>
    /// <remarks>
    /// Either can be the only one up: a patch server started on its own from Settings has no server
    /// behind it, and stopping it is still the thing the button is for. Stated as a rule so it can be
    /// tested without either process.
    /// </remarks>
    public static bool CanStopSomething(ServerState server, bool patchServerIsRunning)
        => server is not (ServerState.Stopped or ServerState.Stopping) || patchServerIsRunning;

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartServerAsync() {
        // A new attempt, so last session's account of a file the server could not load is not this one's.
        ForgetPatchServerRefusal();

        // The patch server goes first, because Imlight reads it during its own boot: starting it
        // afterwards would only serve the next session. Play comes through here too, so Play inherits the
        // order - patch server, then server, then client.
        await EnsurePatchServerStartedAsync().ConfigureAwait(true);

        // Imlight is pointed at this machine's patch server when the launcher is running one and it is
        // up, because its config is read during its boot. Anything else - the public patch server, or one
        // somebody runs elsewhere - is left exactly as the config has it.
        ApplyPatchServerToConfig();

        // The warm-up's first request is Root.wad from the patch server, so a patch server that has not
        // fetched it yet is one the server cannot finish warming up against: it asks, gets a 404, and sits
        // there. Waited for before the server is spawned, for that one file and no more, with a way out on
        // Home - see the method. Nothing is repointed and nothing is refused.
        await WaitForPatchServerFilesAsync().ConfigureAwait(true);

        // Still worth saying once the boot file is here: a patch server with Root.wad and little else serves
        // the boot and then fails the client the first time it asks for a zone that has not arrived.
        await WarnIfPatchServerIsStillFetchingAsync().ConfigureAwait(true);

        // The databases are handed over from here, which makes this the last moment a copy of them can be taken:
        // the sync closes both, does its work, and leaves the player's - the server's own database - for Imlight
        // to open. It is the third of the three moments a sync runs, and the one that is asked for rather than
        // waited for: an upload before a session is what makes the cloud's copy as new as this machine's, and a
        // restore before one is the only chance to take the newer copy that is already there.
        //
        // The flag is what keeps the automatic path out of this window - it would take the database back, and
        // start it again, underneath the boot this is standing in the middle of - and it stays up until the
        // server has actually been spawned, which is what covers the config work between here and there.
        _handingOverToServer = true;

        try {
            await RunCloudBackupAsync(manual: false, force: null, handOverToServer: true).ConfigureAwait(true);

            // Before anything is started: the database the launcher has been holding open is the one the server
            // is about to open, and only one process may have it. A sync that just ran has already let go of it,
            // and this is what says so for the times there was nothing to sync.
            await ReleaseIdleDatabaseAsync().ConfigureAwait(true);

            var executable = ImlightConfig.ExecutablePathFor(ServerPath);

            // Repaired here as well as on install, so an existing install is put right by pressing Play
            // instead of by downloading the server again. Imlight dies during boot on a config that still
            // points its patch server at nothing, reporting only an exit code.
            var repair = ServerConfigRepair.Apply(ImlightConfig.ConfigPathFor(ServerPath));
            if (repair.ChangedAnything) {
                LogRepair(repair);
            }

            var config = ImlightConfig.TryLoad(ServerPath);
            var logPath = config?.ResolveLogFilePath(ServerPath)
                          ?? Path.Combine(ServerPath, "logs", "log.txt");

            Phase = LauncherPhase.StartingServer;

            // The port is probed on the configured server address: pinging the address itself would
            // always succeed, whether or not the account server is listening.
            var host = string.IsNullOrWhiteSpace(config?.GameServerIp) ? "127.0.0.1" : config.GameServerIp;
            var port = config?.LoginServerPort ?? 12000;

            try {
                _server.Start(new ServerStartOptions(ServerPath, executable, host, port, logPath));
                NoteAboutServer(LogLevel.Info, "Waiting for the server to finish warming up…");

                // Profiles are fed by this server and by nothing else, so the watch runs exactly as long as it
                // does - and it reads from here on, not from whatever was already in the log.
                StartProfileWatch(logPath);
            }
            catch (Exception ex) {
                NoteAboutServer(LogLevel.Error, $"Could not start the server: {ex.Message}");
                FailureHeadline = "The server could not be started";
                FailureDetail = ex.Message;
                Phase = LauncherPhase.Failed;
            }
        }
        finally {
            // Imlight has been spawned, or the attempt has failed and said so - either way the databases are
            // no longer being handed over, and the automatic sync is allowed to ask again.
            _handingOverToServer = false;
        }

        RefreshDerived();
    }

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServerAsync() {
        // Last look at the player database while it can still answer: an account added during this
        // session is only in there. Bounded, so stopping is never held up by a database that has already
        // stopped responding - and a read that fails keeps the remembered list rather than clearing it.
        await RefreshAccountsAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);

        // Stopping the server ends the session: a game with no server behind it is over, whether or not
        // the client has noticed yet.
        StopProfileWatch();
        EndPlayingSession();
        await _server.StopAsync().ConfigureAwait(true);

        // The patch server is part of what the launcher started, so it goes down with the server rather
        // than being left holding its port until the launcher is closed - unless it still cannot serve
        // this session's revision, which means a first fetch is in progress and stopping it would stop the
        // download that is the only thing that will ever make it useful. Stop in Settings stops it outright.
        var patchServerCanServe = !PatchServerIsRunning || await PatchServerServingAsync().ConfigureAwait(true);

        if (PatchServerStopsWithTheServer(PatchServerIsRunning, patchServerCanServe)) {
            await _patch.StopAsync().ConfigureAwait(true);
        }
        else if (PatchServerIsRunning) {
            AppendLog(LogLevel.Info,
                "The patch server is still fetching the game's files, so it is left running: the fetch only "
                + "advances while it does, and it stops with ViewLight. Stop it in Settings to stop it now.");
        }

        // Stopping on purpose is not a failure, and a server that is not running is not reported at all.
        Phase = LauncherPhase.Idle;
        RefreshDerived();

        // The server has let go of its database, so the launcher picks it up again: profiles stay readable
        // between runs rather than only while the server is up.
        await EnsureIdleDatabaseAsync().ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- client launch

    public string ClientLaunchArguments => _config is not null
        ? ClientLauncher.BuildArguments(_config.GameServerIp, _config.LoginServerPort)
        : "Download the server to determine the launch arguments.";

    private bool CanLaunchClient()
        => ClientExecutablePath is not null && _config is not null && ServerState == ServerState.Ready;

    /// <summary>The address of this launcher's own server, as the server's own config gives it.</summary>
    private ServerEndpoint? LocalEndpoint => _config is null
        ? null
        : new ServerEndpoint(_config.GameServerIp, _config.LoginServerPort);

    /// <summary>
    /// Settings' "Start Wizard101": the game against this launcher's own server, which is the only
    /// server a session key can sign anyone in to.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunchClient))]
    private Task LaunchClientAsync() => LocalEndpoint is { } endpoint
        ? LaunchClientAsync(endpoint, allowAutoLogin: true, steamRequired: false)
        : Task.CompletedTask;

    /// <summary>
    /// Starts the client against an endpoint and follows it.
    /// </summary>
    /// <param name="allowAutoLogin">
    /// Whether to sign in with a session key. Only ever true for this launcher's own server: a key is a
    /// credential the issuing server knows, and no other server does.
    /// </param>
    /// <param name="steamRequired">
    /// Whether the account is a Steam one, which only Wizard101's own server is ever told - a private
    /// Imlight server signs in against its own accounts.
    /// </param>
    private async Task LaunchClientAsync(ServerEndpoint endpoint, bool allowAutoLogin, bool steamRequired) {
        var executable = ClientExecutablePath;
        if (executable is null) {
            return;
        }

        // Clean slate for the game, whatever it is being pointed at: nothing the launcher holds is left
        // open behind it. On this launcher's own server that is already done - starting the server gave the
        // database back - and this call is the one that closes it for a join, where the local database is
        // not wanted at all.
        await ReleaseIdleDatabaseAsync().ConfigureAwait(true);

        // Read before launching, because the answer decides the arguments the client gets.
        var login = allowAutoLogin ? await LoginForLaunchAsync().ConfigureAwait(true) : null;
        var before = _database;

        try {
            // The client runs from bin\, where its PatchConfig.xml lives - see ClientLauncher - and the
            // session follows the process so the launcher can say whether the game is up.
            _client.Launch(executable, endpoint.Host, endpoint.Port, null, login, steamRequired);
            Phase = LauncherPhase.StartingClient;

            // A session begins here: this is the one moment the launcher knows that somebody is playing,
            // and on which account. Starting the game on this machine's own server is a sign-in for the
            // profile it signs in as - one per session, with playtime credited to it while the game runs.
            // Nothing else begins one: not opening the launcher, not switching profiles, and not reading
            // the server's database, which says an account is in the game whether or not it is.
            if (allowAutoLogin && (SelectedAccount?.AccountId ?? _store.Settings.AutoLoginAccountId) is { } playingAs) {
                Announce(_ledger.BeginSession(playingAs, DateTimeOffset.Now), null);
                RefreshProfiles();
            }

            AppendLog(LogLevel.Info,
                $"Started {Path.GetFileName(executable)} {DescribeLaunchArguments(endpoint, login, steamRequired)}"
                + $" from {ClientLauncher.WorkingDirectoryFor(executable)}.");

            if (login is not null) {
                AppendLog(LogLevel.Success, $"Signing in as {SelectedAccount?.Username ?? login.AccountId.ToString()} - no login screen.");
            }
            else if (allowAutoLogin) {
                // A sign-in from the client's own login screen is what teaches the launcher this
                // machine's id, which is what it needs before it can sign in by itself.
                WatchForMachineId(login, before);
            }
            else {
                AppendLog(LogLevel.Info, steamRequired
                    ? $"{endpoint} is Wizard101's own server, signed in to with a Steam account: Wizard101 will ask for it."
                    : $"{endpoint} is not this launcher's server, so Wizard101 will ask you to sign in.");
            }
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Could not start Wizard101: {ex.Message}");
            FailureHeadline = "Wizard101 could not be started";
            FailureDetail = ex.Message;
            Phase = LauncherPhase.Failed;
        }

        RefreshDerived();
    }

    /// <summary>
    /// The arguments as one line for the Log. The session key is a credential, so it is reported by its
    /// account id with the key left out.
    /// </summary>
    private static string DescribeLaunchArguments(ServerEndpoint endpoint, ClientLogin? login, bool steamRequired) {
        var server = ClientLauncher.BuildArguments(endpoint.Host, endpoint.Port, null, steamRequired);

        return login is null
            ? server
            : $"{server} -U ..{login.AccountId.ToString(CultureInfo.InvariantCulture)} (session key withheld)";
    }

    /// <summary>
    /// The account to sign in as, or null when Wizard101 has to show its login screen.
    /// </summary>
    /// <remarks>
    /// The client is never handed a password: it is handed a session key the server already knows, so
    /// nothing that could be replayed anywhere else leaves the launcher. A key that is missing, or
    /// belongs to another machine, or has expired is replaced with one this launcher issues for this
    /// machine - which it can only do once it knows this machine's id.
    /// </remarks>
    private async Task<ClientLogin?> LoginForLaunchAsync() {
        if (_config is null) {
            return null;
        }

        var accountId = SelectedAccount?.AccountId ?? _store.Settings.AutoLoginAccountId;
        if (accountId is null) {
            return null;
        }

        // The server says it is ready before its embedded database necessarily starts answering, so a
        // refusal here is waited out rather than taken as a database the launcher cannot read. Arriving
        // a second early must not be what turns automatic sign-in off.
        var contents = await _playerDatabase
            .ReadWhenAnsweringAsync(
                _config.EmbeddedDatabasePort, AdminUsername, AdminPassword, TimeSpan.FromSeconds(8))
            .ConfigureAwait(true);

        _database = contents;

        if (!contents.IsTrustworthy) {
            AppendLog(LogLevel.Warning,
                $"Signing in automatically is off: {contents.Problem} Wizard101 will ask you to sign in.");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var machineId = _store.Settings.MachineId;

        var login = AutoLogin.Choose(contents, accountId.Value, machineId, now);
        if (login is not null) {
            return login;
        }

        if (machineId is null) {
            return null;
        }

        var issued = await _playerDatabase
            .EnsureSessionKeyAsync(_config.EmbeddedDatabasePort, contents, accountId.Value, machineId.Value)
            .ConfigureAwait(true);

        if (issued is null) {
            AppendLog(LogLevel.Warning,
                "A session key for this machine could not be arranged, so Wizard101 will ask you to sign in.");
            return null;
        }

        _database = await _playerDatabase
            .ReadAsync(_config.EmbeddedDatabasePort, AdminUsername, AdminPassword)
            .ConfigureAwait(true);

        return new ClientLogin(accountId.Value, issued);
    }

    /// <summary>
    /// Learns this machine's id, which is the one thing the launcher cannot work out for itself.
    /// </summary>
    /// <remarks>
    /// A session key that appears - or changes - while the player is signing in was written by a sign-in
    /// on this machine, and it carries the machine id the server recorded. That is the only honest way
    /// to learn it; a key that has not moved says nothing, and is left alone rather than guessed at.
    /// </remarks>
    private void WatchForMachineId(ClientLogin? login, PlayerDatabaseContents? before) {
        if (_config is null || login is not null || before is null || _store.Settings.MachineId is not null) {
            return;
        }

        var accountId = SelectedAccount?.AccountId ?? _store.Settings.AutoLoginAccountId;
        if (accountId is null) {
            return;
        }

        _machineIdWatch?.Cancel();
        var watch = new CancellationTokenSource();
        _machineIdWatch = watch;
        var port = _config.EmbeddedDatabasePort;
        var username = AdminUsername;
        var password = AdminPassword;

        _ = Task.Run(async () => {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);

            while (!watch.IsCancellationRequested && DateTimeOffset.UtcNow < deadline) {
                try {
                    await Task.Delay(TimeSpan.FromSeconds(5), watch.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    return;
                }

                var after = await _playerDatabase.ReadAsync(port, username, password, watch.Token)
                    .ConfigureAwait(false);

                var machineId = AutoLogin.MachineIdFrom(before, after, accountId.Value);
                if (machineId is null) {
                    continue;
                }

                Post(() => {
                    _store.Settings.MachineId = machineId;
                    _store.Save();
                    AppendLog(LogLevel.Success,
                        $"Learned this machine's server id ({machineId}), so Play can sign in without the login screen.");
                    RefreshDerived();
                });

                return;
            }
        }, watch.Token);
    }

    /// <summary>
    /// Fills the account picker from the last read of the player database, so a launcher that has just
    /// started shows the account it will sign in as rather than an empty box.
    /// </summary>
    /// <remarks>
    /// Nothing beyond the name and id is trusted from it: signing in still re-reads the database, because
    /// only a live read says whether the remembered session key is still good. If the account that was
    /// remembered is no longer in the list, the picker falls back to one that is - the same choice a live
    /// read would make.
    /// </remarks>
    private void SeedAccountsFromCache() {
        if (Accounts.Count > 0) {
            return;
        }

        foreach (var account in RememberedAccounts
                     .OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)) {
            Accounts.Add(account);
        }

        if (Accounts.Count == 0) {
            return;
        }

        _accountsAreRemembered = true;
        SelectedAccount = Accounts.FirstOrDefault(a => a.AccountId == _store.Settings.AutoLoginAccountId)
                          ?? Accounts.FirstOrDefault();
    }

    /// <summary>
    /// Reads the player database and fills the account picker from it.
    /// </summary>
    /// <param name="timeout">
    /// How long the read may take, for callers that must not be held up by a database that has stopped
    /// answering. Running out of time is reported as an unreadable database rather than thrown.
    /// </param>
    private async Task RefreshAccountsAsync(TimeSpan? timeout = null) {
        if (_config is null) {
            Accounts.Clear();
            _database = null;
            _accountsAreRemembered = false;
            RefreshDerived();
            return;
        }

        using var deadline = timeout is { } limit ? new CancellationTokenSource(limit) : null;

        var wanted = SelectedAccount?.AccountId ?? _store.Settings.AutoLoginAccountId;
        var contents = await _playerDatabase
            .ReadWhenAnsweringAsync(
                _config.EmbeddedDatabasePort,
                AdminUsername,
                AdminPassword,
                timeout ?? TimeSpan.FromSeconds(3),
                deadline?.Token ?? default)
            .ConfigureAwait(true);

        // Imlight creates the admin account once and never applies a later password change to it, so the
        // password the config names is put on the account here - from a recognised database, which is the
        // only place any write into it may start from. This is what makes changing the password in Settings
        // mean anything at all: without it the account keeps the one it was created with, and the game
        // refuses the sign-in the screen shows.
        if (contents.AdminPassword is { } drift) {
            var refused = await _playerDatabase
                .AlignAdminPasswordAsync(_config.EmbeddedDatabasePort, contents, AdminPassword,
                    deadline?.Token ?? default)
                .ConfigureAwait(true);

            if (refused is null) {
                AdminPasswordNotice =
                    $"The admin account “{drift.Username}” in the database was created with another password "
                    + "(Imlight only ever creates it), so ViewLight has put the one above on it.";

                AppendLog(LogLevel.Success,
                    $"The admin account “{drift.Username}” was created with a different password; the password "
                    + "set in Imlight.ini has been put on it, so signing in as it in the game now uses that one.");

                // Read again, so accounts and profiles below are the database as it is now rather than as
                // it was a moment ago.
                contents = await _playerDatabase
                    .ReadAsync(_config.EmbeddedDatabasePort, AdminUsername, AdminPassword, deadline?.Token ?? default)
                    .ConfigureAwait(true);
            }
            else {
                AppendLog(LogLevel.Warning,
                    $"The password set in Imlight.ini could not be put on the admin account “{drift.Username}”: "
                    + $"{refused}. The account keeps the password it was created with.");
            }
        }

        _database = contents;

        // Written down as it is read, so the next launcher start has it. An unreadable read is not
        // evidence about the accounts, so on that path the list already stored stands rather than being
        // replaced with nothing - an empty picker is exactly what looks like forgetting.
        var remembered = AccountsToRemember(contents, _store.Settings.CachedAccounts);

        if (!contents.IsTrustworthy) {
            AppendLog(LogLevel.Warning, $"The player database was not read as expected: {contents.Problem}");
            RefreshDerived();
            return;
        }

        _store.Settings.CachedAccounts = [.. remembered];

        // Which server they are the accounts *of*, so the memory is not shown against a server this
        // launcher was later pointed at instead. Kept in the same form the folders themselves are - relative
        // to a portable root - so carrying that folder to another drive does not invalidate it.
        _store.Settings.CachedAccountsServerPath = LauncherPaths.ToStoredPath(ServerPath);
        _store.Save();

        Accounts.Clear();
        foreach (var account in contents.Accounts.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)) {
            Accounts.Add(account);
        }

        // A live read replaces the remembered one, so the picker is no longer showing a memory.
        _accountsAreRemembered = false;

        // The account the player picked last, if it is still there; otherwise the only one, so Play has
        // something to sign in as rather than asking again.
        SelectedAccount = Accounts.FirstOrDefault(a => a.AccountId == wanted) ?? Accounts.FirstOrDefault();

        // A profile exists for every account the launcher knows about, so one is there to be picked before
        // anything has been played on it.
        _ledger.NoteAccounts(Accounts);
        RefreshProfiles();

        AppendLog(LogLevel.Info,
            $"Player database read: {Accounts.Count} account(s), {contents.SessionKeys.Count} session key(s).");

        RefreshDerived();
    }

    /// <summary>
    /// The remembered accounts, or nothing at all when that memory is not this server's.
    /// </summary>
    private IReadOnlyList<PlayerAccount> RememberedAccounts
        => RememberedAccountsAreUsable(
            LauncherPaths.ToStoredPath(ServerPath),
            _store.Settings.CachedAccountsServerPath,
            _config?.AdminUsername,
            _config?.AdminPassword)
                ? _store.Settings.CachedAccounts
                : [];

    /// <summary>
    /// Whether accounts remembered from an earlier read may be put on the screen at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things have to hold, and the second is the one that used to be missed.
    /// </para>
    /// <para>
    /// The remembered list has to belong to the server this launcher is pointed at now. Accounts live in a
    /// server's own player database, so a name read from a different server folder is a name <i>this</i>
    /// server has never heard of: pointing the launcher elsewhere must not present somebody else's accounts
    /// as if they existed here. The server folder is therefore part of what is remembered - in the same
    /// stored form as the folder setting itself, so a portable folder that moved still matches - and a list
    /// written before that was kept is simply not shown again.
    /// </para>
    /// <para>
    /// And there has to be a server here that names the admin account it creates on boot. Until it does,
    /// no account exists at all - an unconfigured server has never made one - so the honest answer is that
    /// there is no profile rather than a name from somewhere else.
    /// </para>
    /// </remarks>
    public static bool RememberedAccountsAreUsable(
        string? serverPath, string? rememberedFor, string? adminUsername, string? adminPassword)
        => !string.IsNullOrWhiteSpace(adminUsername)
           && !string.IsNullOrWhiteSpace(adminPassword)
           && IsSameFolder(rememberedFor, serverPath);

    /// <summary>
    /// Whether two paths name the same folder, allowing for case and a stray trailing separator.
    /// </summary>
    private static bool IsSameFolder(string? one, string? other) {
        if (string.IsNullOrWhiteSpace(one) || string.IsNullOrWhiteSpace(other)) {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(one.Trim()),
            Path.TrimEndingDirectorySeparator(other.Trim()),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The accounts to remember after a read. A trustworthy read is the truth, even when it says there are
    /// none; an unreadable one is not evidence about anything, so the last known list is kept.
    /// </summary>
    public static IReadOnlyList<PlayerAccount> AccountsToRemember(
        PlayerDatabaseContents contents, IReadOnlyList<PlayerAccount> remembered)
        => contents.IsTrustworthy ? contents.Accounts : remembered;

    /// <summary>
    /// Play: start the server, wait until it is genuinely ready, then start the client.
    /// </summary>
    /// <remarks>
    /// The user is told what is happening as it happens and never has to start the server themselves -
    /// which is the whole point of the button. Failures are reported where the user is looking rather
    /// than only in the Log, because a Play button that does nothing is the worst possible answer.
    /// </remarks>
    private async Task PlayAsync() {
        if (GameClientState is ClientState.Starting or ClientState.Running) {
            AppendLog(LogLevel.Info, "Wizard101 is already running.");
            Phase = GameClientState == ClientState.Running
                ? LauncherPhase.Playing
                : LauncherPhase.StartingClient;
            return;
        }

        // Checked up front so the user is not left waiting for a server they cannot use.
        if (ClientExecutablePath is null) {
            FailureHeadline = "Wizard101 was not found";
            FailureDetail = $"Expected {GameFolderLocator.RelativeClientExecutable} inside the game folder. "
                           + "Set the game folder under Settings.";
            Phase = LauncherPhase.Failed;
            AppendLog(LogLevel.Error,
                $"The game client was not found. Expected {GameFolderLocator.RelativeClientExecutable} "
                + "inside the game folder set in Settings.");
            return;
        }

        // Play means this launcher's own server. Joining somewhere else is the dropdown's job, so a
        // plain Play always comes back here.
        PlayServer = PlayServerKind.Local;
        JoinedEndpoint = null;

        _playInFlight = true;
        try {
            if (ServerState != ServerState.Ready) {
                if (!CanStartServer() && ServerState is ServerState.Stopped or ServerState.Faulted) {
                    FailureHeadline = "The server could not be started";
                    FailureDetail = $"Expected {ImlightConfig.ServerExecutableName} in the server folder. "
                                   + "Download the server under Settings.";
                    Phase = LauncherPhase.Failed;
                    AppendLog(LogLevel.Error,
                        $"The server could not be started. Expected {ImlightConfig.ServerExecutableName} "
                        + "in the server folder.");
                    return;
                }

                if (ServerState is ServerState.Stopped or ServerState.Faulted) {
                    await StartServerAsync().ConfigureAwait(true);
                }
            }
            else {
                NoteAboutServer(LogLevel.Info, "The server is already running, so the game is starting.");
            }

            // Wait for readiness without blocking the UI thread. Time spent waiting for the patch server's
            // files is added back on, because that is not the server being slow - it had not been started
            // yet, and counting it here would time out a session that was waiting on a download rather than
            // on Imlight.
            var deadline = DateTimeOffset.Now.AddMinutes(10) + _waitedForPatchServerFiles;
            while (ServerState != ServerState.Ready
                   && ServerState is not (ServerState.Faulted or ServerState.Stopped)
                   && DateTimeOffset.Now < deadline) {
                await Task.Delay(500).ConfigureAwait(true);
            }

            if (ServerState != ServerState.Ready) {
                FailureHeadline = "The server did not come up";
                FailureDetail = "It never reported ready, so the game was not started. "
                                + "The Log has the last thing it said.";
                Phase = LauncherPhase.Failed;
                NoteAboutServer(LogLevel.Error, "The server never reported ready, so the game was not started.");
                return;
            }

            await LaunchClientAsync().ConfigureAwait(true);
        }
        finally {
            _playInFlight = false;
            RefreshDerived();
        }
    }

    // ---------------------------------------------------------------- derived state

    public void RefreshDerived() {
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsProfileSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));

        // What the profile's two views hang their visibility on, which depends on the section exactly as
        // the selected classes above do: a section change that says so only for the nav is a profile tab
        // that comes up empty, because neither of its views is ever told to show itself.
        OnPropertyChanged(nameof(ShowProfilePage));
        OnPropertyChanged(nameof(ShowAchievementsPage));


        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(RequiresSetup));
        OnPropertyChanged(nameof(RequiresImlight));
        OnPropertyChanged(nameof(RequiresPatchServerChoice));
        OnPropertyChanged(nameof(ShowMainInterface));
        OnPropertyChanged(nameof(CanContinueSetup));
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(ServerIsRunning));
        OnPropertyChanged(nameof(IsServerWorking));
        OnPropertyChanged(nameof(IsServerReady));
        OnPropertyChanged(nameof(IsServerFaulted));
        OnPropertyChanged(nameof(ShouldWarnBeforeClosing));
        OnPropertyChanged(nameof(CloseWarningDetail));
        OnPropertyChanged(nameof(CanStartServer));
        OnPropertyChanged(nameof(CanStopServer));
        OnPropertyChanged(nameof(CanLaunchClient));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanPrimaryAction));
        OnPropertyChanged(nameof(StarterKitSummary));
        OnPropertyChanged(nameof(StarterKitRequirement));
        OnPropertyChanged(nameof(IsStarterKitOverLimit));
        OnPropertyChanged(nameof(CanRemoveStarterItem));
        OnPropertyChanged(nameof(CanAddStarterItem));
        OnPropertyChanged(nameof(HasStarterKitError));
        OnPropertyChanged(nameof(ClientLaunchArguments));
        OnPropertyChanged(nameof(InstalledTag));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(InstalledReleaseText));
        OnPropertyChanged(nameof(HomeHint));
        OnPropertyChanged(nameof(LoginHint));
        OnPropertyChanged(nameof(HasLoginHint));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(AutoLoginSummary));

        // The account a server starts with comes from the config, so it changes when the config does.
        OnPropertyChanged(nameof(StarterAccountSummary));
        OnPropertyChanged(nameof(StarterAccountTitle));
        OnPropertyChanged(nameof(StarterAccountUsername));
        OnPropertyChanged(nameof(StarterAccountInitials));
        OnPropertyChanged(nameof(HasStarterAccount));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(ServerChannel));
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPrereleaseChannel));
        OnPropertyChanged(nameof(IsBleedingEdgeChannel));
        OnPropertyChanged(nameof(IsBleedingEdge));
        OnPropertyChanged(nameof(ChannelNote));
        OnPropertyChanged(nameof(BleedingEdgeWarning));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(InstallImlightLabel));
        OnPropertyChanged(nameof(InstallDescription));
        OnPropertyChanged(nameof(EdgeTypeDumpPath));
        OnPropertyChanged(nameof(EdgeTypeDumpNote));
        OnPropertyChanged(nameof(ServerBuildText));
        OnPropertyChanged(nameof(ClientBuild));
        OnPropertyChanged(nameof(ClientBuildText));
        OnPropertyChanged(nameof(HasClientBuild));
        OnPropertyChanged(nameof(ClientBuildMissingHint));
        OnPropertyChanged(nameof(ExpectedClientBuild));
        OnPropertyChanged(nameof(ClientBuildMismatch));
        OnPropertyChanged(nameof(HasClientBuildMismatch));
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsStatusVisible));
        OnPropertyChanged(nameof(IsStatusWorking));
        OnPropertyChanged(nameof(IsStatusGood));
        OnPropertyChanged(nameof(IsStatusFailed));
        OnPropertyChanged(nameof(GameClientState));
        OnPropertyChanged(nameof(ClientStateText));
        OnPropertyChanged(nameof(ClientSentence));
        OnPropertyChanged(nameof(ClientIsStarting));
        OnPropertyChanged(nameof(ClientIsRunning));
        OnPropertyChanged(nameof(ClientIsClosed));
        OnPropertyChanged(nameof(HasReleaseCheckError));
        OnPropertyChanged(nameof(ClientExecutablePath));
        OnPropertyChanged(nameof(IsRemoteSession));
        OnPropertyChanged(nameof(IsOfficialServer));
        OnPropertyChanged(nameof(ServerRowLabel));
        OnPropertyChanged(nameof(ServerRowState));
        OnPropertyChanged(nameof(IsServerRowReady));
        OnPropertyChanged(nameof(HasJoinStatus));
        OnPropertyChanged(nameof(HasDiscoveredServers));
        OnPropertyChanged(nameof(AccountsAreRemembered));
        OnPropertyChanged(nameof(IsPortable));

        // A full reset is offered only while both databases can be deleted, and what moves that is the
        // server and the cloud sync rather than anything else on this list - see the reset partial.
        OnPropertyChanged(nameof(CanResetNow));
        OnPropertyChanged(nameof(IsResetBlocked));
        OnPropertyChanged(nameof(ResetBlockedText));
        OnPropertyChanged(nameof(CanWipeCloudCopy));
        OnPropertyChanged(nameof(IsEditingAllowed));
        OnPropertyChanged(nameof(LauncherRoot));
        OnPropertyChanged(nameof(PortableGameFolder));
        OnPropertyChanged(nameof(PortableNeedsClient));
        OnPropertyChanged(nameof(ShowPortableClientPrompt));
        OnPropertyChanged(nameof(PortableClientCardTitle));
        OnPropertyChanged(nameof(ShowPortableClientCard));
        OnPropertyChanged(nameof(HasPortableSteamWarning));
        OnPropertyChanged(nameof(PortableSteamWarning));
        OnPropertyChanged(nameof(HasCopyGameMessage));
        OnPropertyChanged(nameof(PortableMarkerOn));
        OnPropertyChanged(nameof(PortableNeedsRestart));
        OnPropertyChanged(nameof(PortableRunText));
        OnPropertyChanged(nameof(PortableToggleLabel));
        OnPropertyChanged(nameof(PortableRestartNote));

        // Everything the profile screen shows is derived from the selected profile, so it is refreshed from
        // the same place as the rest.
        RefreshProfileDerived();

        // And so is everything the patch server's rows, state and buttons show.
        RefreshPatchDerived();

        StartServerCommand.NotifyCanExecuteChanged();
        CopyGameIntoPortableCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
        LaunchClientCommand.NotifyCanExecuteChanged();
        InstallOrUpdateCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        PrimaryActionCommand.NotifyCanExecuteChanged();
        InstallImlightCommand.NotifyCanExecuteChanged();
        CheckImlightAgainCommand.NotifyCanExecuteChanged();
        CompleteSetupCommand.NotifyCanExecuteChanged();
        RemoveSelectedItemFromKitCommand.NotifyCanExecuteChanged();
    }

    public void Shutdown() {
        _checkCancellation?.Cancel();
        _checkCancellation = null;

        // Before the watch goes: whatever the last look saw of a session that is still running is added up,
        // so closing the launcher mid-game does not leave the last minute of play uncounted.
        EndPlayingSession();
        StopProfileWatch();

        // Closing the launcher stops the whole stack it started: the patch server and the private server
        // both go with it, rather than being left behind to hold their ports.
        _patch.Dispose();
        _patchInstallCancellation?.Cancel();
        _patchInstallCancellation?.Dispose();

        // The database is let go before the launcher goes: it is the server's, and a held lock would stop
        // Imlight starting next time.
        _raven.Dispose();

        // And the launcher's own database with it - its client first, so nothing is midway through a read
        // when the database it is reading goes. Nothing is waiting on this one, but a database left running
        // would hold its data directory against the next launcher - and against a backup.
        CloseLauncherDatabaseClient();
        _launcherDatabase.Dispose();
        _machineIdWatch?.Cancel();
        _machineIdWatch?.Dispose();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _copyGameCancellation?.Cancel();
        _copyGameCancellation?.Dispose();
        _playerDatabase.Dispose();
        _client.Dispose();
        _server.Dispose();
    }

    public string AppVersion =>
        typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string LauncherDataRoot => LauncherPaths.DataRoot;
}
