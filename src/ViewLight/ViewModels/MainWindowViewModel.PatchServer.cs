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

    /// <summary>
    /// Asks whether the patch server should be filled in from a local install before this session's server
    /// is started. Set by the window, which is the only thing that can put a dialog up.
    /// </summary>
    public Func<PatchServerPreloadPlan, Task<PreloadOfferAnswer>>? AskAboutPreloadAsync { get; set; }

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
    /// How long to wait for the patch server to write the revision's own file list, which is what a copy
    /// into it is planned against.
    /// </summary>
    private static readonly TimeSpan PatchServerFileListWait = TimeSpan.FromSeconds(20);

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
    /// Whether the <c>GameRevision</c> box in Settings can be typed in. It cannot while the mirror is on,
    /// because the value is the patch server's rather than the user's, and a box that takes edits it is
    /// about to overwrite is worse than one that says so.
    /// </summary>
    public bool CanEditGameRevision => !MirrorPatchServerRevision;

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
    /// Whether a patch server with nothing in it is filled in from a copy of the game without being asked.
    /// </summary>
    /// <remarks>
    /// Off by default: the copy is tens of gigabytes read and written, and when to spend that is the user's
    /// decision - which is what the offer before a session is for. On, the offer stops being a question and
    /// Play just does it, saying so in the Log.
    /// </remarks>
    public bool PatchServerPreloadAutomatically {
        get => _store.Settings.PatchServerPreloadAutomatically;
        set {
            if (_store.Settings.PatchServerPreloadAutomatically == value) {
                return;
            }

            _store.Settings.PatchServerPreloadAutomatically = value;
            _store.Save();

            AppendLog(LogLevel.Info, value
                ? "A patch server that holds nothing for the session's revision will be filled in from the "
                  + "game folder on this machine as part of Play."
                : "The copy into the patch server is offered before a session again rather than done by "
                  + "itself.");

            OnPropertyChanged();
            RefreshDerived();
        }
    }

    /// <summary>
    /// Whether the launcher makes the server accept the revision the patch server holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two disagree in the case this exists for. A server build pins the revision it was published
    /// with, and a patch server can only hold what Wizard101 currently offers, so a client that has been
    /// patched past the server build is asking for a revision the patch server has and the server does
    /// not: Imlight requests <c>V_{GameRevision}</c>, gets a 404, and - with the revision check on - turns
    /// the same client away at login. With this on, the revision the patch server actually holds is
    /// written into <c>GameRevision</c>, so the address Imlight asks for exists and the client that was
    /// patched into it is the one the server accepts.
    /// </para>
    /// <para>
    /// Applied whenever this machine's patch server answers with the revisions it holds - on Play, and
    /// when a patch server is started from Settings. Turning it off puts back the value the config had
    /// when it was turned on, and only that value: a config that has moved on since is somebody else's
    /// to keep.
    /// </para>
    /// </remarks>
    public bool MirrorPatchServerRevision {
        get => _store.Settings.MirrorPatchServerRevision;
        set {
            if (_store.Settings.MirrorPatchServerRevision == value) {
                return;
            }

            _store.Settings.MirrorPatchServerRevision = value;

            if (value) {
                // Read before anything is written, so turning it off has the release's own value to put
                // back rather than the launcher's.
                _store.Settings.RevisionBeforeMirror =
                    (_config ?? ImlightConfig.TryLoad(ServerPath))?.GameRevision;
                _store.Settings.MirroredRevision = null;

                AppendLog(LogLevel.Info,
                    "The revision the server accepts (GameRevision) is taken from the patch server from "
                    + "now on.");
            }
            else {
                var restored = RestoreMirroredRevision();

                AppendLog(LogLevel.Info, restored is null
                    ? "The launcher no longer sets GameRevision; it is left as the config has it."
                    : $"GameRevision is back to {restored}, the value it had before it was set from the "
                      + "patch server.");
            }

            _store.Save();
            OnPropertyChanged();
            RefreshDerived();

            // A patch server that is already up can answer straight away, so the box fills in without
            // waiting for Play. One that is not is asked the next time it is started.
            if (value && PatchServerIsRunning) {
                _ = ApplyPatchServerRevisionAsync();
            }
        }
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

        var (plan, source) = await Task
            .Run(() => PlanPreload(patchServer, configured, ownGameFolder))
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
    /// The plan for the best install on this machine: the one that is set, and failing that the launcher's
    /// own game folder, and failing that whatever install can be found.
    /// </summary>
    /// <remarks>
    /// A folder only counts as a source when the plan built from it is possible - which means the patch
    /// server is holding that build's revision and has the file list to prove it. An install of a different
    /// revision is skipped rather than refused, because that is the ordinary state of a machine with an
    /// out-of-date game on it: there is simply nothing here to copy from, which is not worth a sentence.
    /// </remarks>
    /// <param name="alsoConsider">
    /// The installs found on this machine, or <c>null</c> to look for them. Given rather than found by the
    /// caller so that what a machine happens to have installed is not what this rule is about.
    /// </param>
    public static (PatchServerPreloadPlan? Plan, string Source) PlanPreload(
        string? patchServerFolder, string? configuredSource, string? ownGameFolder,
        IEnumerable<string>? alsoConsider = null) {

        PatchServerPreloadPlan? first = null;
        var firstSource = string.Empty;

        foreach (var folder in PreloadSourceCandidates(configuredSource, ownGameFolder, alsoConsider)) {
            if (!GameFolderLocator.LooksLikeGameFolder(folder)) {
                continue;
            }

            PatchServerPreloadPlan plan;
            try {
                plan = PatchServerPreload.Plan(folder, patchServerFolder, ClientBuildReader.Read(folder));
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
            ? (PatchServerPreload.Plan(null, patchServerFolder, null), string.Empty)
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
    /// Offers to fill the patch server in from a local install, before the server this session needs is
    /// started.
    /// </summary>
    /// <remarks>
    /// Asked rather than done: it is tens of gigabytes read and written, and only the user knows whether
    /// this is the moment for it. It is asked before the server starts because that is the whole point - a
    /// patch server that holds the revision is one Imlight loads <c>Root.wad</c> from instead of falling
    /// back to its own cache - and it is asked once per revision, so a "no" is an answer rather than
    /// something to argue with every time Play is pressed. The one exception is
    /// <see cref="PatchServerPreloadAutomatically"/>, which does it unasked; the asking is the setting, not
    /// the rule.
    /// </remarks>
    public async Task OfferPreloadAsync() {
        // Nothing here is about somebody else's patch server, and that is checked before the wait below
        // rather than after: a launcher pointed at a remote one should not stall Play for a file list it is
        // never going to copy into.
        if (!PatchServerIsThisMachines) {
            return;
        }

        var plan = await RefreshPreloadPlanAsync().ConfigureAwait(true);

        // The endpoint answers before the revision's file list has been written - the patch server binds its
        // port first and works out what to fetch second - so a session started straight after it comes up
        // can be one where there is nothing to plan against yet. It arrives within seconds; the wait is
        // bounded, and only for the first start after a patch server is installed.
        if (plan is { IsPossible: false } && PreloadSourceInUse.Length > 0) {
            var folder = PatchServerPreload.RevisionFolderFor(
                PatchServerPath, ClientBuildReader.Read(PreloadSourceInUse));

            var deadline = DateTimeOffset.Now.Add(PatchServerFileListWait);
            while (!PatchServerManifest.Exists(folder) && DateTimeOffset.Now < deadline) {
                await Task.Delay(500).ConfigureAwait(true);
            }

            plan = await RefreshPreloadPlanAsync().ConfigureAwait(true);
        }

        // Done rather than offered, for the setting that says so. No dialog and no remembered refusal,
        // because a patch server that holds nothing is exactly the case the setting is about.
        if (plan is not null && ShouldPreloadWithoutAsking(PatchServerPreloadAutomatically, plan)) {
            AppendLog(LogLevel.Info, PreloadAutomaticMessage(plan));
            await CopyForPlayAsync(plan).ConfigureAwait(true);
            return;
        }

        if (plan is null || !ShouldOfferPreload(PatchServerIsThisMachines, plan,
                _store.Settings.PatchServerPreloadDeclinedRevision)) {
            return;
        }

        if (AskAboutPreloadAsync is null) {
            // No window to ask in: said in the Log, which is all this can do without one.
            AppendLog(LogLevel.Info, PreloadOfferDetail(plan));
            return;
        }

        var answer = await AskAboutPreloadAsync(plan).ConfigureAwait(true);

        if (answer.DontAskAgain) {
            _store.Settings.PatchServerPreloadDeclinedRevision = plan.Revision;
            _store.Save();
        }

        if (!answer.Preload) {
            AppendLog(LogLevel.Info, PreloadDeclinedMessage(plan));
            return;
        }

        await CopyForPlayAsync(plan).ConfigureAwait(true);
    }

    /// <summary>
    /// Copies a plan in as part of starting a session, with Home saying so while it happens.
    /// </summary>
    /// <remarks>
    /// The phase is set and put back around the copy rather than left to the caller, because both ways of
    /// getting here - the offer answered yes, and the setting that does not ask - spend the same minutes of
    /// a session in the same place.
    /// </remarks>
    private async Task CopyForPlayAsync(PatchServerPreloadPlan plan) {
        var before = Phase;
        Phase = LauncherPhase.CopyingGameFiles;

        try {
            await CopyCacheInAsync(plan).ConfigureAwait(true);
        }
        finally {
            Phase = before == LauncherPhase.CopyingGameFiles ? LauncherPhase.Idle : before;
        }
    }

    /// <summary>
    /// Whether Play should offer a copy at all.
    /// </summary>
    /// <remarks>
    /// Three conditions, and each of them is somebody's decision rather than a nicety. It is only asked
    /// about this machine's own patch server - the one the user pointed the launcher at - because writing
    /// into the folder of somebody else's is not the launcher's to do, however much good it would do. There
    /// has to be something to copy that this install can supply, or the question is nothing. And it is asked
    /// once per revision, because a "no" about one revision says nothing about the next.
    /// </remarks>
    public static bool ShouldOfferPreload(
        bool patchServerIsThisMachines, PatchServerPreloadPlan? plan, string? declinedRevision)
        => patchServerIsThisMachines && plan is { IsWorthDoing: true }
           && !ClientBuildReader.AreSame(declinedRevision, plan.Revision);

    /// <summary>
    /// Whether Play fills the patch server in without asking - the setting, and something worth doing.
    /// </summary>
    public static bool ShouldPreloadWithoutAsking(bool preloadAutomatically, PatchServerPreloadPlan? plan)
        => preloadAutomatically && plan is { IsWorthDoing: true };

    /// <summary>What the Log says when the copy is made without being asked.</summary>
    public static string PreloadAutomaticMessage(PatchServerPreloadPlan plan)
        => $"Filling the patch server in from {plan.SourceFolder}: {plan.FilesToCopy:N0} files "
           + $"({ServerInstallService.FormatBytes(plan.BytesToCopy)}) of {plan.Revision}, because Settings "
           + "says to do that without asking. Files it already holds are left alone.";

    /// <summary>What the offer asks.</summary>
    public static string PreloadOfferHeadline(PatchServerPreloadPlan plan)
        => $"Copy {plan.FilesToCopy:N0} files ({ServerInstallService.FormatBytes(plan.BytesToCopy)}) into the "
           + "patch server?";

    /// <summary>What the offer explains: why these files, and why a copy rather than a download.</summary>
    public static string PreloadOfferDetail(PatchServerPreloadPlan plan)
        => $"{plan.SourceFolder} already holds {plan.Revision}, so these are the same files the patch server "
           + "would download for itself. A copy is local, so it is minutes rather than hours - and the files "
           + "large enough to run past the patch server's own download limit are ones only a copy can supply.";

    /// <summary>What the Log says when the offer is turned down.</summary>
    public static string PreloadDeclinedMessage(PatchServerPreloadPlan plan)
        => $"Playing without filling the patch server in. It is still missing {plan.FilesToCopy:N0} files "
           + $"({ServerInstallService.FormatBytes(plan.BytesToCopy)}) and will fetch what it can; the copy is "
           + "under Settings whenever it is wanted.";

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
    /// Asks the patch server which revision it holds, and writes it into Imlight's config when the mirror
    /// is on.
    /// </summary>
    /// <remarks>
    /// The one request the launcher makes to a patch server, and it is only made when the mirror is on -
    /// nothing else needs an answer from it. Whether the files behind that revision have finished arriving
    /// is not asked, and not acted on: the patch server serves what it has, and a fetch still in flight is
    /// the patch server's business rather than a reason for the launcher to point Imlight somewhere else.
    /// </remarks>
    private async Task ApplyPatchServerRevisionAsync() {
        if (!MirrorPatchServerRevision || !PatchServerIsRunning) {
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
            return;
        }

        var revision = config?.GameRevision ?? GameRevision;

        AppendLog(LogLevel.Warning,
            $"The patch server at {LocalPatchServerUrl} has not fetched {revision}'s files yet, so it does "
            + $"not have {PatchServerServing.BootFile} to serve. This session loads Root.wad from "
            + "Imlight's own cache, or stops on it if it has none - and while this launcher is open the "
            + "fetch keeps running, so pressing Play again once it has finished gets its files from here.");
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
        if (!MirrorPatchServerRevision) {
            return false;
        }

        var config = _config ?? ImlightConfig.TryLoad(ServerPath);
        var wanted = RevisionToMirror(GameRevision, revisions);

        if (config is null || wanted is null) {
            return false;
        }

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
        if (!MirrorPatchServerRevision) {
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
        OnPropertyChanged(nameof(PatchServerPreloadAutomatically));
        OnPropertyChanged(nameof(PatchServerPort));
        OnPropertyChanged(nameof(CanStartPatchServer));
        OnPropertyChanged(nameof(CanStopPatchServer));
        OnPropertyChanged(nameof(CanDownloadPatchServer));
        OnPropertyChanged(nameof(AnythingIsRunning));
        OnPropertyChanged(nameof(StopButtonLabel));
        OnPropertyChanged(nameof(ImlightPatchServerPort));
        OnPropertyChanged(nameof(PatchServerPortClashes));
        OnPropertyChanged(nameof(PatchServerPortClashWarning));
        OnPropertyChanged(nameof(MirrorPatchServerRevision));
        OnPropertyChanged(nameof(CanEditGameRevision));
        OnPropertyChanged(nameof(PreloadSourceInUse));
        OnPropertyChanged(nameof(HasPreloadStatus));
        OnPropertyChanged(nameof(CanPreloadPatchServerCache));
        CancelPreloadCommand.NotifyCanExecuteChanged();
        PreloadPatchServerCacheCommand.NotifyCanExecuteChanged();

        // Recomputed rather than refreshed: what it says depends on the config as much as on the toggle.
        RefreshMirrorStatus();
        OnPropertyChanged(nameof(PatchServerRevisionStatus));
        OnPropertyChanged(nameof(HasPatchServerRevisionStatus));

        StartPatchServerCommand.NotifyCanExecuteChanged();
        StopPatchServerCommand.NotifyCanExecuteChanged();
        DownloadPatchServerCommand.NotifyCanExecuteChanged();
    }
}
