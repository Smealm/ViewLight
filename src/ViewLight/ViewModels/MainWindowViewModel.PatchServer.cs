using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The local patch server: Aurorium, the file server Imlight loads <c>Root.wad</c> and the world data
/// from.
/// </summary>
/// <remarks>
/// <para>
/// It is the missing piece of an otherwise local stack. Imlight and the client can both live on this
/// machine, but Imlight still asks a <i>patch server</i> for the game's own files, and the address it
/// ships with is the community's - so a session without one leaves the machine for every asset it does
/// not have cached. Running Aurorium here, and pointing Imlight at it, is what makes the whole thing
/// local.
/// </para>
/// <para>
/// <b>It starts first, and stops last.</b> Imlight reads the patch server during its own boot, so the
/// patch server is started before the server and waited for; the client is started last, as always. On
/// the way out all three go together, because closing the launcher stops the stack it started.
/// </para>
/// <para>
/// <b>A patch server failing is not a failed session.</b> Imlight falls back to its own cached copy of
/// <c>Root.wad</c>, so a patch server that will not start is reported - in the Log, on Home and in
/// Settings - and the server is started anyway rather than being refused.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    private readonly PatchServerReleaseService _patchReleaseService = new();
    private readonly PatchServerInstallService _patchInstallService = new();
    private readonly PatchServerRunner _patch = new();
    private readonly PatchServerRevisions _patchRevisions = new();
    private readonly PatchServerServing _patchServing = new();
    private readonly PatchServerPreload _patchPreload = new();

    /// <summary>What a preload would do, as it was last worked out; null until it has been looked at.</summary>
    private PatchServerPreloadPlan? _preloadPlan;

    private CancellationTokenSource? _preloadCancellation;

    /// <summary>How fast the patch server's own fetch is going, worked out from what has landed.</summary>
    private readonly PatchServerFetchPace _fetchPace = new();

    private CancellationTokenSource? _fetchWatchCancellation;

    /// <summary>The file the fetch was last reported as being on, so a line is written when it moves on.</summary>
    private string? _fetchFileReported;

    /// <summary>The revision the fetch was last reported as being on, for the same reason.</summary>
    private string? _fetchRevisionReported;

    /// <summary>Whether the mismatch between the two revisions has already been said in the Log.</summary>
    private bool _fetchMismatchReported;

    /// <summary>When the fetch was last written into the Log, so a long download is not a wall of lines.</summary>
    private DateTimeOffset _fetchLoggedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// How often a running fetch is described in the Log, when nothing about it has changed.
    /// </summary>
    /// <remarks>
    /// Once a minute, and on every change besides: the figures move constantly, and a line per sample would
    /// bury everything else the launcher has to say about a session that is mostly an hours-long download.
    /// </remarks>
    private static readonly TimeSpan FetchLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether the running patch server could answer for the revision <c>GameRevision</c> asks for the
    /// last time it was asked, or <c>null</c> while it has not been asked.
    /// </summary>
    /// <remarks>
    /// Kept only so stopping can tell a patch server that is done from one that is still fetching, which
    /// is the difference between stopping it and stopping the thing that is working on its behalf.
    /// </remarks>
    private bool? _patchServerCanServe;

    private PatchServerRelease? _latestPatchRelease;
    private CancellationTokenSource? _patchInstallCancellation;

    /// <summary>How long to wait for the patch server to answer before starting without it.</summary>
    private static readonly TimeSpan PatchServerStartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the revision is counted while the patch server is fetching, and how often the two ways of
    /// working out what to copy are looked at again.
    /// </summary>
    /// <remarks>
    /// Five seconds: fine enough that the figure moves while a large file is being written, coarse enough
    /// that walking a revision of thousands of files is a rounding error next to the download itself.
    /// </remarks>
    private static readonly TimeSpan FetchSampleInterval = TimeSpan.FromSeconds(5);

    // ---------------------------------------------------------------- state

    /// <summary>Folder the patch server is installed in.</summary>
    [ObservableProperty] private string _patchServerPath = string.Empty;

    /// <summary>What the patch server is doing, in the same five states the server uses.</summary>
    [ObservableProperty] private ServerState _patchServerState = ServerState.Stopped;

    [ObservableProperty] private string _patchFolderStatus = string.Empty;
    [ObservableProperty] private string _patchInstalledText = "Not downloaded";
    [ObservableProperty] private string _latestPatchReleaseText = "Not checked yet.";
    [ObservableProperty] private string _patchCheckError = string.Empty;
    [ObservableProperty] private bool _isPatchBusy;
    [ObservableProperty] private double _patchInstallProgress;
    [ObservableProperty] private string _patchInstallStage = "Idle";

    public bool HasPatchCheckError => !string.IsNullOrEmpty(PatchCheckError);

    /// <summary>True when there is an <c>aurorium.exe</c> in the configured folder.</summary>
    public bool PatchServerInstalled
        => !string.IsNullOrWhiteSpace(PatchServerPath)
           && File.Exists(PatchServerRunner.ExecutablePathFor(PatchServerPath));

    public bool PatchServerIsRunning => _patch.IsRunning;

    public bool IsPatchWorking => PatchServerState is ServerState.Starting or ServerState.WarmingUp
        or ServerState.Stopping;

    public bool IsPatchReady => PatchServerState == ServerState.Ready;

    public bool IsPatchFaulted => PatchServerState == ServerState.Faulted;

    /// <summary>The patch server in one word, for the row beside its status dot.</summary>
    public string PatchStateText => DescribeServerState(PatchServerState);

    /// <summary>
    /// Show a row for it on Home while it is the patch server this stack uses, and not otherwise: a
    /// launcher pointed at somebody else's patch server has nothing to say about one of its own, and one
    /// left for the user to start is not part of what Play does.
    /// </summary>
    public bool ShowPatchServerRow => PatchServerIsThisMachines && PatchServerManaged;

    public string PatchServerRowLabel => "Patch server";

    /// <summary>
    /// What Home's patch server row says: the process, and nothing more. What it holds is the patch
    /// server's business - a session points at the address it was given and finds out there - so the row
    /// does not invent a second opinion about it.
    /// </summary>
    public string PatchServerRowState => PatchServerInstalled ? PatchStateText : "Not installed";

    /// <summary>The address Imlight is pointed at for this patch server.</summary>
    public string LocalPatchServerUrl => PatchServerConfig.EndpointUrl(PatchServerPort);

    /// <summary>
    /// What the mirror is doing about <c>GameRevision</c>, for the line under the box that shows it, or
    /// empty while the mirror is off.
    /// </summary>
    [ObservableProperty] private string _patchServerRevisionStatus = string.Empty;

    public bool HasPatchServerRevisionStatus => PatchServerRevisionStatus.Length > 0;

    /// <summary>
    /// Whether the <c>GameRevision</c> box in Settings can be typed in. It can while the sync is off, which
    /// is when the revision is the server's own and nothing but the user moves it. While the sync is on the
    /// box is dead, because the value is the patch server's rather than the user's, and a box that takes
    /// edits it is about to overwrite is worse than one that says so.
    /// </summary>
    public bool CanEditGameRevision => !PatchServerRevisionSynced;

    /// <summary>
    /// The port Imlight's <b>own</b> patch server listens on, read from its config.
    /// </summary>
    [ObservableProperty] private int _imlightPatchServerPort = ImlightConfig.DefaultPatchServerPort;

    /// <summary>
    /// True when the launcher's patch server is set to the port Imlight's own patch server listens on.
    /// </summary>
    /// <remarks>
    /// Two listeners cannot share a port, and the loser dies during boot:
    /// <c>TcpListener for Imlight.Patch on port 12500 failed: SocketException (10048)</c>. It is worth
    /// catching in the launcher because it is entirely invisible from the game - the server simply stops.
    /// Imlight's own listener is not the patch server it downloads from; it is the one the client talks
    /// to, which relays what was downloaded.
    /// </remarks>
    public bool PatchServerPortClashes => PatchServerPort == ImlightPatchServerPort;

    public string PatchServerPortClashWarning => $"Imlight's own patch server, the one the client talks "
        + $"to, listens on port {ImlightPatchServerPort} (PatchServerPort in [Patch Server]). Two servers "
        + "cannot share a port and the launcher's starts first, so Imlight would stop during boot with "
        + "'TcpListener for Imlight.Patch failed'. Pick another port here.";

    /// <summary>Whether anything the launcher started is still running.</summary>
    public bool AnythingIsRunning => ServerIsRunning || PatchServerIsRunning;

    /// <summary>
    /// What the Stop button on Home says. Plural while the patch server is up too, because the one press
    /// stops both - except while it is still fetching its files, when the press stops the server and leaves
    /// the download running. A button that promised to stop something it does not stop is worse than one
    /// that says which of the two it means.
    /// </summary>
    public string StopButtonLabel => PatchServerIsRunning && _patchServerCanServe != false
        ? "Stop the servers"
        : "Stop the server";

    /// <summary>Whether a newer patch server build is published than the one installed.</summary>
    public bool IsPatchUpdateAvailable => _latestPatchRelease is not null
                                          && PatchServerInstalled
                                          && !string.Equals(_store.Settings.PatchServerInstalledTag,
                                              _latestPatchRelease.Tag, StringComparison.Ordinal);

    // ---------------------------------------------------------------- what it has fetched so far

    /// <summary>
    /// What the patch server has of the revision it holds, in one line: how much of the revision is on disk
    /// and how long the rest should take at the rate it is going.
    /// </summary>
    /// <remarks>
    /// Empty until the patch server is running with a revision to count. It is the launcher <i>watching</i>
    /// the patch server fetch, which is the workflow the patch server was built for - nothing is written
    /// into its folders to hurry it along, and nothing else is asked of it: the figure comes from the
    /// revision's own file list against the files that are there.
    /// </remarks>
    [ObservableProperty] private string _fetchStatus = string.Empty;

    public bool HasFetchStatus => FetchStatus.Length > 0;

    /// <summary>How far along that fetch is, 0..100.</summary>
    [ObservableProperty] private double _fetchProgress;

    /// <summary>
    /// Whether the revision the patch server holds is still arriving.
    /// </summary>
    /// <remarks>
    /// The patch server's own state says nothing about this: it is <c>Running</c> from the moment its
    /// endpoint answers, which is before the first asset is asked for. This is what the second line on
    /// screen hangs off, and it is why that line can say more than the row above it does.
    /// </remarks>
    [ObservableProperty] private bool _isFetchingGameFiles;

    /// <summary>
    /// Whether the revision the patch server is fetching is the one the server asks it for.
    /// </summary>
    /// <remarks>
    /// The question the whole arrangement turns on, and one the launcher can answer for itself: the revision
    /// the patch server lists against the revision in Imlight's own config. False means a session that will
    /// get a <c>404</c> for its files whatever else is right - so it is worth a line on its own rather than
    /// being left inside a sentence about how the download is going.
    /// </remarks>
    [ObservableProperty] private bool _fetchingRevisionIsTheServers = true;

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Whether the patch server in use is this machine's own, rather than one somebody else runs.
    /// </summary>
    /// <remarks>
    /// The question Settings leads with, and it is not "use a patch server or not": Imlight cannot boot
    /// without one, so the only thing being chosen is <i>whose</i>. It is stated as a pair of properties -
    /// this one and <see cref="UsesRemotePatchServer"/> - so the two options read as a choice rather than as
    /// a switch with an off state, and both write the same setting.
    /// </remarks>
    public bool UseLocalPatchServer {
        get => _store.Settings.UseLocalPatchServer;
        set {
            if (_store.Settings.UseLocalPatchServer == value) {
                return;
            }

            _store.Settings.UseLocalPatchServer = value;
            _store.Save();

            AppendLog(LogLevel.Info, value
                ? $"Imlight's patch server is this machine's from now on: Play starts it and points Imlight "
                  + $"at {LocalPatchServerUrl}."
                : "Imlight's patch server is a remote one from now on: this machine's is left alone, and the "
                  + "address in the box is the one Imlight keeps.");

            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesRemotePatchServer));
            RefreshDerived();
        }
    }

    /// <summary>The same answer from the other side, for the radio button that offers it.</summary>
    public bool UsesRemotePatchServer {
        get => !UseLocalPatchServer;
        set => UseLocalPatchServer = !value;
    }

    /// <summary>
    /// Whether this machine's patch server is the one a session is going to ask - the choice above, and a
    /// patch server actually installed to answer with.
    /// </summary>
    /// <remarks>
    /// One property rather than that pair of conditions repeated, because everything about a local patch
    /// server hangs off the same answer: whether Imlight is pointed at it, whether Play starts it, whether
    /// Home shows it, and whether a session's files may be copied into its folder.
    /// </remarks>
    public bool PatchServerIsThisMachines => UseLocalPatchServer && PatchServerInstalled;

    /// <summary>
    /// Whether the launcher runs the patch server: started with the server, stopped with the server.
    /// </summary>
    /// <remarks>
    /// On by default, so installing one is enough to be using it; turned off for a patch server the user
    /// starts themselves, which the launcher then neither starts nor stops.
    /// </remarks>
    public bool PatchServerManaged {
        get => _store.Settings.PatchServerManaged;
        set {
            if (_store.Settings.PatchServerManaged == value) {
                return;
            }

            _store.Settings.PatchServerManaged = value;
            _store.Save();

            AppendLog(LogLevel.Info, value
                ? "The patch server will be started with the server and stopped when the server stops."
                : "The patch server is left to you: ViewLight will not start or stop it.");

            OnPropertyChanged();
            RefreshDerived();
        }
    }

    /// <summary>
    /// Whether the launcher keeps the revision the server accepts clients on in step with the revision
    /// the patch server actually holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, and it is what makes the two agree without either piece of software being changed: a
    /// patch server fetches whatever revision Wizard101 is serving and cannot be asked for a different one,
    /// so the value that has to move is the server's <c>GameRevision</c>. Imlight asks its patch server for
    /// <c>V_{GameRevision}</c> and accepts clients on that same revision, so writing the patch server's
    /// revision there is what makes the server's own files reachable <i>and</i> what lets the client that
    /// has just updated get in - both values saying the same thing is the only state either of them works
    /// in.
    /// </para>
    /// <para>
    /// Off leaves <c>GameRevision</c> exactly as the server's own config has it, pinned the way Imlight was
    /// published, which is right for a server whose build is deliberately held at one revision. The patch
    /// server is then the side left needing something it cannot fetch, and the launcher's part is the file
    /// hand-off - see <see cref="PatchServerPreload"/> - which needs this machine's client to be exactly
    /// the revision the server asks for. Turning the sync back on puts back the value the config had before
    /// the launcher wrote one, and only that value: a config that has moved on since is somebody else's to
    /// keep.
    /// </para>
    /// </remarks>
    public bool PatchServerRevisionSynced {
        get => _store.Settings.PatchServerRevisionSynced;
        set {
            if (_store.Settings.PatchServerRevisionSynced == value) {
                return;
            }

            ApplyPatchServerRevisionMode(value);
        }
    }

    /// <summary>
    /// Moves the launcher between the two arrangements, writing whatever the move makes true.
    /// </summary>
    /// <remarks>
    /// One method for both directions because they are each other's undo, and because each has the same
    /// second half: the setting is saved, the box's editability follows it, and a patch server that is
    /// already running is asked so the new arrangement takes hold without waiting for Play.
    /// </remarks>
    private void ApplyPatchServerRevisionMode(bool synced) {
        _store.Settings.PatchServerRevisionSynced = synced;

        if (synced) {
            AppendLog(LogLevel.Info,
                "The server's accepted revision is kept in step with the patch server's from now on: "
                + "GameRevision is set to the revision the patch server holds, so the server asks for files "
                + "it actually has.");

            // A patch server that is already up can answer straight away, so the value is written without
            // waiting for Play. One that is not is asked the next time it is started.
            if (PatchServerIsRunning) {
                _ = ApplyPatchServerRevisionAsync();
            }
        }
        else {
            var restored = RestoreMirroredRevision();

            AppendLog(LogLevel.Info, restored is null
                ? "GameRevision is left as the server's own config has it from now on, and the patch server "
                  + "is not being kept in step with it: its files can only come from the hand-off in "
                  + "Settings, and only while this machine's client is the revision the server asks for."
                : $"GameRevision is back to {restored}, the value it had before the launcher set it from the "
                  + "patch server. It is left as the server's own from now on.");
        }

        _store.Save();
        OnPropertyChanged(nameof(PatchServerRevisionSynced));
        OnPropertyChanged(nameof(CanEditGameRevision));
        RefreshDerived();
    }

    /// <summary>
    /// The port the patch server listens on. Written into its own config the next time it is started,
    /// because a running process has already read the file it has.
    /// </summary>
    public int PatchServerPort {
        get => _store.Settings.PatchServerPort;
        set {
            var port = value is > 0 and <= 65535 ? value : PatchServerConfig.DefaultPort;
            if (_store.Settings.PatchServerPort == port) {
                return;
            }

            _store.Settings.PatchServerPort = port;
            _store.Save();

            AppendLog(LogLevel.Info,
                $"Patch server port set to {port}. It is used the next time the patch server starts.");

            OnPropertyChanged();
            OnPropertyChanged(nameof(LocalPatchServerUrl));
            RefreshDerived();
        }
    }

    partial void OnPatchServerPathChanged(string value) {
        _store.Settings.PatchServerPath = (value ?? string.Empty).Trim();
        _store.Save();
        RefreshPatchFolderStatus();
        RefreshDerived();
    }

    /// <summary>Picks the folder the launcher suggests when there is no preference yet.</summary>
    [RelayCommand]
    private void UseRecommendedPatchServerFolder() {
        PatchServerPath = LauncherPaths.DefaultPatchServerPath;
        AppendLog(LogLevel.Info, $"Patch server folder set to {PatchServerPath}");
    }

    [RelayCommand]
    private async Task BrowsePatchServerFolderAsync() {
        if (PickFolderAsync is null) {
            return;
        }

        var picked = await PickFolderAsync(PatchServerPath).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked)) {
            return;
        }

        PatchServerPath = picked;
        AppendLog(LogLevel.Info, $"Patch server folder set to {picked}");
    }

    // ---------------------------------------------------------------- download & install

    private bool CanDownloadPatchServer()
        => !IsPatchBusy && !string.IsNullOrWhiteSpace(PatchServerPath);

    [RelayCommand(CanExecute = nameof(CanDownloadPatchServer))]
    private async Task DownloadPatchServerAsync() {
        if (IsPatchBusy) {
            return;
        }

        IsPatchBusy = true;
        RefreshDerived();

        _patchInstallCancellation = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p => {
            PatchInstallProgress = p.Percent;
            PatchInstallStage = $"{p.Stage} · {p.Detail}";
        });

        try {
            var release = _latestPatchRelease
                          ?? await _patchReleaseService.GetLatestWindowsReleaseAsync().ConfigureAwait(true);

            _latestPatchRelease = release;
            LatestPatchReleaseText = release.Tag;

            // A running patch server holds its own executable open, so it is stopped before the file
            // that is being replaced is written.
            if (PatchServerIsRunning) {
                AppendLog(LogLevel.Info, "Stopping the patch server so its executable can be replaced…");
                await _patch.StopAsync().ConfigureAwait(true);
            }

            AppendLog(LogLevel.Info,
                $"Downloading patch server {release.Tag} ({release.AssetSizeDisplay}) into {PatchServerPath}.");

            var result = await _patchInstallService
                .InstallAsync(release, PatchServerPath, PatchServerPort, progress, _patchInstallCancellation.Token)
                .ConfigureAwait(true);

            if (result.Success) {
                _store.Settings.PatchServerInstalledTag = release.Tag;
                _store.Settings.PatchServerInstalledAssetUrl = release.AssetUrl;
                _store.Settings.PatchServerInstalledAt = DateTimeOffset.Now;
                _store.Save();

                AppendLog(LogLevel.Success, result.Message);
            }
            else {
                AppendLog(LogLevel.Error, result.Message);
            }

            if (result.Warning is { Length: > 0 }) {
                AppendLog(LogLevel.Warning, result.Warning);
            }

            RefreshPatchInstalledText();

            // A folder that refused the executable leaves nothing to run, and saying so here is better
            // than a Start button that quietly does nothing.
            if (result.Success && !PatchServerInstalled) {
                AppendLog(LogLevel.Warning,
                    $"{PatchServerRunner.ExecutableName} was not written to {PatchServerPath}.");
            }
        }
        catch (Exception ex) {
            PatchCheckError = ex.Message;
            AppendLog(LogLevel.Error, $"Patch server download failed: {ex.Message}");
        }
        finally {
            _patchInstallCancellation?.Dispose();
            _patchInstallCancellation = null;
            IsPatchBusy = false;
            PatchInstallStage = "Idle";
            PatchInstallProgress = 0;
            RefreshDerived();
        }
    }

    [RelayCommand]
    private void CancelPatchInstall() {
        _patchInstallCancellation?.Cancel();
        AppendLog(LogLevel.Warning, "Cancelling the patch server download…");
    }

    [RelayCommand]
    private async Task CheckForPatchServerUpdatesAsync() {
        PatchCheckError = string.Empty;
        LatestPatchReleaseText = "Checking GitHub…";

        try {
            _latestPatchRelease = await _patchReleaseService.GetLatestWindowsReleaseAsync().ConfigureAwait(true);
            LatestPatchReleaseText = _latestPatchRelease.Tag;
            AppendLog(LogLevel.Info,
                $"Newest patch server build: {_latestPatchRelease.Tag} ({_latestPatchRelease.AssetName}).");
            RefreshPatchInstalledText();
        }
        catch (Exception ex) {
            _latestPatchRelease = null;
            LatestPatchReleaseText = "Could not reach GitHub.";
            PatchCheckError = ex.Message;
            AppendLog(LogLevel.Error, $"Patch server release check failed: {ex.Message}");
        }
        finally {
            RefreshDerived();
        }
    }

    private void RefreshPatchInstalledText() {
        var settings = _store.Settings;
        PatchInstalledText = settings.PatchServerInstalledTag is null
            ? "Not downloaded"
            : settings.PatchServerInstalledAt is null
                ? settings.PatchServerInstalledTag
                : $"{settings.PatchServerInstalledTag} · {settings.PatchServerInstalledAt.Value.LocalDateTime:d MMM yyyy}";
    }

    private void RefreshPatchFolderStatus() => PatchFolderStatus = string.IsNullOrWhiteSpace(PatchServerPath)
        ? "No folder chosen yet."
        : PatchServerInstalled
            ? $"Patch server installed — {PatchServerRunner.ExecutableName} found."
            : Directory.Exists(PatchServerPath)
                ? $"No patch server here yet ({PatchServerRunner.ExecutableName} not found)."
                : "Folder does not exist yet — it is created when the patch server is downloaded.";

    // ---------------------------------------------------------------- filling it in from a local install

    /// <summary>
    /// The Wizard101 install a preload copies from, or empty to use whichever one fits.
    /// </summary>
    /// <remarks>
    /// Empty by default rather than filled in, because which install is the right one is a question about
    /// the patch server's revision: the answer is whichever local build <i>is</i> that revision. It is the
    /// launcher's own game folder first and then any install the launcher can find, and the question is
    /// asked again every time the answer is needed - so a machine that patches its game later does not go
    /// on copying from the folder it used to have.
    /// </remarks>
    [ObservableProperty] private string _preloadSourcePath = string.Empty;

    /// <summary>The install a preload would actually copy from, as it resolved.</summary>
    [ObservableProperty] private string _preloadSourceInUse = string.Empty;

    /// <summary>
    /// What the patch server is missing that this machine can hand it, or why it can be handed nothing.
    /// </summary>
    [ObservableProperty] private string _preloadStatus = string.Empty;

    public bool HasPreloadStatus => PreloadStatus.Length > 0;

    /// <summary>Whether a copy is being made now.</summary>
    [ObservableProperty] private bool _isPreloading;

    /// <summary>How far along it is, 0..100.</summary>
    [ObservableProperty] private double _preloadProgress;

    /// <summary>The line beside the bar: which file, of how many, and how many bytes.</summary>
    [ObservableProperty] private string _preloadStage = "Idle";

    /// <summary>Whether there is a copy to make right now.</summary>
    public bool CanPreloadPatchServerCache => !IsPreloading && _preloadPlan is { IsWorthDoing: true };

    // ---------------------------------------------------------------- working out what to copy

    partial void OnPreloadSourcePathChanged(string value) {
        _store.Settings.PatchServerPreloadSource = (value ?? string.Empty).Trim();
        _store.Save();

        _ = RefreshPreloadPlanAsync();
    }

    /// <summary>
    /// Works out what copying a local install into the patch server would do, and puts it on screen.
    /// </summary>
    /// <remarks>
    /// Reads the revision's own file list, which is a megabyte or two of XML, and stats thousands of files
    /// - so it runs off the UI thread. It is what both the Settings card and Play's offer are decided from,
    /// which is why it is one method rather than two: the offer and the button never disagree about what
    /// there is to do.
    /// </remarks>
    public async Task<PatchServerPreloadPlan?> RefreshPreloadPlanAsync() {
        var patchServer = PatchServerPath;
        var configured = PreloadSourcePath;
        var ownGameFolder = ClientPath;
        var revision = ServerWantedRevision;

        var (plan, source) = await Task
            .Run(() => PlanPreload(patchServer, revision, configured, ownGameFolder))
            .ConfigureAwait(true);

        _preloadPlan = plan;
        PreloadSourceInUse = source;

        // Nothing chosen yet: the line stays empty rather than naming a folder that is not there. The
        // folder box above it already says as much, and two lines about the same missing thing is noise.
        PreloadStatus = string.IsNullOrWhiteSpace(patchServer) ? string.Empty : DescribePreload(plan);

        OnPropertyChanged(nameof(HasPreloadStatus));
        OnPropertyChanged(nameof(CanPreloadPatchServerCache));
        PreloadPatchServerCacheCommand.NotifyCanExecuteChanged();

        return plan;
    }

    /// <summary>
    /// The revision the server asks the patch server for - <c>GameRevision</c> - which is also the revision
    /// the server accepts clients on, and therefore the one revision worth having on disk.
    /// </summary>
    /// <remarks>
    /// Read from the config when the launcher has one, and from the box on screen when it has not, so the
    /// answer is the same one the session will use rather than a second opinion about it.
    /// </remarks>
    public string? ServerWantedRevision
        => (_config ?? ImlightConfig.TryLoad(ServerPath))?.GameRevision ?? GameRevision;

    /// <summary>
    /// The plan for the best install on this machine: the one that is set, and failing that the launcher's
    /// own game folder, and failing that whatever install can be found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A folder only counts as a source when the plan built from it is possible - which means its build is
    /// the revision the server asks for, and the patch server has that revision's file list to prove what
    /// belongs in it. An install of a different revision is skipped rather than refused, because that is the
    /// ordinary state of a machine with a game that has patched itself past this server: there is simply
    /// nothing here to give, which is not worth a sentence.
    /// </para>
    /// <para>
    /// The revision is a parameter rather than read here, because it is a fact about the server - and a
    /// machine with no server installed has no revision to ask about, which is a state these tests need to
    /// be able to describe.
    /// </para>
    /// </remarks>
    /// <param name="serverRevision">The revision the server asks the patch server for.</param>
    /// <param name="alsoConsider">
    /// The installs found on this machine, or <c>null</c> to look for them. Given rather than found by the
    /// caller so that what a machine happens to have installed is not what this rule is about.
    /// </param>
    public static (PatchServerPreloadPlan? Plan, string Source) PlanPreload(
        string? patchServerFolder, string? serverRevision, string? configuredSource, string? ownGameFolder,
        IEnumerable<string>? alsoConsider = null) {

        PatchServerPreloadPlan? first = null;
        var firstSource = string.Empty;

        foreach (var folder in PreloadSourceCandidates(configuredSource, ownGameFolder, alsoConsider)) {
            if (!GameFolderLocator.LooksLikeGameFolder(folder)) {
                continue;
            }

            PatchServerPreloadPlan plan;
            try {
                plan = PatchServerPreload.Plan(
                    folder, patchServerFolder, serverRevision, ClientBuildReader.Read(folder));
            }
            catch (Exception) {
                // A path that cannot be used - too long, or not a path at all - is not a source. Another
                // candidate may be, so the search goes on rather than the plan failing.
                continue;
            }

            if (plan.IsPossible) {
                return (plan, folder);
            }

            // Kept only so that a machine with nothing to copy from can be told why, in the words of the
            // first install it has.
            first ??= plan;

            if (firstSource.Length == 0) {
                firstSource = folder;
            }
        }

        return first is null
            ? (PatchServerPreload.Plan(null, patchServerFolder, serverRevision, null), string.Empty)
            : (first, firstSource);
    }

    /// <summary>The installs to try, best first: the one that was set, the launcher's own, then any found.</summary>
    private static IEnumerable<string> PreloadSourceCandidates(
        string? configured, string? ownGameFolder, IEnumerable<string>? alsoConsider) {

        if (!string.IsNullOrWhiteSpace(configured)) {
            yield return configured.Trim();
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(ownGameFolder)) {
            yield return ownGameFolder.Trim();
        }

        foreach (var candidate in alsoConsider ?? GameFolderLocator.Candidates().Select(c => c.Path)) {
            yield return candidate;
        }
    }

    /// <summary>
    /// What the line under the patch server's folder box says about filling it in.
    /// </summary>
    /// <remarks>
    /// Counted out of the revision's own file list, so it is a fact about the patch server rather than an
    /// estimate: how many files of the revision it holds, how many of the rest are on this machine, and how
    /// many nobody here can supply - which the patch server fetches itself.
    /// </remarks>
    public static string DescribePreload(PatchServerPreloadPlan? plan) {
        if (plan is null) {
            return string.Empty;
        }

        if (plan.Refusal is { Length: > 0 } refusal) {
            return refusal;
        }

        var held = plan.AlreadyHeld + plan.HeldAtAnotherSize;
        var head = $"The patch server holds {held:N0} of {plan.RevisionFiles:N0} files of {plan.Revision}.";

        if (!plan.IsWorthDoing) {
            return $"{head} Nothing is missing from it that this Wizard101 install can supply.";
        }

        var elsewhere = plan.NotInTheBuild + plan.AtAnotherSizeInTheBuild;
        var tail = elsewhere == 0
            ? string.Empty
            : $" The other {elsewhere:N0} are not in this install, so the patch server fetches those itself.";

        return $"{head} {plan.FilesToCopy:N0} of the rest ({ServerInstallService.FormatBytes(plan.BytesToCopy)}) "
               + $"are here and can be copied straight in.{tail}";
    }

    // ---------------------------------------------------------------- copying it in

    [RelayCommand]
    private async Task BrowsePreloadSourceAsync() {
        if (PickFolderAsync is null) {
            return;
        }

        var picked = await PickFolderAsync(PreloadSourcePath).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked)) {
            return;
        }

        PreloadSourcePath = picked;
        AppendLog(LogLevel.Info, $"The patch server's files are copied from {picked}.");
    }

    /// <summary>Goes back to whichever install fits, rather than one that was picked.</summary>
    [RelayCommand]
    private void FindPreloadSource() {
        PreloadSourcePath = string.Empty;

        AppendLog(LogLevel.Info,
            "The install to copy from is found again: whichever one is the revision the patch server holds.");

        _ = RefreshPreloadPlanAsync();
    }

    [RelayCommand(CanExecute = nameof(IsPreloading))]
    private void CancelPreload() {
        _preloadCancellation?.Cancel();
        AppendLog(LogLevel.Warning, "Stopping the copy into the patch server…");
    }

    [RelayCommand(CanExecute = nameof(CanPreloadPatchServerCache))]
    private async Task PreloadPatchServerCacheAsync() {
        var plan = _preloadPlan ?? await RefreshPreloadPlanAsync().ConfigureAwait(true);

        if (plan is not { IsWorthDoing: true }) {
            return;
        }

        await CopyCacheInAsync(plan).ConfigureAwait(true);
    }

    /// <summary>
    /// Copies a plan's files into the patch server, saying what it is doing in the Log as well as on the
    /// bar.
    /// </summary>
    private async Task<bool> CopyCacheInAsync(PatchServerPreloadPlan plan) {
        if (IsPreloading) {
            return false;
        }

        IsPreloading = true;
        _preloadCancellation = new CancellationTokenSource();
        RefreshPatchDerived();

        var progress = new Progress<PatchServerPreloadProgress>(update => {
            PreloadProgress = update.Percent;
            PreloadStage = update.Detail;
        });

        try {
            AppendLog(LogLevel.Info,
                $"Copying {plan.FilesToCopy:N0} files ({ServerInstallService.FormatBytes(plan.BytesToCopy)}) of "
                + $"{plan.Revision} from {plan.SourceFolder} into the patch server…");

            var result = await _patchPreload
                .RunAsync(plan, progress, _preloadCancellation.Token)
                .ConfigureAwait(true);

            AppendLog(result.Success ? LogLevel.Success : LogLevel.Warning, result.Message);

            if (result.Warning is { Length: > 0 }) {
                AppendLog(LogLevel.Warning, result.Warning);
            }

            return result.Success;
        }
        finally {
            _preloadCancellation?.Dispose();
            _preloadCancellation = null;
            IsPreloading = false;
            PreloadProgress = 0;
            PreloadStage = "Idle";

            // Asked again rather than assumed: what the patch server holds has just changed, and so has what
            // is left to copy.
            await RefreshPreloadPlanAsync().ConfigureAwait(true);
            RefreshPatchDerived();
        }
    }

    /// <summary>
    /// Counts the revision the patch server holds, and keeps counting it while it fetches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the launcher using the patch server rather than helping it.</b> Aurorium fetches a
    /// revision exactly once and in its own way - it writes the revision's file list, asks Wizard101's patch
    /// server for what that list names, and skips anything it already has - and the launcher's part is to
    /// say how far along that is and roughly when it will be done. Nothing is written into the patch
    /// server's folders, nothing is repointed, and nothing is asked of it: the whole figure comes from the
    /// revision's own file list against the files that are on disk, which is why watching cannot perturb a
    /// fetch that is running.
    /// </para>
    /// <para>
    /// <b>Started when the patch server is, and it stops when that is.</b> A patch server only fetches while
    /// it is alive, so there is nothing to watch the rest of the time. It is left running after a fetch is
    /// over, because a finished revision is worth saying once and a revision that is merely paused will
    /// start again on its own.
    /// </para>
    /// </remarks>
    private void SyncFetchWatch() {
        if (PatchServerIsRunning) {
            StartFetchWatch();
        }
        else {
            StopFetchWatch();
        }
    }

    private void StartFetchWatch() {
        if (_fetchWatchCancellation is not null) {
            return;
        }

        // A rate measured over a fetch that has ended has nothing to do with the one starting now, and the
        // gap between the two would be read as an impossibly long window.
        _fetchPace.Reset();

        var cancellation = new CancellationTokenSource();
        _fetchWatchCancellation = cancellation;

        _ = WatchFetchAsync(cancellation);
    }

    /// <summary>
    /// Stops watching, if anything is.
    /// </summary>
    /// <remarks>
    /// The source is cancelled here and disposed by the loop itself, which is the only thing that knows when
    /// it has finished with it: disposing it out from under a delay that is already waiting on its token
    /// would be a use-after-free rather than a stop.
    /// </remarks>
    private void StopFetchWatch() {
        _fetchWatchCancellation?.Cancel();
        ClearFetchStatus();
    }

    /// <summary>
    /// Samples the disk every few seconds and puts the answer on screen, for as long as the patch server is
    /// running.
    /// </summary>
    /// <remarks>
    /// The revision is asked of the patch server's own API rather than assumed, and asked again until it
    /// answers: its endpoint is up before its first pass has put a revision in its database, so a watch that
    /// starts with the process has nothing to count for the first few seconds. A revision folder that is
    /// being written is read as no measurement at all, which is why a sample that comes back empty leaves
    /// the line as it was rather than clearing it.
    /// </remarks>
    private async Task WatchFetchAsync(CancellationTokenSource cancellation) {
        var cancellationToken = cancellation.Token;
        string? revision = null;

        try {
            while (!cancellationToken.IsCancellationRequested && PatchServerIsRunning) {
                revision ??= await LatestPatchRevisionAsync(cancellationToken).ConfigureAwait(true);

                if (revision is not null) {
                    var patchServer = PatchServerPath;
                    var state = await Task
                        .Run(() => PatchServerFetch.Measure(patchServer, revision), cancellationToken)
                        .ConfigureAwait(true);

                    // Checked rather than assumed: a stop that lands while this sample is being taken
                    // clears the line, and a sample arriving afterwards would put it back on a patch
                    // server that is no longer running - with nothing left to take it down again.
                    if (state is not null && !cancellationToken.IsCancellationRequested) {
                        ApplyFetchState(state);
                    }
                }

                await Task.Delay(FetchSampleInterval, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) {
            // A stop, which is what a stopped patch server is. Nothing to report.
        }
        catch (Exception ex) {
            // Never a failed session: a figure the launcher could not work out is not something the patch
            // server is doing wrong, and the fetch itself is unaffected either way.
            AppendLog(LogLevel.Warning, $"Could not keep up with the patch server's fetch: {ex.Message}");
        }
        finally {
            // The loop can also end by itself - the process went away - and the field must not be left
            // holding a watch that is no longer running, or the next start would think one was already on.
            if (ReferenceEquals(_fetchWatchCancellation, cancellation)) {
                _fetchWatchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    /// <summary>
    /// The name of the newest revision the patch server holds, or <c>null</c> while it holds none - which
    /// is the state it is in between its endpoint answering and its first pass finishing.
    /// </summary>
    private async Task<string?> LatestPatchRevisionAsync(CancellationToken cancellationToken) {
        var revisions = await _patchRevisions
            .ListAsync(LocalPatchServerUrl, cancellationToken)
            .ConfigureAwait(true);

        var newest = PatchServerConfig.NewestGameRevision(revisions);

        return newest is null ? null : PatchServerPreload.RevisionNameFor(newest);
    }

    private void ApplyFetchState(PatchServerFetchState state) {
        var estimate = _fetchPace.Observe(state, DateTimeOffset.Now);

        // The revision the server asks for is handed to the description rather than read by it, because it
        // is a fact about Imlight's config and the description is about the disk - and because the mismatch
        // between the two is the one thing on that line a session cannot work through.
        var revision = ServerWantedRevision;

        FetchProgress = state.IsComplete ? 100 : state.Percent;
        IsFetchingGameFiles = !state.IsComplete;
        FetchingRevisionIsTheServers = PatchServerFetch.DescribeRevisionMismatch(state, revision).Length == 0;

        FetchStatus = PatchServerFetch.Describe(state, estimate, revision);

        OnPropertyChanged(nameof(HasFetchStatus));
        OnPropertyChanged(nameof(FetchingRevisionIsTheServers));

        ReportFetchInTheLog(state, estimate, revision);
    }

    private void ClearFetchStatus() {
        FetchStatus = string.Empty;
        FetchProgress = 0;
        IsFetchingGameFiles = false;
        _fetchPace.Reset();
        _fetchFileReported = null;
        _fetchRevisionReported = null;
        _fetchMismatchReported = false;
        _fetchLoggedAt = DateTimeOffset.MinValue;

        OnPropertyChanged(nameof(HasFetchStatus));
    }

    /// <summary>
    /// Writes the fetch into the Log when there is something new to say about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things make it worth a line: the file it is downloading has moved on, the revision it is
    /// fetching has changed, or a minute has passed since the last one - plus the revision mismatch, which is
    /// said the first time it is seen whatever else is going on, because that one is not progress but an
    /// answer to why a session will fail.
    /// </para>
    /// <para>
    /// The line is the same sentence the card shows, which is the point: the Log keeps what the line on a
    /// screen cannot - how far along this was at each point of a download that takes hours.
    /// </para>
    /// </remarks>
    private void ReportFetchInTheLog(
        PatchServerFetchState state, PatchServerFetchEstimate estimate, string? serverRevision) {

        var file = state.CurrentFile?.RelativePath;
        var revisionChanged = !string.Equals(state.Revision, _fetchRevisionReported, StringComparison.OrdinalIgnoreCase);
        var fileChanged = !string.Equals(file, _fetchFileReported, StringComparison.OrdinalIgnoreCase);
        var due = DateTimeOffset.Now - _fetchLoggedAt >= FetchLogInterval;
        var mismatch = PatchServerFetch.DescribeRevisionMismatch(state, serverRevision);
        var mismatchIsNew = mismatch.Length > 0 && !_fetchMismatchReported;

        if (!revisionChanged && !fileChanged && !due) {
            return;
        }

        _fetchRevisionReported = state.Revision;
        _fetchFileReported = file;
        _fetchLoggedAt = DateTimeOffset.Now;

        // The file's own path is worth a line of its own: which file of a revision of thousands it is on is
        // exactly the detail a percentage leaves out.
        AppendLog(LogLevel.Info,
            state.CurrentFile is { } currentFile && (fileChanged || revisionChanged)
                ? $"Fetching {currentFile.RelativePath}: "
                  + $"{ServerInstallService.FormatBytes(currentFile.BytesSoFar)}"
                  + (currentFile.HasASize
                      ? $" of {ServerInstallService.FormatBytes(currentFile.TotalBytes)}"
                      : string.Empty)
                  + $". {PatchServerFetch.Describe(state, estimate, serverRevision)}"
                : PatchServerFetch.Describe(state, estimate, serverRevision));

        if (!mismatchIsNew) {
            return;
        }

        _fetchMismatchReported = true;
        AppendLog(LogLevel.Warning, mismatch);
        AppendLog(LogLevel.Warning,
            "Play waits for the patch server to have "
            + $"{PatchServerServing.BootFile} before it starts the server, and it can be sent on without "
            + "one with the button on Home.");
    }
    // ---------------------------------------------------------------- when the server cannot load its files

    /// <summary>The address the last refusal was for, so one failure is reported once.</summary>
    private string? _patchServerRefusalReported;

    /// <summary>
    /// What the server said about a file it could not load from the patch server, in the launcher's own
    /// words, or empty while it has said nothing.
    /// </summary>
    /// <remarks>
    /// The failure that has no other symptom: the server is alive, its ports are open, and it sits at
    /// "warming up" forever because the first file it asked its patch server for was not there. The
    /// launcher reads it out of the server's own log - the only place it exists - and puts it on Home with
    /// the reason and the way out, rather than leaving the user to wait for a server that is never going to
    /// finish.
    /// </remarks>
    [ObservableProperty] private string _patchServerRefusalNotice = string.Empty;

    public bool HasPatchServerRefusalNotice => PatchServerRefusalNotice.Length > 0;

    /// <summary>
    /// Reports a file the server could not load from its patch server, once per address.
    /// </summary>
    /// <remarks>
    /// A session that fails this way fails over and over - the server restarts the patch actor and asks
    /// again - so the address asked for is what tells a new failure from the same one being retried. Nothing
    /// is acted on: the launcher does not repoint anything and does not restart the server under the player.
    /// What it does is say which two revisions have to agree and which of the two is the launcher's to move.
    /// </remarks>
    public void ReportPatchServerRefusal(PatchServerRefusedAFile refusal) {
        if (string.Equals(refusal.Url, _patchServerRefusalReported, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        _patchServerRefusalReported = refusal.Url;

        var holding = _store.Settings.MirroredRevision;
        var patchServerHas = holding is { Length: > 0 }
            ? $"The patch server is holding {holding}"
            : "A patch server fetches whatever revision Wizard101 is serving and cannot be asked for another";

        PatchServerRefusalNotice =
            $"The server asked the patch server for {refusal.Revision}/{refusal.File} and was told 404, so it "
            + $"cannot finish warming up. {patchServerHas}, and your server's own revision is the one it asks "
            + "for: the two have to be the same revision for either of them to work."
            + (PatchServerRevisionSynced
                ? " The launcher keeps them in step when it starts the patch server, so stop the server and "
                  + "press Play to pick up the patch server's revision."
                : " The level of sync under Settings → Patch server is off, so the server is pinned to the "
                  + "revision its own config has - and a pinned revision can only be filled in by the hand-off "
                  + "on that card.");

        AppendLog(LogLevel.Error, PatchServerRefusalNotice);
        OnPropertyChanged(nameof(HasPatchServerRefusalNotice));
    }

    /// <summary>Reports, once each, every file the server said it could not load from its patch server.</summary>
    private void ReportPatchServerRefusals(IReadOnlyList<string> lines) {
        foreach (var line in lines) {
            foreach (var fact in ServerLogFacts.Parse(line)) {
                if (fact is PatchServerRefusedAFile refusal) {
                    ReportPatchServerRefusal(refusal);
                }
            }
        }
    }

    /// <summary>Forgets the last refusal, for a session that is starting over.</summary>
    private void ForgetPatchServerRefusal() {
        _patchServerRefusalReported = null;
        PatchServerRefusalNotice = string.Empty;
        OnPropertyChanged(nameof(HasPatchServerRefusalNotice));
    }

    // ---------------------------------------------------------------- waiting for the files it needs

    /// <summary>
    /// How often the file Imlight's boot asks for is asked of the patch server while Play waits for it.
    /// </summary>
    private static readonly TimeSpan PatchServerBootFilePollInterval = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _waitingForBootFile;

    /// <summary>How long the last start spent waiting for the patch server's files.</summary>
    private TimeSpan _waitedForPatchServerFiles;

    /// <summary>
    /// Whether Play is holding the server back because the patch server does not have the file its boot asks
    /// for yet.
    /// </summary>
    /// <remarks>
    /// A line on Home rather than a phase, because it is not the server doing anything: the server has not
    /// been started, and the thing making progress is the patch server's own download - which is why what it
    /// says is the fetch's own line.
    /// </remarks>
    [ObservableProperty] private bool _isWaitingForPatchServerFiles;

    /// <summary>What that wait says, including where the download has got to.</summary>
    [ObservableProperty] private string _waitingForPatchServerFilesText = string.Empty;

    public bool HasWaitingForPatchServerFilesText => WaitingForPatchServerFilesText.Length > 0;

    /// <summary>
    /// Whether a start should wait for the patch server rather than go ahead.
    /// </summary>
    /// <remarks>
    /// Stated as a rule so it can be tested without a patch server: the patch server has to be up, it has to
    /// be the one Imlight is about to ask, and it has to be unable to answer for the file Imlight's boot
    /// needs first. Anything else is nothing to wait for. This is the bug it exists for - a server whose
    /// warm-up starts before the patch server has fetched anything reaches its first request, gets a
    /// <c>404</c> for its own <c>Root.wad</c>, and never finishes warming up.
    /// </remarks>
    public static bool ShouldWaitForPatchServerFiles(
        bool patchServerIsRunning, bool imlightAsksThisOne, bool canServeTheBootFile)
        => patchServerIsRunning && imlightAsksThisOne && !canServeTheBootFile;

    public bool CanPlayAnyway => IsWaitingForPatchServerFiles;

    /// <summary>
    /// Starts the server now, without waiting any longer for the patch server's files.
    /// </summary>
    /// <remarks>
    /// The wait is a real cost - a first fetch is hours - so it is always something the user can end, and
    /// ending it is said out loud: a session started against a patch server that is still fetching is one
    /// that fails on the <c>404</c> rather than finding its files, and nothing else would connect the two.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPlayAnyway))]
    private void PlayAnyway() {
        var waiting = _waitingForBootFile;

        if (waiting is null) {
            return;
        }

        AppendLog(LogLevel.Warning,
            "Playing without waiting for the patch server's files. Imlight asks for "
            + $"{PatchServerServing.BootFile} during its own boot, and a patch server that does not have it "
            + "yet answers 404 - the session loads Root.wad from Imlight's own cache if it has one, and stops "
            + "on it if it does not. The fetch keeps running while ViewLight is open."
            + (FetchStatus.Length > 0 ? $" {FetchStatus}" : string.Empty));

        waiting.Cancel();
    }

    /// <summary>
    /// Waits for the patch server to be able to serve the file Imlight's boot asks for, or for the user to
    /// send the session on without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one file, not the whole revision.</b> Imlight's warm-up asks the patch server for
    /// <c>Data/GameData/Root.wad</c> and caches it; the rest of the revision is asked for by the client as it
    /// plays. So what is waited for is the file the boot cannot start without - minutes rather than the hours
    /// a whole revision takes - and a session can still find a zone file missing later, which is the honest
    /// limit of this and is why the fetch line stays on screen afterwards.
    /// </para>
    /// <para>
    /// Asked rather than assumed: a <c>HEAD</c> for the boot file is the same request Imlight makes first,
    /// so an answer means the boot that is about to start will get its files. Nothing is repointed and
    /// nothing is refused - a patch server that goes away, or a user who would rather play now, both end the
    /// wait and the session goes ahead exactly as it used to.
    /// </para>
    /// </remarks>
    private async Task WaitForPatchServerFilesAsync() {
        _waitedForPatchServerFiles = TimeSpan.Zero;

        if (!PatchServerIsRunning) {
            return;
        }

        // Nothing to wait for when the session is not going to ask this machine's patch server: the wait is
        // about the address Imlight reads, and a remote one is reached whether this is up or not.
        var config = _config ?? ImlightConfig.TryLoad(ServerPath);

        if (!ShouldWaitForPatchServerFiles(
                PatchServerIsRunning,
                IsTheLocalPatchServerAddress(config?.PatchServerUrl, LocalPatchServerUrl),
                await PatchServerServingAsync().ConfigureAwait(true))) {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _waitingForBootFile = cancellation;

        var revision = RevisionThisSessionAsks();
        var started = DateTimeOffset.Now;

        IsWaitingForPatchServerFiles = true;
        WaitingForPatchServerFilesText = DescribeWaitingForBootFile(revision);
        RefreshDerived();
        OnPropertyChanged(nameof(HasWaitingForPatchServerFilesText));

        AppendLog(LogLevel.Info,
            $"The patch server at {LocalPatchServerUrl} does not have {revision}'s "
            + $"{PatchServerServing.BootFile} yet, which is the first thing Imlight asks it for when it "
            + "starts. Waiting for it rather than starting a server that cannot fetch its own files - press "
            + "Play anyway on Home to start it now.");

        try {
            while (!cancellation.IsCancellationRequested) {
                // Asked again while it waits, and this is the request that makes the wait worth having: a
                // patch server has no revision to list until its own first pass has written one into its
                // database, which can be seconds after its endpoint answers - so a session that started out
                // waiting for the revision the server shipped with is moved onto the one the patch server
                // has, and then it is waiting for the right file. With the sync off this does nothing.
                await ApplyPatchServerRevisionAsync().ConfigureAwait(true);

                revision = RevisionThisSessionAsks();

                await Task.Delay(PatchServerBootFilePollInterval, cancellation.Token).ConfigureAwait(true);

                if (await PatchServerServingAsync().ConfigureAwait(true)) {
                    AppendLog(LogLevel.Success,
                        $"The patch server has {revision}'s {PatchServerServing.BootFile} now, so the server "
                        + "is being started.");
                    return;
                }

                // A patch server that has gone away cannot be waited for, and one that is being written into
                // is still worth waiting on - so only the first of those ends the wait.
                if (!PatchServerIsRunning) {
                    AppendLog(LogLevel.Warning,
                        "The patch server stopped while the launcher was waiting for its files, so the "
                        + "server is being started without them.");
                    return;
                }

                WaitingForPatchServerFilesText = DescribeWaitingForBootFile(revision);
                OnPropertyChanged(nameof(HasWaitingForPatchServerFilesText));
            }
        }
        catch (OperationCanceledException) {
            // Play anyway, which has already said so.
        }
        finally {
            _waitedForPatchServerFiles = DateTimeOffset.Now - started;
            _waitingForBootFile = null;
            IsWaitingForPatchServerFiles = false;
            WaitingForPatchServerFilesText = string.Empty;

            cancellation.Dispose();
            RefreshDerived();
            OnPropertyChanged(nameof(HasWaitingForPatchServerFilesText));
        }
    }

    /// <summary>
    /// The revision this session is going to ask the patch server for, as Imlight's config now has it.
    /// </summary>
    /// <remarks>
    /// Read every time round the wait rather than once, because the sync may have moved it in between - the
    /// wait is about the revision the server asks for, and that answer changes while it is being waited on.
    /// </remarks>
    private string? RevisionThisSessionAsks()
        => (_config ?? ImlightConfig.TryLoad(ServerPath))?.GameRevision ?? GameRevision;

    /// <summary>
    /// What the wait says on Home: what is being waited for, and where the fetch has got to.
    /// </summary>
    private string DescribeWaitingForBootFile(string? revision) {
        var what = $"Waiting for the patch server to have {revision}'s {PatchServerServing.BootFile}, which "
                   + "is the first file Imlight asks it for when its own warm-up starts, before starting the "
                   + "server.";

        return FetchStatus.Length == 0
            ? $"{what} The patch server has not said how far along it is yet. Play anyway starts the server "
              + "now."
            : $"{what} {FetchStatus} Play anyway starts the server now.";
    }

    // ---------------------------------------------------------------- running it

    private bool CanStartPatchServer()
        => !IsPatchBusy && PatchServerState is ServerState.Stopped or ServerState.Faulted && PatchServerInstalled;

    private bool CanStopPatchServer() => PatchServerState is not (ServerState.Stopped or ServerState.Stopping);

    /// <summary>Starts the patch server on its own, for running one without the server.</summary>
    [RelayCommand(CanExecute = nameof(CanStartPatchServer))]
    private async Task StartPatchServerAsync() {
        await StartPatchServerCoreAsync().ConfigureAwait(true);
        RefreshDerived();
    }

    [RelayCommand(CanExecute = nameof(CanStopPatchServer))]
    private async Task StopPatchServerAsync() {
        if (!PatchServerIsRunning) {
            return;
        }

        await _patch.StopAsync().ConfigureAwait(true);
        RefreshDerived();
    }

    /// <summary>
    /// Makes sure the patch server is up before the server is started, and says whether it is.
    /// </summary>
    /// <remarks>
    /// Waited for rather than raced with the Director: the Director reads the patch server during its
    /// own boot, so a patch server that comes up afterwards is one this session does not get to use.
    /// </remarks>
    private async Task<bool> EnsurePatchServerStartedAsync() {
        if (!PatchServerIsThisMachines || !PatchServerManaged) {
            return false;
        }

        if (PatchServerState == ServerState.Ready) {
            return true;
        }

        if (PatchServerState is ServerState.Starting or ServerState.WarmingUp) {
            // Something else already asked for it - a manual start, or a slow one. Wait for that.
            return await WaitForPatchServerAsync().ConfigureAwait(true);
        }

        return await StartPatchServerCoreAsync().ConfigureAwait(true);
    }

    private async Task<bool> StartPatchServerCoreAsync() {
        if (!PatchServerInstalled) {
            AppendLog(LogLevel.Warning,
                $"There is no {PatchServerRunner.ExecutableName} in the patch server folder, so there is "
                + "nothing to start. Download it under Settings.");
            return false;
        }

        // The config is what decides which port it listens on, so it is brought in line before the
        // process starts - and written only when it says something else.
        if (PatchServerConfig.EnsureConfig(PatchServerPath, PatchServerPort)) {
            AppendLog(LogLevel.Info,
                $"Wrote the patch server's config for endpoint {PatchServerConfig.Host}:{PatchServerPort} "
                + $"and a {PatchServerConfig.FetchIntervalSeconds / 60}-minute retry interval. A fetch that "
                + "cannot finish is one that waits hours between attempts.");
        }

        try {
            _patch.Start(new PatchServerStartOptions(
                PatchServerPath,
                PatchServerRunner.ExecutablePathFor(PatchServerPath),
                PatchServerConfig.Host,
                PatchServerPort));
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"Could not start the patch server: {ex.Message}");
            return false;
        }

        if (await WaitForPatchServerAsync().ConfigureAwait(true)) {
            // Up and answering, so it can be asked which revision it holds - the one thing a session needs
            // from it before the server is started.
            await ApplyPatchServerRevisionAsync().ConfigureAwait(true);
            return true;
        }

        AppendLog(LogLevel.Warning, PatchServerState == ServerState.Faulted
            ? "The patch server did not stay up. Imlight keeps its own cached copy of Root.wad, so the "
              + "server is started anyway."
            : "The patch server has not answered yet. Imlight keeps its own cached copy of Root.wad, so "
              + "the server is started anyway.");

        return false;
    }

    private async Task<bool> WaitForPatchServerAsync() {
        var deadline = DateTimeOffset.Now.Add(PatchServerStartTimeout);

        while (PatchServerState is ServerState.Starting or ServerState.WarmingUp
               && DateTimeOffset.Now < deadline) {
            await Task.Delay(250).ConfigureAwait(true);
        }

        return PatchServerState == ServerState.Ready;
    }

    /// <summary>
    /// Asks the patch server which revision it holds, and keeps the server's own revision in step with it.
    /// </summary>
    /// <remarks>
    /// The one request the launcher makes to a patch server, and it is only made while the sync is on.
    /// Nothing else needs an answer from it: what the launcher does with the answer is write it into
    /// <c>GameRevision</c>, and that is the whole of the arrangement. Whether the files behind that revision
    /// have finished arriving is a separate question with a separate answer - Play waits for the one file the
    /// server's boot asks for, and the fetch line says what the rest of it is doing.
    /// </remarks>
    private async Task ApplyPatchServerRevisionAsync() {
        if (!PatchServerRevisionSynced || !PatchServerIsRunning) {
            return;
        }

        var revisions = await _patchRevisions.ListAsync(LocalPatchServerUrl).ConfigureAwait(true);

        if (ApplyMirroredRevision(revisions)) {
            return;
        }

        if (revisions.Count == 0) {
            AppendLog(LogLevel.Warning,
                $"The patch server at {LocalPatchServerUrl} did not list a revision, so GameRevision is "
                + "left as the config has it. It is asked again the next time the patch server starts.");
        }
    }

    /// <summary>
    /// Points Imlight at this machine's patch server, on the address and port it was given.
    /// </summary>
    /// <remarks>
    /// Run before the Director starts, because its patch server address is read during its boot. Two
    /// outcomes, both deliberate: this machine's patch server when the launcher is the one running it and
    /// it is up, and <b>no change at all otherwise</b> - which is what keeps the public patch server, or a
    /// patch server somebody runs elsewhere, a choice rather than something the launcher second-guesses.
    /// No address is ever written back to the public one, and nothing asks what the patch server can
    /// serve: the launcher gives the address, and what is behind it is not its business.
    /// </remarks>
    private void ApplyPatchServerToConfig() {
        var config = _config ?? ImlightConfig.TryLoad(ServerPath);
        if (config is null) {
            return;
        }

        var wanted = PatchServerAddressFor(PatchServerIsThisMachines, PatchServerState, PatchServerPort);

        if (wanted is null) {
            return;
        }

        if (string.Equals(config.PatchServerUrl, wanted, StringComparison.OrdinalIgnoreCase)) {
            PatchServerUrl = wanted;
            return;
        }

        try {
            config.PatchServerUrl = wanted;
            config.Save(ServerPath);
            _config = config;
            PatchServerUrl = wanted;

            AppendLog(LogLevel.Info,
                $"Imlight's patch server is now the local one at {wanted}, so Root.wad comes from this "
                + "machine.");
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Warning,
                $"Could not point Imlight at {wanted} ({ex.Message}), so it will use whatever its config "
                + "already says.");
        }
    }

    /// <summary>
    /// The address Imlight should be pointed at, or <c>null</c> when its config should be left alone.
    /// </summary>
    /// <remarks>
    /// This machine's patch server when it is the one that was chosen and it is up - on the address and port
    /// the launcher was given, which is the whole rule. Anything else leaves Imlight's own
    /// <c>PatchServerInternalUrl</c> exactly as it is, so the public patch server and a patch server
    /// somebody else runs are both reached by leaving the address alone rather than by the launcher
    /// rewriting it. Stated as a rule so it can be tested without a patch server running.
    /// </remarks>
    /// <param name="patchServerIsThisMachines">
    /// The setting, and a patch server installed to answer with - <c>PatchServerIsThisMachines</c>. It is not
    /// "the launcher is running it": a patch server this machine owns and started by hand is still the one
    /// this session's files should come from.
    /// </param>
    public static string? PatchServerAddressFor(bool patchServerIsThisMachines, ServerState state, int port)
        => patchServerIsThisMachines && state == ServerState.Ready ? PatchServerConfig.EndpointUrl(port) : null;

    /// <summary>
    /// Whether stopping the server also stops the patch server.
    /// </summary>
    /// <remarks>
    /// It normally does: both are processes this launcher started, and the patch server holding its port
    /// after the server is gone is the reason it goes down with it. The exception is a patch server that
    /// cannot serve its revision yet - still on its first fetch - because then stopping it is stopping the
    /// download, and the fetch only ever advances while it is running. A patch server that has finished,
    /// or one nobody runs for the launcher, is not kept. Stated as a rule so it can be tested without a
    /// patch server or a server.
    /// </remarks>
    public static bool PatchServerStopsWithTheServer(bool patchServerIsRunning, bool patchServerCanServeItsRevision)
        => patchServerIsRunning && patchServerCanServeItsRevision;

    /// <summary>
    /// Whether the address in Imlight's config is this machine's patch server - the one a session's files
    /// would come from.
    /// </summary>
    /// <remarks>
    /// What the fetch report hangs on. A patch server started from Settings while the config points at the
    /// public one is a patch server this session never asks, and warning about it would be a sentence about
    /// something that is not happening. Stated as a rule rather than written inline so it can be tested
    /// without a config or a patch server.
    /// </remarks>
    public static bool IsTheLocalPatchServerAddress(string? configured, string localPatchServerUrl) {
        var configuredUrl = (configured ?? string.Empty).Trim().TrimEnd('/');
        var local = (localPatchServerUrl ?? string.Empty).Trim().TrimEnd('/');

        return configuredUrl.Length > 0
               && string.Equals(configuredUrl, local, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks whether this machine's patch server can serve the revision the session is about to ask for,
    /// remembers the answer, and says so in the Log when it cannot.
    /// </summary>
    /// <remarks>
    /// <b>Reported, not obeyed.</b> Nothing is repointed on the strength of it: Imlight is given the
    /// address it is given, and a session started before a first fetch has finished loads <c>Root.wad</c>
    /// from its own cache - or stops on the 404, which is a patch server serving nothing rather than a
    /// patch server the launcher should have second-guessed. What the launcher can do is stop leaving the
    /// user to guess why: the fetch is thousands of files, it continues while ViewLight is open, and the
    /// session that is about to fail because of it is worth one line of the Log.
    /// </remarks>
    private async Task WarnIfPatchServerIsStillFetchingAsync() {
        if (!PatchServerIsRunning) {
            return;
        }

        var config = _config ?? ImlightConfig.TryLoad(ServerPath);

        // Nothing to say about a patch server this session is not going to ask: the warning is about the
        // address Imlight reads, and Imlight reads this machine's only while it is the one in the config.
        if (!IsTheLocalPatchServerAddress(config?.PatchServerUrl, LocalPatchServerUrl)) {
            return;
        }

        if (await PatchServerServingAsync().ConfigureAwait(true)) {
            var fetch = await PatchServerFetchProgressAsync().ConfigureAwait(true);

            if (fetch is { IsComplete: false } unfinished) {
                var revision = config?.GameRevision ?? GameRevision;

                AppendLog(LogLevel.Warning,
                    $"The patch server at {LocalPatchServerUrl} is still fetching {revision}'s files: it "
                    + $"has {unfinished.HeldFiles:N0} of {unfinished.TotalFiles:N0} "
                    + $"({ServerInstallService.FormatBytes(unfinished.FetchedBytes)} of "
                    + $"{ServerInstallService.FormatBytes(unfinished.TotalBytes)}). It can serve what it "
                    + "has, and the game asks for the rest as it plays - a zone or an asset that has not "
                    + "arrived yet is a 404 in the client rather than a launcher problem. The fetch keeps "
                    + "running while this launcher is open.");
            }

            return;
        }

        var wanted = config?.GameRevision ?? GameRevision;

        AppendLog(LogLevel.Warning,
            $"The patch server at {LocalPatchServerUrl} has not fetched {wanted}'s files yet, so it does "
            + $"not have {PatchServerServing.BootFile} to serve. This session loads Root.wad from "
            + "Imlight's own cache, or stops on it if it has none - and while this launcher is open the "
            + "fetch keeps running, so pressing Play again once it has finished gets its files from here.");
    }

    /// <summary>
    /// What the patch server has of the revision Imlight is pointed at, or <c>null</c> when there is nothing
    /// to count.
    /// </summary>
    /// <remarks>
    /// Read for the sentence about a fetch that is still running rather than for the line on screen - the
    /// watch owns that one - and read off the disk, so it is the same answer whether the launcher has been
    /// watching or not.
    /// </remarks>
    private async Task<PatchServerFetchState?> PatchServerFetchProgressAsync() {
        var revision = ServerWantedRevision;
        var patchServer = PatchServerPath;
        var name = PatchServerPreload.RevisionNameFor(revision);

        if (name is null) {
            return null;
        }

        return await Task.Run(() => PatchServerFetch.Measure(patchServer, name)).ConfigureAwait(true);
    }

    /// <summary>
    /// Whether this machine's patch server can serve the revision Imlight is pointed at right now, asking
    /// it and remembering the answer.
    /// </summary>
    private async Task<bool> PatchServerServingAsync() {
        var config = _config ?? ImlightConfig.TryLoad(ServerPath);
        var serving = await _patchServing
            .IsServingAsync(LocalPatchServerUrl, config?.GameRevision)
            .ConfigureAwait(true);

        SetPatchServerCanServe(serving);
        return serving;
    }

    private void SetPatchServerCanServe(bool value) {
        if (_patchServerCanServe == value) {
            return;
        }

        _patchServerCanServe = value;

        // What the Stop button promises depends on it: a patch server that is still fetching is not
        // stopped with the server.
        OnPropertyChanged(nameof(StopButtonLabel));
    }

    /// <summary>
    /// Writes this machine's patch server into Imlight's config now, for a patch server the user starts
    /// themselves.
    /// </summary>
    /// <remarks>
    /// Play does the same thing by itself while this machine's patch server is the one chosen and is up, so
    /// this is for the case it cannot see: a patch server started outside the launcher, whose process the
    /// launcher never watches and whose state it therefore never has. Named the long way round because
    /// <see cref="UseLocalPatchServer"/> is the setting rather than the action.
    /// </remarks>
    [RelayCommand]
    private void UseThisMachinesPatchServer() {
        PatchServerUrl = LocalPatchServerUrl;
        AppendLog(LogLevel.Info,
            $"Patch server set to {PatchServerUrl}. Save settings to apply it.");
    }

    /// <summary>
    /// The patch server went down on its own. Not a failure of the session - Imlight keeps its cached
    /// files - but the local assets have stopped being served, which is worth saying.
    /// </summary>
    private void OnPatchServerExited(int exitCode) {
        AppendLog(LogLevel.Warning, exitCode == 0
            ? "The patch server stopped on its own."
            : $"The patch server stopped on its own (exit code {exitCode}).");

        // A fetch that has just finished is usually too recent to be removed yet, so this normally finds
        // nothing to do - but a patch server that was stopped is a good moment to look, and either way the
        // list on screen becomes a list of what is actually there.
        if (CleansUpRevisionsAutomatically) {
            _ = CleanUpUnusedPatchServerRevisionsAsync();
        }

        RefreshDerived();
    }

    /// <summary>
    /// Writes the revision the patch server holds into Imlight's config, for the mirror being on.
    /// </summary>
    /// <returns>
    /// Whether <c>GameRevision</c> changed, so a caller that has already asked a question about it can
    /// ask again - the answer it holds is about the revision the server was asking for before this.
    /// </returns>
    private bool ApplyMirroredRevision(IReadOnlyList<string> revisions) {
        if (!PatchServerRevisionSynced) {
            return false;
        }

        var config = _config ?? ImlightConfig.TryLoad(ServerPath);
        var wanted = RevisionToMirror(GameRevision, revisions);

        if (config is null || wanted is null) {
            return false;
        }

        // The value the config held before the launcher's first write, recorded once and only then, so
        // turning the sync off later puts the server's own revision back rather than the launcher's last one.
        _store.Settings.RevisionBeforeMirror ??= config.GameRevision;

        try {
            config.GameRevision = wanted;
            config.Save(ServerPath);
        }
        catch (Exception ex) {
            // A read-only config is not a failed session: Imlight keeps whatever revision it already
            // reads, which is the behaviour the mirror is off for.
            AppendLog(LogLevel.Warning,
                $"Could not set Imlight's revision from the patch server ({ex.Message}).");
            return false;
        }

        _config = config;
        _store.Settings.MirroredRevision = wanted;
        _store.Save();

        // Setting it is what puts it on screen, the same way a reload of the config would.
        GameRevision = wanted;

        AppendLog(LogLevel.Success,
            $"Imlight now asks the patch server for {wanted} and accepts clients on it, which is what the "
            + "patch server holds (GameRevision).");

        return true;
    }

    /// <summary>
    /// Puts the value the config had before the mirror took hold back, for the mirror being turned off.
    /// </summary>
    /// <returns>The value written, or <c>null</c> when the config was left alone.</returns>
    private string? RestoreMirroredRevision() {
        var settings = _store.Settings;
        var config = _config ?? ImlightConfig.TryLoad(ServerPath);
        var wanted = RevisionToRestore(
            settings.MirroredRevision, settings.RevisionBeforeMirror, config?.GameRevision);

        settings.MirroredRevision = null;

        if (wanted is null || config is null) {
            return null;
        }

        try {
            config.GameRevision = wanted;
            config.Save(ServerPath);
            _config = config;
            GameRevision = wanted;

            // Nothing is left to undo once the launcher's own value is out of the config.
            settings.RevisionBeforeMirror = null;

            return wanted;
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Warning, $"Could not put GameRevision back ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// The revision to write into <c>GameRevision</c> so the server asks the patch server for what it
    /// actually holds, or <c>null</c> when there is nothing to write.
    /// </summary>
    /// <remarks>
    /// Imlight asks a patch server for <c>V_{GameRevision}</c>, and the same value is the revision the
    /// server accepts clients on. So a config pinned to a build the patch server does not hold is a 404
    /// during boot and, with the revision check on, a refused login - which is why the mirror is stated
    /// here as a rule that can be tested without a patch server, a server or a client.
    /// </remarks>
    public static string? RevisionToMirror(string? currentGameRevision, IReadOnlyList<string>? revisions) {
        var newest = PatchServerConfig.NewestGameRevision(revisions);

        return newest is null || ClientBuildReader.AreSame(newest, currentGameRevision) ? null : newest;
    }

    /// <summary>
    /// The <c>GameRevision</c> to put back when the mirror is turned off, or <c>null</c> when the config
    /// should be left alone.
    /// </summary>
    /// <remarks>
    /// Only the launcher's own writing is undone. A config that no longer holds what the launcher wrote -
    /// because it was edited by hand, or because a newer server build brought its own revision - belongs
    /// to whoever put it there, and is not something the launcher may overwrite on its way out.
    /// </remarks>
    public static string? RevisionToRestore(string? mirrored, string? before, string? current) {
        if (string.IsNullOrWhiteSpace(mirrored) || before is null
            || !ClientBuildReader.AreSame(mirrored, current)) {
            return null;
        }

        return ClientBuildReader.AreSame(before, current) ? null : before;
    }

    /// <summary>What the line under the <c>GameRevision</c> box says.</summary>
    private void RefreshMirrorStatus() {
        if (!PatchServerRevisionSynced) {
            PatchServerRevisionStatus = string.Empty;
            return;
        }

        var mirrored = _store.Settings.MirroredRevision;

        PatchServerRevisionStatus = mirrored switch {
            null or "" => "Waiting for the patch server: the revision is set from it as soon as one is "
                          + "running.",
            _ when ClientBuildReader.AreSame(mirrored, GameRevision) =>
                $"Set from the patch server: {mirrored}.",
            _ => $"The config has moved on from {mirrored} since; it is set from the patch server again the "
                 + "next time it is asked.",
        };
    }

    // ---------------------------------------------------------------- the revisions on disk

    /// <summary>
    /// How the revisions the patch server has fetched are looked after - see
    /// <see cref="PatchServerCleanupChoices"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Automatic is the recommended answer and the default, and choosing it <i>is</i> the permission the
    /// launcher needs to remove a revision on the user's behalf: it is stated before anything is downloaded,
    /// it can be changed at any time, and nothing is ever removed that the server asks for or that this
    /// machine's game is. Manual is the same cleanup with a person in the middle of it - the list below, and
    /// nothing happens until they press something.
    /// </para>
    /// <para>
    /// Switching to automatic cleans up on the spot rather than waiting for the next start, because a
    /// setting that says one thing while the disk says another is worse than either.
    /// </para>
    /// </remarks>
    public string PatchServerRevisionCleanup {
        get => _store.Settings.PatchServerRevisionCleanup;
        set {
            var chosen = PatchServerCleanupChoices.All.Contains(value)
                ? value
                : PatchServerCleanupChoices.Automatic;

            if (_store.Settings.PatchServerRevisionCleanup == chosen) {
                return;
            }

            _store.Settings.PatchServerRevisionCleanup = chosen;
            _store.Save();

            AppendLog(LogLevel.Info, chosen == PatchServerCleanupChoices.Automatic
                ? "The patch server's revisions are looked after automatically from now on: the revision the "
                  + "server asks for and the one your game is are kept, and any other is removed."
                : "The patch server's revisions are yours to manage from now on: ViewLight will not remove "
                  + "one unless you say so in Settings.");

            OnPropertyChanged();
            RefreshDerived();

            if (chosen == PatchServerCleanupChoices.Automatic) {
                _ = CleanUpUnusedPatchServerRevisionsAsync();
            }
        }
    }

    /// <summary>
    /// The arrangement, as Settings states it while the sync is on: which value moves, and why that one.
    /// </summary>
    public const string PatchServerRevisionModeExplanation =
        "Imlight asks the patch server for V_{GameRevision}, and that same revision is the one the server "
        + "accepts clients on. A patch server fetches whatever revision Wizard101 is serving and there is no "
        + "way to ask it for a different one, so the value that has to move is the server's: GameRevision is "
        + "set to the revision the patch server actually holds, every time the launcher starts one. Both "
        + "values then say the same thing - the server asks for files the patch server has, and the client "
        + "that has just been updated is the revision the server accepts.\n\n"
        + "The GameRevision box in Network is greyed out while this is on, because the value is the patch "
        + "server's rather than yours. Only this machine's patch server is asked, so with a remote one in "
        + "use this does nothing at all.";

    /// <summary>
    /// What the other arrangement is, in the words Settings shows while the sync is off.
    /// </summary>
    public const string PatchServerRevisionFallbackText =
        "GameRevision is left exactly as your server's own config has it, pinned the way Imlight was "
        + "published. That is right for a server build deliberately held at one revision - and it means the "
        + "patch server is the side left needing something it cannot fetch, because it can only ever hold "
        + "what Wizard101 is serving.\n\n"
        + "The one way round it is the hand-off in this card: the files of the revision your server asks for "
        + "are copied in from a copy of the game that is exactly that revision. That is the only way a server "
        + "pinned to an older revision gets its files, and it needs this machine's game to be the same "
        + "revision - if it is not, nothing can be copied and the server cannot serve its own clients.\n\n"
        + "Turning this back on puts the value your config had before the launcher wrote one back.";

    // The three texts above, as instance members: a XAML binding resolves a property and not a field, so the
    // words live in a constant - where a test can read them and where they cannot drift from what the code
    // says - and the view reads them through these. Named apart from the constants so the two are never
    // mistaken for one another.
    public string PatchServerRevisionModeText => PatchServerRevisionModeExplanation;

    public string PatchServerRevisionFallbackWarningText => PatchServerRevisionFallbackText;

    public string PatchServerCleanupRecommendationText => PatchServerCleanupRecommendation;

    /// <summary>The two answers, for the radio buttons that offer them.</summary>
    public IReadOnlyList<string> PatchServerCleanupModes => PatchServerCleanupChoices.All;

    public bool CleansUpRevisionsAutomatically
        => PatchServerRevisionCleanup == PatchServerCleanupChoices.Automatic;

    public bool ManagesRevisionsByHand => !CleansUpRevisionsAutomatically;

    /// <summary>
    /// Why automatic is what the launcher recommends, said before it is chosen rather than after.
    /// </summary>
    public const string PatchServerCleanupRecommendation =
        "Each revision is about 18 GB, and the patch server fetches a new one every time Wizard101 "
        + "publishes one - it writes it beside the ones it already has and never removes anything, so a "
        + "machine that has been running one for a while is holding all of them. Keeping several is useful "
        + "to a data hoarder or to an advanced server arrangement, and for everybody else it is a disk that "
        + "only ever fills up.\n\n"
        + "Automatic keeps the two revisions that are actually in use - the one your server asks the patch "
        + "server for, and the one your game is - and removes the others. Choosing it is you giving "
        + "ViewLight permission to do that on your behalf: it is asked once, before anything is downloaded, "
        + "you can change your mind here at any time, and every revision it removes is written into the Log "
        + "with what it was and why. Nothing that is being downloaded, nothing your server asks for, and "
        + "nothing your game is can ever be removed.\n\n"
        + "Manual leaves every revision where it is and gives you the list below instead: keep what you "
        + "want, remove what you do not.";

    /// <summary>The revisions the patch server has fetched, for the manual list.</summary>
    public ObservableCollection<PatchServerRevisionRow> PatchServerRevisions { get; } = [];

    public bool HasPatchServerRevisions => PatchServerRevisions.Count > 0;

    /// <summary>
    /// What the list says when there is nothing in it, which is the answer on a patch server that has not
    /// started yet rather than a mistake.
    /// </summary>
    public string PatchServerRevisionsEmptyText => string.IsNullOrWhiteSpace(PatchServerPath)
        ? "No patch server folder is set."
        : "The patch server has not fetched a revision yet, so there is nothing to manage.";

    /// <summary>What the last look at the revisions found, in one line.</summary>
    [ObservableProperty] private string _patchServerRevisionsStatus = string.Empty;

    public bool HasPatchServerRevisionsStatus => PatchServerRevisionsStatus.Length > 0;

    /// <summary>Whether a cleanup or a look is going on, so the buttons know to keep still.</summary>
    [ObservableProperty] private bool _isManagingPatchServerRevisions;

    /// <summary>Whether anything in the list is marked for removal.</summary>
    public bool CanDeleteUnkeptRevisions
        => !IsManagingPatchServerRevisions && PatchServerRevisions.Any(row => !row.Keep && row.CanBeDeleted);

    /// <summary>
    /// Reads the revisions off the disk and fills the list in.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: this walks a folder holding tens of gigabytes of files to count and weigh them, and
    /// nothing on screen is waiting for the answer. The rows keep whatever was ticked before a refresh only
    /// for the revisions that are still there, which is the behaviour a list of files should have.
    /// </remarks>
    [RelayCommand]
    private async Task RefreshPatchServerRevisionsAsync() {
        var patchServer = PatchServerPath;
        var serverRevision = ServerWantedRevision;
        var ticked = PatchServerRevisions.Where(row => row.Keep).Select(row => row.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadAny = PatchServerRevisions.Count > 0;

        var revisions = await Task
            .Run(() => PatchServerRevisionCatalogue.List(patchServer, serverRevision))
            .ConfigureAwait(true);

        PatchServerRevisions.Clear();

        foreach (var revision in revisions) {
            // Ticked if it was ticked before, and otherwise if it is one of the ones in use - so a list opened
            // for the first time opens on the answer automatic mode would have arrived at by itself.
            var keep = hadAny
                ? ticked.Contains(revision.Name) || revision.Role != PatchServerRevisionRole.Unused
                : revision.Role != PatchServerRevisionRole.Unused;

            PatchServerRevisions.Add(new PatchServerRevisionRow(revision, keep, RequestRevisionDownloadAsync));
        }

        PatchServerRevisionsStatus = DescribeRevisions(revisions);

        OnPropertyChanged(nameof(HasPatchServerRevisions));
        OnPropertyChanged(nameof(PatchServerRevisionsEmptyText));
        OnPropertyChanged(nameof(HasPatchServerRevisionsStatus));
        OnPropertyChanged(nameof(CanDeleteUnkeptRevisions));
        DeleteUnkeptRevisionsCommand.NotifyCanExecuteChanged();
        CleanUpUnusedPatchServerRevisionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the revisions on disk add up to, in one line: how many there are, what is in use, and what is
    /// only taking up room.
    /// </summary>
    public static string DescribeRevisions(IReadOnlyList<PatchServerRevisionOnDisk>? revisions) {
        if (revisions is null || revisions.Count == 0) {
            return string.Empty;
        }

        var spare = PatchServerRevisionCatalogue.Spare(revisions);
        var total = revisions.Sum(revision => revision.Bytes);

        var head = revisions.Count == 1
            ? $"The patch server holds 1 revision ({ServerInstallService.FormatBytes(total)})."
            : $"The patch server holds {revisions.Count} revisions ({ServerInstallService.FormatBytes(total)} "
              + "altogether).";

        return spare.Count == 0
            ? $"{head} They are all in use."
            : $"{head} {spare.Count} of them "
              + $"({ServerInstallService.FormatBytes(spare.Sum(r => r.Bytes))}) "
              + (spare.Count == 1 ? "is not in use" : "are not in use")
              + ".";
    }

    /// <summary>
    /// Removes every revision nobody is using - the automatic mode's whole job, and the button in its card.
    /// </summary>
    /// <remarks>
    /// Given the spare revisions and nothing else: the catalogue re-checks each one immediately before it
    /// goes, so a fetch that starts in between is not something this can get wrong.
    /// </remarks>
    /// <summary>
    /// The pass the launcher makes on its own account, on the way in and when a patch server stops.
    /// </summary>
    /// <remarks>
    /// Quiet on purpose. Nobody asked for this one, it runs on a thread of its own while the launcher is
    /// still coming up, and the Log is a collection the UI thread owns - so the outcome is reported on the
    /// card in Settings, which is where somebody would look for it, rather than written into a Log that is
    /// being built at the same time. The button in that card is a person asking, and that one is written.
    /// </remarks>
    private Task LookAfterPatchServerRevisionsAsync() => CleanUpUnusedPatchServerRevisionsCoreAsync(log: false);

    [RelayCommand(CanExecute = nameof(CanManagePatchServerRevisions))]
    private Task CleanUpUnusedPatchServerRevisionsAsync()
        => CleanUpUnusedPatchServerRevisionsCoreAsync(log: true);

    private async Task CleanUpUnusedPatchServerRevisionsCoreAsync(bool log) {
        var patchServer = PatchServerPath;

        // Nothing to look at and nobody to say it to: a launcher with no patch server folder has no
        // revisions, and this runs on startup when the mode is automatic.
        if (string.IsNullOrWhiteSpace(patchServer) || !Directory.Exists(patchServer)) {
            return;
        }

        var serverRevision = ServerWantedRevision;

        await RemovePatchServerRevisionsAsync(
            () => PatchServerRevisionCatalogue.Spare(
                PatchServerRevisionCatalogue.List(patchServer, serverRevision)),
            onlySpare: true, log: log).ConfigureAwait(true);
    }

    private bool CanManagePatchServerRevisions => !IsManagingPatchServerRevisions;

    /// <summary>
    /// Removes the revisions the manual list has left unticked - the user's own decision, taken as given.
    /// </summary>
    /// <remarks>
    /// The one thing it does not take as given is a revision the patch server is writing into, which is
    /// re-checked at the moment of removal and refused either way.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDeleteUnkeptRevisions))]
    private async Task DeleteUnkeptRevisionsAsync() {
        var unticked = PatchServerRevisions
            .Where(row => !row.Keep && row.CanBeDeleted)
            .Select(row => row.Revision)
            .ToList();

        if (unticked.Count == 0) {
            return;
        }

        await RemovePatchServerRevisionsAsync(() => unticked, onlySpare: false, log: true).ConfigureAwait(true);
    }

    private async Task RemovePatchServerRevisionsAsync(
        Func<IReadOnlyList<PatchServerRevisionOnDisk>> choose, bool onlySpare, bool log) {

        if (IsManagingPatchServerRevisions) {
            return;
        }

        IsManagingPatchServerRevisions = true;
        RefreshPatchDerived();

        try {
            var result = await Task
                .Run(() => PatchServerRevisionCatalogue.Delete(choose(), onlySpare))
                .ConfigureAwait(true);

            if (log && result.RemovedAnything) {
                AppendLog(LogLevel.Success, result.Describe());
            }
            else if (log && result.Kept.Count > 0) {
                AppendLog(LogLevel.Info, result.Describe());
            }

            // A revision that has just gone changes what is left, so the list is read again. The refresh
            // describes what is left; what just happened outranks it, because after pressing Delete the
            // question is what it did - and the list underneath already shows what is there now.
            await RefreshPatchServerRevisionsAsync().ConfigureAwait(true);

            if (result.RemovedAnything || result.Kept.Count > 0) {
                PatchServerRevisionsStatus = result.Describe();
                OnPropertyChanged(nameof(HasPatchServerRevisionsStatus));
            }
        }
        catch (Exception ex) {
            // Never a failed session: a folder that would not go is a folder that stays.
            if (log) {
                AppendLog(LogLevel.Warning, $"Could not clean up the patch server's revisions: {ex.Message}");
            }
        }
        finally {
            IsManagingPatchServerRevisions = false;
            RefreshPatchDerived();
        }
    }

    /// <summary>
    /// Asks for one specific revision to be downloaded - and says why it cannot be, which is what it is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aurorium fetches the revision Wizard101's own patch server is serving <i>at that moment</i>, and there
    /// is no way to ask it for an older one: its configuration says which patch host to talk to and nothing
    /// about which revision to want. So the launcher cannot promise a download of a revision named in a list,
    /// and it does not pretend to. What it can do is tell the difference between the two reasons somebody
    /// would ask - the newest one, which is already here or already on its way, and an older one, which can
    /// never be asked for - and say which it is.
    /// </para>
    /// <para>
    /// This is the rule the launcher works under everywhere else in this card, said out loud rather than
    /// implied: the patch server manages what it fetches, and the launcher's part is to say what is going on.
    /// </para>
    /// </remarks>
    private async Task RequestRevisionDownloadAsync(PatchServerRevisionRow row) {

        var newest = PatchServerIsRunning
            ? PatchServerConfig.NewestGameRevision(
                await _patchRevisions.ListAsync(LocalPatchServerUrl).ConfigureAwait(true))
            : null;

        if (ClientBuildReader.AreSame(row.GameRevision, newest)) {
            PatchServerRevisionsStatus =
                $"{row.GameRevision} is the newest revision the patch server has, and it is already on disk. "
                + "It is the revision Wizard101 was serving when it was fetched, so there is nothing left to "
                + "download for it.";
        }
        else {
            PatchServerRevisionsStatus =
                $"{row.GameRevision} cannot be asked for. The patch server fetches whatever revision Wizard101 "
                + "is serving when it looks - it is told which patch host to talk to and not which revision "
                + "to want - so an older one can be kept or removed here, but it cannot be fetched again once "
                + "it is gone.";
        }

        AppendLog(LogLevel.Info, PatchServerRevisionsStatus);
        OnPropertyChanged(nameof(HasPatchServerRevisionsStatus));
    }

    // ---------------------------------------------------------------- first run: preparing the files now

    /// <summary>
    /// Whether the setup screen's offer to prepare the patch server's files has been answered.
    /// </summary>
    /// <remarks>
    /// The screen is left as soon as the choice is confirmed, so this is per screen rather than persisted: it
    /// is what stops the offer sitting there after somebody has said "not now" and gone on.
    /// </remarks>
    [ObservableProperty] private bool _patchServerPrepareAnswered;

    /// <summary>
    /// Whether the setup screen should be asking about preparing the files.
    /// </summary>
    /// <remarks>
    /// Only where there is something to prepare and somewhere to put it: a patch server that is not
    /// installed has nothing to start, and one that is already up - because it was started from Settings,
    /// or by hand - is already fetching. Its own <i>state</i> rather than its process, because a patch server
    /// the launcher did not start has a state of "stopped" and is still one there is nothing to offer.
    /// </remarks>
    public bool ShouldOfferToPreparePatchServerFiles
        => UseLocalPatchServer && PatchServerInstalled && !PatchServerPrepareAnswered
           && !PatchServerIsRunning
           && PatchServerState is ServerState.Stopped or ServerState.Faulted;

    public bool CanPreparePatchServerFilesNow => ShouldOfferToPreparePatchServerFiles && !IsPatchBusy;

    /// <summary>
    /// Starts the patch server from the setup screen, so its first fetch is under way before the first
    /// session rather than after it.
    /// </summary>
    /// <remarks>
    /// The fetch is the patch server's own, done its own way; what this adds is the moment it starts. The
    /// screen stays where it is and the line and bar under it fill in while it works - the same two the Log
    /// and Settings show - so "preparing" is something being watched rather than something to take on trust.
    /// Leaving the screen does not stop it: the fetch only ever advances while the process is alive, and the
    /// launcher keeps it alive until the session is over.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPreparePatchServerFilesNow))]
    private async Task PreparePatchServerFilesNowAsync() {
        PatchServerPrepareAnswered = true;
        RefreshDerived();

        AppendLog(LogLevel.Info,
            $"Starting the patch server now so it can fetch the revision Wizard101 is serving into "
            + $"{PatchServerSetupFolder}. It is thousands of files, and the line below says how far along "
            + "it is.");

        await StartPatchServerCoreAsync().ConfigureAwait(true);
        RefreshDerived();
    }

    /// <summary>Leaves the fetch for Play, which is where it would otherwise have started.</summary>
    [RelayCommand]
    private void SkipPreparingPatchServerFiles() {
        PatchServerPrepareAnswered = true;
        RefreshDerived();

        AppendLog(LogLevel.Info,
            "The patch server's first fetch is left for Play: it starts the patch server before the server, "
            + "and the fetch runs while the launcher is open.");
    }

    /// <summary>Everything on screen that is derived from the patch server's own state.</summary>
    private void RefreshPatchDerived() {
        OnPropertyChanged(nameof(PatchServerInstalled));
        OnPropertyChanged(nameof(PatchServerIsRunning));
        OnPropertyChanged(nameof(PatchStateText));
        OnPropertyChanged(nameof(IsPatchWorking));
        OnPropertyChanged(nameof(IsPatchReady));
        OnPropertyChanged(nameof(IsPatchFaulted));
        OnPropertyChanged(nameof(ShowPatchServerRow));
        OnPropertyChanged(nameof(PatchServerRowLabel));
        OnPropertyChanged(nameof(PatchServerRowState));
        OnPropertyChanged(nameof(PatchFolderStatus));
        OnPropertyChanged(nameof(PatchInstalledText));
        OnPropertyChanged(nameof(LatestPatchReleaseText));
        OnPropertyChanged(nameof(LocalPatchServerUrl));
        OnPropertyChanged(nameof(IsPatchUpdateAvailable));
        OnPropertyChanged(nameof(HasPatchCheckError));
        OnPropertyChanged(nameof(PatchServerManaged));
        OnPropertyChanged(nameof(UseLocalPatchServer));
        OnPropertyChanged(nameof(UsesRemotePatchServer));
        OnPropertyChanged(nameof(PatchServerIsThisMachines));
        OnPropertyChanged(nameof(PatchServerPort));
        OnPropertyChanged(nameof(FetchStatus));
        OnPropertyChanged(nameof(HasFetchStatus));
        OnPropertyChanged(nameof(FetchProgress));
        OnPropertyChanged(nameof(IsFetchingGameFiles));
        OnPropertyChanged(nameof(FetchingRevisionIsTheServers));
        OnPropertyChanged(nameof(HasWaitingForPatchServerFilesText));
        OnPropertyChanged(nameof(CanPlayAnyway));
        OnPropertyChanged(nameof(CanStartPatchServer));
        OnPropertyChanged(nameof(CanStopPatchServer));
        OnPropertyChanged(nameof(CanDownloadPatchServer));
        OnPropertyChanged(nameof(AnythingIsRunning));
        OnPropertyChanged(nameof(StopButtonLabel));
        OnPropertyChanged(nameof(ImlightPatchServerPort));
        OnPropertyChanged(nameof(PatchServerPortClashes));
        OnPropertyChanged(nameof(PatchServerPortClashWarning));
        OnPropertyChanged(nameof(PatchServerRevisionSynced));
        OnPropertyChanged(nameof(CanEditGameRevision));

        // The first-run question hangs off the same state: whether one is installed is half of whether it
        // has to be asked, and the folder it would go into is what the screen shows.
        OnPropertyChanged(nameof(PatchServerSetupFolder));
        OnPropertyChanged(nameof(CanCompletePatchServerSetup));
        OnPropertyChanged(nameof(PreloadSourceInUse));
        OnPropertyChanged(nameof(HasPreloadStatus));
        OnPropertyChanged(nameof(CanPreloadPatchServerCache));
        CancelPreloadCommand.NotifyCanExecuteChanged();
        PreloadPatchServerCacheCommand.NotifyCanExecuteChanged();

        // Recomputed rather than refreshed: what it says depends on the config as much as on the toggle.
        RefreshMirrorStatus();
        OnPropertyChanged(nameof(PatchServerRevisionStatus));
        OnPropertyChanged(nameof(HasPatchServerRevisionStatus));

        // The revision list and the setup screen's offer hang off the same state as everything else: whether
        // one is installed, what the server asks for, and whether the patch server is already running.
        OnPropertyChanged(nameof(PatchServerRevisionCleanup));
        OnPropertyChanged(nameof(PatchServerCleanupModes));
        OnPropertyChanged(nameof(PatchServerRevisionModeText));
        OnPropertyChanged(nameof(PatchServerRevisionFallbackWarningText));
        OnPropertyChanged(nameof(PatchServerCleanupRecommendationText));
        OnPropertyChanged(nameof(CleansUpRevisionsAutomatically));
        OnPropertyChanged(nameof(ManagesRevisionsByHand));
        OnPropertyChanged(nameof(HasPatchServerRevisions));
        OnPropertyChanged(nameof(PatchServerRevisionsEmptyText));
        OnPropertyChanged(nameof(HasPatchServerRevisionsStatus));
        OnPropertyChanged(nameof(CanDeleteUnkeptRevisions));
        OnPropertyChanged(nameof(PatchServerPrepareAnswered));
        OnPropertyChanged(nameof(ShouldOfferToPreparePatchServerFiles));
        OnPropertyChanged(nameof(CanPreparePatchServerFilesNow));
        OnPropertyChanged(nameof(ServerWantedRevision));

        StartPatchServerCommand.NotifyCanExecuteChanged();
        StopPatchServerCommand.NotifyCanExecuteChanged();
        DownloadPatchServerCommand.NotifyCanExecuteChanged();
        CompletePatchServerSetupCommand.NotifyCanExecuteChanged();
        GetPatchServerForSetupCommand.NotifyCanExecuteChanged();
        DeleteUnkeptRevisionsCommand.NotifyCanExecuteChanged();
        CleanUpUnusedPatchServerRevisionsCommand.NotifyCanExecuteChanged();
        PreparePatchServerFilesNowCommand.NotifyCanExecuteChanged();
        PlayAnywayCommand.NotifyCanExecuteChanged();
    }
}
