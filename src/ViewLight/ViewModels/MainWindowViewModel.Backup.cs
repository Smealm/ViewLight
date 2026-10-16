using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Models;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The cloud backup: a copy of the two databases that cannot be downloaded again, kept in the user's own
/// Google Drive and put back on a machine that has lost them.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is copied, and what is not.</b> The launcher's own database and the server's own RavenDB - the
/// accounts, the wizards, the profiles, the playtime and the badges - and nothing else. Not the game's files,
/// not the client, not the server binaries, not the cached art and downloads, and not <c>Imlight.ini</c>: the
/// first four can be fetched again, and the last is machine-specific paths that would be wrong on the machine
/// doing the restoring.
/// </para>
/// <para>
/// <b>Syncing is only possible while neither database is open.</b> RavenDB holds its data directory under an
/// exclusive lock, so the launcher's own database, the server's own RavenDB, and the server
/// (<see cref="DatabaseProfilePersistence"/>) over the launcher's database can all be backed up or restored
/// only when that lock is not held. The sync gates on <see cref="ServerIsBusyOrUp"/> (the server is running or
/// starting, so it owns its database or the launcher cannot hand it back) and on
/// <see cref="_launcherDatabaseClient"/> (the launcher's own database is open and so cannot be copied), and it
/// closes both before it copies anything and reopens them when it is done.
/// </para>
/// <para>
/// <b>A sync runs at three moments, and all three are the launcher's own.</b> When the launcher's own database
/// comes up, which is the earliest it can read the sign-in from; when a server is about to start, on the way in;
/// and when one has stopped. The first and the last are found by the launcher itself and asked for in the
/// background, from <see cref="CloudSyncMayStart"/> - one rule, read in one place rather than inferred from its
/// callers. The middle one is asked for by the start that is taking the databases - see
/// <c>StartServerAsync</c> - because that caller is the one about to need them, and it waits for the answer. A
/// manual backup or restore is the fourth way in, and can only be chosen when the launcher is idle with the
/// server stopped.
/// </para>
/// <para>
/// <b>The launcher can stand down its own database briefly, on purpose, when the server is not running.</b> The
/// launcher is the one that starts the server and manages its own connection to its own database, so when the
/// server is not running the launcher may let go of its database, run the sync, and reconnect without asking
/// anybody - the gap between the databases being open is the whole thing the sync needs. It does not do this while
/// a server is running, because that server owns a database that is not the launcher's to close. On the way into
/// a server it closes the launcher's own database for the copy and starts it again in the background afterwards,
/// and leaves the player's alone: Imlight is about to open that one, and starting it here only to stop it again
/// would be a RavenDB boot paid for on the way into every session.
/// </para>
/// <para>
/// <b>Nothing is edited while a sync has the databases closed.</b> The notes live in one of the databases the
/// sync stops, so a setting changed or a picture picked in that window is a change the launcher could not keep:
/// the window says so (<see cref="CloudSyncNotice"/>) and turns the pages off (<see cref="IsEditingAllowed"/>) for
/// as long as it lasts.
/// </para>
/// <para>
/// <b>The ordinary case says nothing.</b> A sync that finds both sides already agree records nothing and says
/// nothing; the Log gets a line and the status does not change. Only a backup, a restore, a real conflict or a
/// failure is worth a sentence.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>Set while a sync is running, so the automatic one and a button press cannot overlap.</summary>
    private bool _cloudSyncInFlight;

    /// <summary>The sync that is running, so a start that arrives during one can wait for it.</summary>
    /// <remarks>
    /// Waiting rather than starting a second one: a sync has both databases closed and reopened underneath it,
    /// so a Play that came in the middle of one cannot be handed a database that is being copied.
    /// </remarks>
    private Task? _cloudSyncTask;

    /// <summary>
    /// Set while the databases are being handed over to a server that is about to start.
    /// </summary>
    /// <remarks>
    /// Play releases the player database and spawns Imlight with it, and this is that window - the one moment the
    /// sync is driven by the caller rather than by the gate, because the caller is the one about to take the
    /// databases. Nothing else may start a sync while it is up: the automatic path would otherwise take the
    /// database back, and start it again, underneath the server's own boot.
    /// </remarks>
    private bool _handingOverToServer;

    /// <summary>
    /// Whether the cloud backup has the databases closed and the launcher paused right now.
    /// </summary>
    /// <remarks>
    /// Shown on screen while it is true, because everything else that would write is refused while it holds:
    /// the notes live in a database the sync has stopped, so a picture set or a setting changed in that window
    /// would be a change the launcher cannot keep.
    /// </remarks>
    [ObservableProperty] private bool _isCloudSyncRunning;

    partial void OnIsCloudSyncRunningChanged(bool value) {
        OnPropertyChanged(nameof(IsEditingAllowed));
        OnPropertyChanged(nameof(HasCloudSyncNotice));
    }

    /// <summary>Whether the launcher may be edited at all: not while a sync has the databases closed.</summary>
    public bool IsEditingAllowed => !IsCloudSyncRunning;

    /// <summary>The line the window shows while a sync is running, in the corner the user is looking at.</summary>
    public string CloudSyncNotice => $"Syncing your backup with {CloudBackupProviderName}…";

    /// <summary>The second half of that sentence: what is paused, and until when.</summary>
    public string CloudSyncNoticeDetail
        => "Settings and profile changes - including pictures and banners - are paused until it finishes.";

    public bool HasCloudSyncNotice => IsCloudSyncRunning;

    /// <summary>The service the backup is kept in, named once so the notice and the card agree.</summary>
    public const string CloudBackupProviderName = "Google Drive";

    /// <summary>What the last sync did, or why it did not, in the words the Settings card shows.</summary>
    [ObservableProperty] private string _cloudBackupStatus = string.Empty;

    public bool HasCloudBackupStatus => CloudBackupStatus.Length > 0;

    partial void OnCloudBackupStatusChanged(string value) => OnPropertyChanged(nameof(HasCloudBackupStatus));

    /// <summary>Whether an account is signed in, so a backup can be made.</summary>
    [ObservableProperty] private bool _isCloudBackupSignedIn;

    /// <summary>
    /// The sign-in this run knows about, kept so that a restore cannot take it away.
    /// </summary>
    /// <remarks>
    /// The token itself lives in the launcher's own database, which a restore replaces whole - so a copy taken
    /// before this machine signed in would sign it out on the way back. Held here for the run, the sign-in can
    /// be put back the next time that database comes up; see <see cref="CloudBackupState.ReadRefreshTokenOrRestoreAsync"/>.
    /// Signing out clears it, so nothing comes back from nowhere.
    /// </remarks>
    private string? _cloudRefreshToken;

    /// <summary>
    /// Whether the launcher keeps a backup in the cloud. Off until it is turned on here, because it is the
    /// user's Drive being written to.
    /// </summary>
    public bool BackUpToCloud {
        get => _store.Settings.BackUpToCloud;
        set {
            if (_store.Settings.BackUpToCloud == value) {
                return;
            }

            _store.Settings.BackUpToCloud = value;
            _store.Save();

            AppendLog(LogLevel.Info, value
                ? "ViewLight keeps a backup of its own database and the server's in the cloud from now on."
                : "The cloud backup is off; nothing will be uploaded or restored by itself.");

            OnPropertyChanged();
            RefreshDerived();

            // Turning it on is the moment to do something about it, if there is an account to use.
            if (value && IsCloudBackupSignedIn) {
                _ = RunCloudBackupAsync(manual: true, force: null);
            }
        }
    }

    /// <summary>
    /// Whether the sign-in uses ViewLight's own Google client rather than one the user registered.
    /// </summary>
    /// <remarks>
    /// Two workflows, one button. <i>Bring your own</i> is the user's own Desktop-app OAuth client, with its id
    /// and secret pasted in; <i>ViewLight's own</i> is a client id the launcher carries, so signing in needs no
    /// setup - and it is greyed out until this build actually has one to name.
    /// </remarks>
    public bool CloudBackupUsesBuiltInClient {
        get => _store.Settings.CloudBackupUsesBuiltInClient;
        set {
            if (_store.Settings.CloudBackupUsesBuiltInClient == value) {
                return;
            }

            // A choice that cannot be made is not recorded: the greyed-out button is disabled, and this is the
            // second half of that so a settings file edited by hand cannot put the launcher on a client that
            // does not exist.
            if (value && !GoogleOAuth.HasBuiltInClient) {
                CloudBackupStatus = "This build of ViewLight has no Google client of its own to sign in with, "
                                    + "so the sign-in has to use one you registered.";
                OnPropertyChanged();
                return;
            }

            _store.Settings.CloudBackupUsesBuiltInClient = value;
            _store.Save();

            AppendLog(LogLevel.Info, value
                ? "Signing in with ViewLight's own Google client, so no client of your own is needed."
                : "Signing in with your own Google client; the id and secret below are the ones used.");

            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesOwnGoogleClient));
            OnPropertyChanged(nameof(CanSignInToCloud));
        }
    }

    /// <summary>
    /// Whether the user's own client id and secret are the ones in play - the other side of the choice.
    /// </summary>
    /// <remarks>
    /// The setter is what makes the radio pair work in both directions, the same way
    /// <c>UsesRemotePatchServer</c> does: picking one option is picking the other's absence, and a get-only
    /// property would leave the "bring your own" button unable to be re-selected once the built-in one had
    /// been chosen.
    /// </remarks>
    public bool UsesOwnGoogleClient {
        get => !CloudBackupUsesBuiltInClient;
        set => CloudBackupUsesBuiltInClient = !value;
    }

    /// <summary>Whether this build carries a Google client of its own, which is what the built-in option needs.</summary>
    public bool HasBuiltInGoogleClient => GoogleOAuth.HasBuiltInClient;

    /// <summary>
    /// Why the built-in sign-in cannot be chosen yet, said on the screen so a greyed-out button explains itself.
    /// </summary>
    public string BuiltInClientNotice => GoogleOAuth.HasBuiltInClient
        ? "Signing in needs no setup beyond pressing the button."
        : "ViewLight ships no Google client of its own yet, so this is greyed out until one is registered for "
          + "it. The workflow is in place and needs nothing but a client id; until then, use your own.";

    /// <summary>The user's own Google OAuth client id, for the bring-your-own workflow.</summary>
    public string CloudBackupClientId {
        get => _store.Settings.CloudBackupClientId;
        set {
            if (_store.Settings.CloudBackupClientId == value) {
                return;
            }

            _store.Settings.CloudBackupClientId = value;
            _store.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSignInToCloud));
        }
    }

    /// <summary>That client's secret, when it has one. Blank means no secret is sent.</summary>
    public string CloudBackupClientSecret {
        get => _store.Settings.CloudBackupClientSecret;
        set {
            if (_store.Settings.CloudBackupClientSecret == value) {
                return;
            }

            _store.Settings.CloudBackupClientSecret = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a sign-in is being waited on right now: the browser has been opened and nothing has come back.
    /// </summary>
    /// <remarks>
    /// Shown on screen, because it is the state the launcher used to have no way out of: the sign-in button is
    /// pressed, the browser window is closed without consenting, and nothing ever arrives - so what is offered
    /// while this is true is the way back, see <see cref="CancelCloudSignIn"/>.
    /// </remarks>
    [ObservableProperty] private bool _isCloudSignInWaiting;

    partial void OnIsCloudSignInWaitingChanged(bool value) {
        OnPropertyChanged(nameof(CanSignInToCloud));
        OnPropertyChanged(nameof(CanCancelCloudSignIn));
    }

    /// <summary>
    /// What stops the sign-in being waited on, or <c>null</c> when none is.
    /// </summary>
    /// <remarks>
    /// Held for the run of one sign-in and dropped when it ends, so a cancel that arrives after the wait is
    /// over cancels nothing.
    /// </remarks>
    private CancellationTokenSource? _cloudSignIn;

    /// <summary>Whether the sign-in can be stopped, which is exactly while it is being waited on.</summary>
    public bool CanCancelCloudSignIn => IsCloudSignInWaiting;

    /// <summary>
    /// Whether a sign-in can start: the built-in client, or an id of the user's own - and not while one is
    /// already being waited on, so a second press cannot leave the first wait running with nothing pointing at
    /// it.
    /// </summary>
    public bool CanSignInToCloud
        => !IsCloudSignInWaiting
           && (CloudBackupUsesBuiltInClient
               ? GoogleOAuth.HasBuiltInClient
               : GoogleOAuth.Normalize(CloudBackupClientId).Length > 0);

    /// <summary>Stops the sign-in that is being waited on, so the user can go back to the button that started it.</summary>
    [RelayCommand]
    private void CancelCloudSignIn() => _cloudSignIn?.Cancel();

    /// <summary>
    /// Signs in to Google in the user's own browser, and keeps the refresh token that comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The password never reaches the launcher: the browser is Google's own page, and a loopback socket on
    /// this machine receives the one redirect. What is kept is the refresh token, in the launcher's own
    /// database - a credential that has to work while nobody is watching, which is what an unattended upload
    /// is.
    /// </para>
    /// <para>
    /// <b>The wait is cancellable, because the browser can be taken away.</b> Closing the sign-in window leaves
    /// nothing for the loopback socket to receive and no way for the launcher to notice, so the wait is
    /// registered against a token and <see cref="CancelCloudSignIn"/> is what stops it - the user is never left
    /// on a screen with no way back to the button that started this.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task SignInToCloudAsync() {
        if (_launcherDatabaseClient is null) {
            CloudBackupStatus = "The launcher's own database is not up yet, so there is nowhere to keep the "
                                + "sign-in. Try again in a moment.";
            return;
        }

        // The client the choice names, and the secret that goes with it: none for the built-in one, and the
        // user's own - possibly also none - for the other. Both are cleaned up first, once, so what is signed
        // in with is what is stored; see CleanUpPastedCredentials.
        var id = GoogleOAuth.Normalize(CloudBackupUsesBuiltInClient ? GoogleOAuth.BuiltInClientId : CloudBackupClientId);
        var secret = GoogleOAuth.Normalize(CloudBackupUsesBuiltInClient ? null : CloudBackupClientSecret);

        if (id.Length == 0) {
            CloudBackupStatus = "A Google client id is needed before anything can be signed in: register an "
                                + "OAuth client and paste its id below.";
            return;
        }

        CleanUpPastedCredentials(id, secret);

        using var signIn = new CancellationTokenSource();

        _cloudSignIn = signIn;
        IsCloudSignInWaiting = true;

        CloudBackupStatus = "Waiting for you to sign in with Google in your browser… If you close that window, "
                            + "press Cancel sign-in to come back here.";

        GoogleAuth answer;

        try {
            answer = await GoogleOAuth.SignInAsync(id, secret, OpenInBrowser, token: signIn.Token)
                .ConfigureAwait(true);
        }
        finally {
            // Dropped whatever happened, so the Cancel button only ever stops a wait that is still running.
            _cloudSignIn = null;
            IsCloudSignInWaiting = false;
        }

        if (answer.Tokens?.RefreshToken is not { Length: > 0 } refresh) {
            CloudBackupStatus = answer.Problem ?? "Google did not return a token that lets the launcher back up "
                                                  + "on its own, so nothing was saved.";
            AppendLog(LogLevel.Warning, CloudBackupStatus);
            return;
        }

        var kept = await CloudBackupState.WriteRefreshTokenAsync(_launcherDatabaseClient, refresh, account: null)
            .ConfigureAwait(true);

        if (!kept) {
            // Checked rather than assumed, because a sign-in that was never stored is the whole of "it asks me
            // to sign in again": the card would say signed in, and the next start would have nothing to read.
            CloudBackupStatus = "Signed in to Google, but the sign-in could not be kept "
                                + $"({_launcherDatabaseClient.Problem}), so the next start will ask for it "
                                + "again.";
            AppendLog(LogLevel.Warning, CloudBackupStatus);
            return;
        }

        _cloudRefreshToken = refresh;
        IsCloudBackupSignedIn = true;
        CloudBackupStatus = "Signed in to Google Drive. ViewLight will keep the backup in the app's own folder.";
        AppendLog(LogLevel.Success, CloudBackupStatus);

        // The moment to make the first backup is now, so a signed-in launcher is a backed-up one.
        await RunCloudBackupAsync(manual: true, force: null).ConfigureAwait(true);
    }

    /// <summary>
    /// Puts the cleaned-up client id and secret back into the settings, so what is sent is what is shown.
    /// </summary>
    /// <remarks>
    /// The settings hold what was pasted, character for character, because that is what a settings box has to
    /// do. What Google is sent is the same value without the whitespace around it - and a trailing space there
    /// is a 401 that reads as a sign-in which did not last rather than as a stray character. Writing the cleaned
    /// value back is what keeps the two from drifting apart, and it cleans up a file that already has one.
    /// </remarks>
    private void CleanUpPastedCredentials(string clientId, string clientSecret) {
        if (CloudBackupUsesBuiltInClient) {
            return;
        }

        var changed = false;

        if (_store.Settings.CloudBackupClientId != clientId) {
            _store.Settings.CloudBackupClientId = clientId;
            changed = true;
        }

        if (_store.Settings.CloudBackupClientSecret != clientSecret) {
            _store.Settings.CloudBackupClientSecret = clientSecret;
            changed = true;
        }

        if (!changed) {
            return;
        }

        _store.Save();

        OnPropertyChanged(nameof(CloudBackupClientId));
        OnPropertyChanged(nameof(CloudBackupClientSecret));
        OnPropertyChanged(nameof(CanSignInToCloud));
    }

    /// <summary>Forgets the account, so the next launcher starts signed out.</summary>
    [RelayCommand]
    private async Task SignOutOfCloudAsync() {
        if (_launcherDatabaseClient is not null) {
            await CloudBackupState.ClearRefreshTokenAsync(_launcherDatabaseClient).ConfigureAwait(true);
        }

        // Forgotten here as well as in the database: this run's copy is what a restore is healed from, and a
        // sign-out that left one behind would put the account back the next time the database came up.
        _cloudRefreshToken = null;
        IsCloudBackupSignedIn = false;
        CloudBackupStatus = "Signed out. The backup already in the cloud is left where it is.";
        AppendLog(LogLevel.Info, CloudBackupStatus);
    }

    /// <summary>Makes a backup now, whatever the plan would have said - this machine's copy is the one kept.</summary>
    [RelayCommand]
    private Task BackUpNowAsync() => RunCloudBackupAsync(manual: true, force: BackupSyncAction.Upload);

    /// <summary>Takes the cloud's copy now, whatever the plan would have said - the stored backup is the one kept.</summary>
    [RelayCommand]
    private Task RestoreFromCloudAsync() => RunCloudBackupAsync(manual: true, force: BackupSyncAction.Download);

    /// <summary>
    /// Runs one sync: what is here hashed, what is there read, and whichever side the plan names moved.
    /// </summary>
    /// <param name="manual">Whether a person asked, which is what makes it speak when there is nothing to do.</param>
    /// <param name="force">
    /// <see cref="BackupSyncAction.Upload"/> or <see cref="BackupSyncAction.Download"/> to override the plan
    /// with a person's choice, or <c>null</c> to let the plan decide.
    /// </param>
    /// <param name="handOverToServer">
    /// Whether the databases are being handed to a server that is about to start. The sync still closes both to
    /// copy them, and then gives the player's back to nobody: the server is about to open it, and starting it
    /// here only to stop it again is work the launcher would pay for twice on the way into a session. The
    /// launcher's own database is started again in the background, because it is the launcher's and no server
    /// ever competes with it for the folder.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Syncing is only possible while neither database is open.</b> RavenDB holds its data directory under
    /// an exclusive lock, so the launcher's own database, the server's own RavenDB, and the server
    /// (<see cref="DatabaseProfilePersistence"/>) over the launcher's database can all be backed up or restored
    /// only when that lock is not held. The sync therefore gates on <see cref="ServerIsBusyOrUp"/> (the server is
    /// running or starting, so it owns its database or the launcher cannot hand it back) and on
    /// <see cref="_launcherDatabaseClient"/> (the launcher's own database is open and so cannot be copied),
    /// and it closes both before it copies anything and reopens them when it is done.
    /// </para>
    /// <para>
    /// <b>The windows it uses are the ones the launcher already creates.</b> A manual backup or restore can
    /// only be chosen when the launcher is idle with the server stopped, so there is nothing automatic to
    /// time it to - the user picks the moment and the launcher takes it. The launcher reaches for a sync on
    /// its own at launcher start, once its own database is up, and at server stop, once the server has handed
    /// the player database back and the launcher has taken it up again - both times the server is already
    /// down, so both databases are free to be copied without waiting on them. The third moment is the one on
    /// the way into a server: the start that takes the databases asks for a sync first, and waits for it - see
    /// <paramref name="handOverToServer"/>.
    /// </para>
    /// <para>
    /// <b>The launcher can stand down its own database briefly, on purpose, when the server is not running.</b>
    /// The launcher is the one that starts the server and manages its own connection to its own database, so
    /// when the server is not running the launcher may let go of its database, run the sync, and reconnect
    /// without asking anybody - the gap between the databases being open is the whole thing the sync needs.
    /// It does not do this while a server is running, because that server owns a database that is not the
    /// launcher's to close; if the server is stopped the launcher waits until the player database is free
    /// before backing it up, and backs up the server's database before the server is started again, once the
    /// launcher has started the player database so both sides are known.
    /// </para>
    /// </remarks>
    private async Task RunCloudBackupAsync(bool manual, BackupSyncAction? force, bool handOverToServer = false) {
        if (_cloudSyncInFlight) {
            // A start that arrives while the launcher is already syncing waits for it rather than starting a
            // second one: the databases are closed underneath that sync, and the server cannot be given one that
            // is being copied.
            if (handOverToServer) {
                await (_cloudSyncTask ?? Task.CompletedTask).ConfigureAwait(true);
            }
            else if (manual) {
                CloudBackupStatus = "A backup is already running; this press did not start a second one.";
            }

            return;
        }

        if (!BackUpToCloud) {
            if (manual) {
                CloudBackupStatus = "Turn the cloud backup on first.";
            }

            return;
        }

        if (_config is null || RequiresImlight) {
            return;
        }

        // Only the caller that is taking the databases may sync in that window. Everything else - a press, or
        // the automatic path - waits for the server to be up, or to be stopped again.
        if (_handingOverToServer && !handOverToServer) {
            if (manual) {
                CloudBackupStatus = "The server is starting, and its database is being handed to it. "
                                    + "Back up once it is running, or once it has stopped.";
            }

            return;
        }

        if (_launcherDatabaseClient is not { } launcherDatabase) {
            if (manual) {
                CloudBackupStatus = "The launcher's own database is not up yet, so there is nothing to back up. "
                                    + "Try again in a moment.";
            }
            else if (handOverToServer) {
                AppendLog(LogLevel.Info,
                    $"The cloud backup was not taken before the server started: the launcher's own database is "
                    + $"not up yet. It is taken once that database is up, or when the server stops.");
            }

            return;
        }

        // A running Imlight holds its own data directory, and the launcher cannot take it back while the server
        // is on its way up or down either: one process at a time is the whole handover. So a manual backup waits
        // for the server to be down, and the automatic one is not started in the first place - see
        // <see cref="CloudSyncMayStart"/>.
        if (IsServerBusyOrUp(ServerState)) {
            if (manual) {
                CloudBackupStatus = "The server is running, and its database cannot be copied while it is. "
                                    + "Stop the server and back up again.";
            }

            return;
        }

        // Checked again rather than only in the gate, because the gate is what keeps the automatic path from
        // starting and this is what keeps a press from a moment the automatic path would have refused: the
        // player database is in the middle of being opened, so its files are still being written, and a copy
        // taken now would be recorded as the truth about it. A handover is the one caller that goes ahead anyway:
        // it is about to take the database out of the launcher's hands either way, and the release it does first
        // stops an open that is still running rather than waiting for it - see <see cref="ReleaseIdleDatabaseAsync"/>.
        if (!handOverToServer && !PlayerDatabaseIsSettled) {
            if (manual) {
                CloudBackupStatus = "The player database is still starting, so it cannot be copied yet. "
                                    + "Try again in a moment.";
            }

            return;
        }

        _cloudSyncInFlight = true;
        IsCloudSyncRunning = true;

        if (handOverToServer) {
            AppendLog(LogLevel.Info,
                $"Syncing the backup with {CloudBackupProviderName} before the server takes the databases.");
        }

        _cloudSyncTask = RunCloudSyncAsync(manual, force, handOverToServer, launcherDatabase);

        try {
            await _cloudSyncTask.ConfigureAwait(true);
        }
        finally {
            IsCloudSyncRunning = false;
            _cloudSyncInFlight = false;
            _cloudSyncTask = null;
            RefreshDerived();
        }
    }

    /// <summary>
    /// The sync itself, once the launcher has said it is running one.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="RunCloudBackupAsync"/> so the task that is running is a field the next caller
    /// can wait on, and so the flag that says a sync is running comes down however this ends.
    /// </remarks>
    private async Task RunCloudSyncAsync(
        bool manual, BackupSyncAction? force, bool handOverToServer, LauncherDatabaseClient launcherDatabase) {

        try {
            // The database is read through with the client this sync was started for, not through the field:
            // the sync is the thing that closes and reopens it, so which client it is has to be decided once.
            var refresh = await CloudBackupState
                .ReadRefreshTokenAsync(launcherDatabase)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(refresh)) {
                IsCloudBackupSignedIn = false;

                if (manual) {
                    CloudBackupStatus = "No Google account is signed in, so nothing was backed up.";
                }

                return;
            }

            _cloudRefreshToken = refresh;
            IsCloudBackupSignedIn = true;

            var credentials = new GoogleCredentials(
                CloudBackupUsesBuiltInClient ? GoogleOAuth.BuiltInClientId : CloudBackupClientId,
                CloudBackupUsesBuiltInClient ? null : CloudBackupClientSecret,
                refresh);

            // A refusal that is about the sign-in itself rather than about the network, kept out of the store's
            // own problem field so it can be told apart afterwards - see the end of this method.
            GoogleAuth? signInProblem = null;

            // The account is known, so the database is not needed while the sync runs - and the sync cannot
            // run while it is held. Both of the launcher's databases are let go before a byte is copied.
            var store = new GoogleDriveBackupStore(async _ => {
                var answer = await GoogleOAuth.RefreshAsync(credentials).ConfigureAwait(false);
                signInProblem = answer;

                return (answer.Tokens, answer.Problem);
            });
            var directories = BackupSet.Directories(LauncherPaths.DataRoot, _config, ServerPath);
            var cloud = new CloudBackup(store, directories, Path.Combine(LauncherPaths.DataRoot, "backup"));

            try {
                // Nothing may be left holding either data directory while it is copied, so both are let go first -
                // and neither is opened again until the copying is done. Inside the try rather than before it,
                // because letting go of a database is exactly the kind of thing that can fail halfway.
                CloseLauncherDatabaseClient();
                await _launcherDatabase.StopAsync().ConfigureAwait(true);
                await ReleaseIdleDatabaseAsync().ConfigureAwait(true);

                await SyncWithCloudAsync(cloud, force, manual).ConfigureAwait(true);
            }
            finally {
                if (handOverToServer) {
                    // The player database is left to the server that is about to open it, and letting go of it
                    // again is what makes that true even when this ended badly - it is a no-op when it is already
                    // gone. The launcher's own database is not the server's business and comes back in the
                    // background, so the profiles and their pictures are read from it again while Imlight is
                    // booting rather than waiting for it.
                    await ReleaseIdleDatabaseAsync().ConfigureAwait(true);
                    _ = EnsureLauncherDatabaseAsync();
                }
                else {
                    // Whatever happened, the launcher gets its databases back: they are what it reads profiles
                    // and accounts from, and leaving them down would be a worse outcome than a failed sync.
                    await EnsureIdleDatabaseAsync().ConfigureAwait(true);
                    await EnsureLauncherDatabaseAsync().ConfigureAwait(true);
                }
            }

            // A refusal that is about the sign-in itself is the one failure only the user can do anything about,
            // so it is said on the card rather than only in the Log: a Google client that will not refresh is not
            // a backup that failed once, it is a backup that will fail every time until somebody fixes it. And a
            // sign-in Google has stopped honouring takes the card's word for it back - the token is still stored,
            // and pressing Sign in to Google Drive is the only thing that replaces it.
            if (signInProblem is { Problem: { Length: > 0 } refusal }) {
                CloudBackupStatus = refusal;
                AppendLog(LogLevel.Warning, refusal);

                if (signInProblem.SignInAgain) {
                    IsCloudBackupSignedIn = false;
                }
            }
        }
        catch (Exception ex) {
            CloudBackupStatus = $"The cloud backup did not finish ({ex.Message}).";
            AppendLog(LogLevel.Warning, CloudBackupStatus);
        }
    }

    /// <summary>The plan, and then the one thing it names - or the one thing a person named.</summary>
    private async Task SyncWithCloudAsync(CloudBackup cloud, BackupSyncAction? force, bool manual) {
        var ancestor = CloudBackupState.ReadSyncState(LauncherPaths.DataRoot).Ancestor;
        var result = await cloud.PlanAsync(ancestor).ConfigureAwait(true);

        var action = force ?? result.Plan.Action;

        if (action == BackupSyncAction.Nothing) {
            if (manual) {
                CloudBackupStatus = result.Plan.Reason;
            }
            else {
                AppendLog(LogLevel.Info, $"The cloud backup is up to date: {result.Plan.Reason}");
            }

            // A first sync that has nothing to move still leaves the two sides agreeing, so it is recorded -
            // otherwise every start would look like a first sync and ask the same question again.
            if (ancestor is null) {
                CloudBackupState.WriteSyncState(LauncherPaths.DataRoot, result.Local);
            }

            return;
        }

        if (action == BackupSyncAction.Conflict) {
            CloudBackupStatus = $"{result.Plan.Reason} Use “Back up now” to keep this machine's data, or "
                                + "“Restore from the cloud” to take the stored copy.";

            AppendLog(LogLevel.Warning, result.Plan.Reason);
            return;
        }

        if (action == BackupSyncAction.Upload) {
            // A forced upload still refuses a manifest built from a database that could not be read: it would
            // say the database is gone, and the next restore would act on it.
            if (result.LocalProblem is { Length: > 0 } localProblem) {
                CloudBackupStatus = $"Nothing was backed up: this machine's data could not be read ({localProblem}).";
                AppendLog(LogLevel.Warning, CloudBackupStatus);
                return;
            }

            var problem = await cloud.UploadAsync(result.Local).ConfigureAwait(true);

            if (problem is not null) {
                CloudBackupStatus = problem;
                AppendLog(LogLevel.Warning, problem);
                return;
            }

            CloudBackupState.WriteSyncState(LauncherPaths.DataRoot, result.Local);
            CloudBackupStatus = $"Backed up to the cloud: {Describe(result.Local)}.";
            AppendLog(LogLevel.Success, CloudBackupStatus);
            return;
        }

        var (restored, restoreProblem) = await cloud.DownloadAndRestoreAsync().ConfigureAwait(true);

        if (restoreProblem is not null || restored is null) {
            CloudBackupStatus = restoreProblem ?? "The backup could not be restored.";
            AppendLog(LogLevel.Warning, CloudBackupStatus);
            return;
        }

        CloudBackupState.WriteSyncState(LauncherPaths.DataRoot, restored);
        CloudBackupStatus = $"Restored from the cloud: {Describe(restored)}. "
                            + "The launcher's notes are being re-read now.";

        AppendLog(LogLevel.Success, CloudBackupStatus);
    }

    /// <summary>What a manifest holds, in one line, for the Log and the status.</summary>
    private static string Describe(BackupManifest manifest) {
        var parts = new List<string>();

        foreach (var database in BackupDatabases.All) {
            var state = manifest.For(database);

            if (state is null) {
                continue;
            }

            parts.Add(state.Present
                ? $"{BackupDatabases.Describe(database)} ({state.Files:N0} files, {Megabytes(state.Bytes)})"
                : $"{BackupDatabases.Describe(database)}: nothing yet");
        }

        return string.Join(", ", parts);
    }

    private static string Megabytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
            : $"{bytes / (1024.0 * 1024):0.#} MB";

    /// <summary>
    /// Reads whether an account is signed in from the launcher's own database, which is where the sign-in lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read as soon as that database is up, rather than left to a sync to discover.</b> The sign-in outlives
    /// a run - that is the whole reason it is kept in a database instead of in a field - and the card has to say
    /// so from the moment it can be read. Left to a sync, a launcher whose sync never runs - the player database
    /// will not open, the server is up, the feature was just turned off and on - offers a sign-in that already
    /// happened, and signing in again is exactly what a person does when the screen says they are not.
    /// </para>
    /// <para>
    /// <b>Only the absence of the document signs the launcher out.</b> Nothing here clears anything on a failure
    /// of any kind: a sync that could not reach Google is a network that is missing rather than a sign-in that is
    /// gone, and it is the sync that finds out when Google genuinely refuses the token - see
    /// <see cref="RunCloudSyncAsync"/>.
    /// </para>
    /// </remarks>
    private async Task ReadCloudSignInAsync() {
        if (_launcherDatabaseClient is not { } client) {
            return;
        }

        var refresh = await CloudBackupState.ReadRefreshTokenOrRestoreAsync(client, _cloudRefreshToken)
            .ConfigureAwait(true);

        var signedIn = !string.IsNullOrWhiteSpace(refresh);
        _cloudRefreshToken = signedIn ? refresh : null;

        if (IsCloudBackupSignedIn == signedIn) {
            return;
        }

        IsCloudBackupSignedIn = signedIn;

        if (signedIn) {
            AppendLog(LogLevel.Info, "A Google account is signed in, so the backup can be made without you.");
        }
    }

    /// <summary>
    /// Starts a sync in the background, if the cloud backup is on and the moment is right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the automatic path, and it is called from exactly the two moments the launcher owns both databases
    /// at once: once its own is up (<see cref="EnsureLauncherDatabaseAsync"/>), and once the player database has
    /// come back to it (<see cref="EnsureIdleDatabaseAsync"/>, which is what follows a server stopping). Both
    /// times the server is already down, so the sync does not wait on it. It is deliberately not called from
    /// anywhere else - in particular not on the way into Play, where the databases are the server's from that
    /// point: a sync closing them again there would fight the start it is standing in the middle of, which is what
    /// <see cref="CloudSyncMayStart"/> refuses in the first place.
    /// </para>
    /// <para>
    /// Nothing is awaited: this is a moment the launcher has to itself, a failure is a status rather than a
    /// stopped launcher, and no caller has anything to do with the answer.
    /// </para>
/// <para>
/// The in-flight flag is what keeps this from re-entering itself: the sync closes and reopens the launcher's
/// own database when it is done, which calls back here, and the flag is still up at that point. The handover flag
/// is the other half of the same idea - it keeps this path out of the window in which a server is being given the
/// databases, where a sync started here would take them back underneath its boot.
/// </para>
    /// </remarks>
    private void MaybeSyncWithCloudInBackground() {
        if (!CloudSyncMayStart(
                BackUpToCloud, _cloudSyncInFlight, _playInFlight, _launcherDatabaseClient is not null,
                ServerState, PlayerDatabaseIsSettled, _handingOverToServer)) {
            return;
        }

        _ = RunCloudBackupAsync(manual: false, force: null);
    }

    /// <summary>
    /// Whether the automatic sync may start, from what the launcher is doing. Pure, so the rule can be checked on
    /// its own rather than through a launcher with two databases in it.
    /// </summary>
    /// <param name="backUpToCloud">Whether the user has turned the cloud backup on.</param>
    /// <param name="syncInFlight">Whether a sync is already running.</param>
    /// <param name="playInFlight">
    /// Whether a Play is on its way to the server. Play hands the player database over and starts Imlight with it,
    /// so a sync starting in that window would close and reopen the database underneath the start.
    /// </param>
    /// <param name="launcherDatabaseIsUp">Whether the launcher's own database is open - the sign-in is read from it.</param>
    /// <param name="server">What the server is doing.</param>
    /// <param name="playerDatabaseIsSettled">Whether the player database can be copied, or is being opened right now.</param>
    /// <param name="handingOverToServer">
    /// Whether the databases are on their way to a server that is about to start. That is one handover, driven by
    /// the caller that is taking them - see <see cref="RunCloudBackupAsync"/> - so nothing automatic joins it.
    /// </param>
    /// <remarks>
    /// <b>The sync never starts while Imlight is the one with the database, or is on its way to it.</b> A server that
    /// is running, warming up, starting or stopping owns its own data directory, the launcher's job then is to hand it
    /// over rather than to stop it again for a copy, and Play is that handover - so the moments left to the sync are
    /// the ones the launcher already holds both databases in, with nothing else on its way to them. A server that is
    /// merely <see cref="ServerState.Faulted"/> holds nothing, so a sync is allowed there.
    /// </remarks>
    public static bool CloudSyncMayStart(
        bool backUpToCloud, bool syncInFlight, bool playInFlight, bool launcherDatabaseIsUp, ServerState server,
        bool playerDatabaseIsSettled, bool handingOverToServer)
        => backUpToCloud
           && !syncInFlight
           && !playInFlight
           && launcherDatabaseIsUp
           && !IsServerBusyOrUp(server)
           && playerDatabaseIsSettled
           && !handingOverToServer;

    /// <summary>Opens a URL in whatever this machine uses for a browser.</summary>
    private static void OpenInBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
