using CommunityToolkit.Mvvm.ComponentModel;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The launcher's own database: held open for as long as the launcher runs, because it is the launcher's
/// and nobody else's.
/// </summary>
/// <remarks>
/// <para>
/// This is a second Raven instance beside the one the server's database runs in, started from the same
/// binaries with a data directory under the launcher's own root - see <see cref="LauncherDatabase"/>. The
/// difference between the two is the whole reason this file exists: the server's database is borrowed, so it
/// is opened while Imlight is idle and given back before anything is started, while this one is the
/// launcher's own, so it is started once and kept for the run. Nothing else opens it, and there is no
/// handover to make.
/// </para>
/// <para>
/// Started when the requirement screen is satisfied - an install and a runtime are what running it takes -
/// and never on a launcher that is stopped on that screen, where there is nothing to keep notes about yet.
/// A database that will not start is a status rather than a crash: the launcher still runs, and what it
/// cannot do is say so.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    private readonly EmbeddedRaven _launcherDatabase = new();

    /// <summary>
    /// The client the notes are read and written through, once the database is up.
    /// </summary>
    /// <remarks>
    /// Held for the run: it is what owns the connection the profile screen reads pictures over, and a client
    /// per read would be a connection per read.
    /// </remarks>
    private LauncherDatabaseClient? _launcherDatabaseClient;

    /// <summary>
    /// What the launcher's own database is doing, or why it is not doing anything.
    /// </summary>
    [ObservableProperty] private string _launcherDatabaseStatus = string.Empty;

    public bool HasLauncherDatabaseStatus => LauncherDatabaseStatus.Length > 0;

    partial void OnLauncherDatabaseStatusChanged(string value)
        => OnPropertyChanged(nameof(HasLauncherDatabaseStatus));

    /// <summary>Whether the launcher's own database is up and holding its documents.</summary>
    public bool IsLauncherDatabaseUp => _launcherDatabase.IsRunning;

    /// <summary>Where those documents are, for anybody who wants to look at them or back them up.</summary>
    public string LauncherDatabaseDirectory => LauncherDatabase.DataDirectoryFor(LauncherPaths.DataRoot);

    /// <summary>
    /// The start that is running, so that asking twice does not open two databases on one data directory.
    /// </summary>
    /// <remarks>
    /// The same single-flight guard the player database's open has, and for the same reason: an open takes tens of
    /// seconds, and two callers can easily overlap inside that window - the start of the run, an install finishing,
    /// a cloud sync handing the databases back. The second ask is answered with the first answer rather than by
    /// racing it onto the same port, where the loser's client would be left pointing at a process that was killed.
    /// </remarks>
    private Task? _launcherDatabaseAttempt;

    /// <summary>
    /// Starts the launcher's own database, if it is not already running and can be.
    /// </summary>
    /// <remarks>
    /// The status is written before the wait, because a database takes tens of seconds to come up and a
    /// screen that says nothing for that long looks like one that is not trying. Nothing here blocks: the
    /// caller fires this and carries on, which is why it is a task rather than a property.
    /// </remarks>
    private Task EnsureLauncherDatabaseAsync() {
        if (_launcherDatabaseAttempt is { IsCompleted: false } attempt) {
            return attempt;
        }

        _launcherDatabaseAttempt = OpenLauncherDatabaseAsync();
        return _launcherDatabaseAttempt;
    }

    /// <summary>
    /// Whether the launcher is waiting for the profiles database to come up.
    /// </summary>
    /// <remarks>
    /// A RavenDB start is seconds of work and the profile screen has nothing to show until it answers, which
    /// reads as an account that does not exist rather than as one that is being loaded. The window shows a
    /// card while this is true, saying what it is waiting for; it is true for exactly as long as an attempt is
    /// running, so a failed attempt takes it down and leaves the status line and the Log to explain.
    /// </remarks>
    [ObservableProperty] private bool _isLauncherDatabaseStarting;

    partial void OnIsLauncherDatabaseStartingChanged(bool value) => RefreshNotifications();

    /// <summary>The open itself, which is what <see cref="EnsureLauncherDatabaseAsync"/> remembers.</summary>
    private async Task OpenLauncherDatabaseAsync() {
        // Nothing to wait for when there is nothing to open, and nothing to say about it either.
        if (_launcherDatabase.IsRunning || RequiresImlight || _config is null) {
            return;
        }

        IsLauncherDatabaseStarting = true;

        try {
            await OpenLauncherDatabaseCoreAsync().ConfigureAwait(true);
        }
        finally {
            IsLauncherDatabaseStarting = false;
        }
    }

    /// <summary>The attempt itself, with the window already saying that it is under way.</summary>
    private async Task OpenLauncherDatabaseCoreAsync() {
        if (_launcherDatabase.IsRunning) {
            return;
        }

        // A launcher stopped on the requirement screen is not keeping notes about anything yet, and has no
        // install to run a database from.
        if (RequiresImlight || _config is null) {
            return;
        }

        // The engine these notes run on is the launcher's own copy, made before anything is started - a copy
        // replaces a folder a running database would have open.
        await MakeOwnEngineAsync().ConfigureAwait(true);

        var setup = LauncherDatabase.For(_config, ServerPath, LauncherPaths.DataRoot, out var unusable);
        if (setup is null) {
            LauncherDatabaseStatus = $"{unusable} The launcher's own notes cannot be kept until it is fixed.";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            return;
        }

        LauncherDatabaseStatus = "Starting the launcher's own database…";
        RefreshDerived();

        var failed = await StartAndWaitAsync(setup).ConfigureAwait(true);

        // The engine inside the install is the second answer, and is tried only when the first one was the
        // launcher's own copy: a copy made from an engine older than the data it was asked to open, or one a
        // half-finished copy left damaged, is not a reason for the launcher to have no notes at all.
        if (failed is not null && LauncherDatabase.RunsOnItsOwnEngine(setup, LauncherPaths.DataRoot)) {
            var borrowed = LauncherDatabase.ForServerEngine(
                _config, ServerPath, LauncherPaths.DataRoot, out var noInstallEngine);

            if (borrowed is not null) {
                AppendLog(LogLevel.Warning,
                    $"The launcher's own copy of the database engine did not start ({failed}) - "
                    + "trying the one the server install ships instead.");

                setup = borrowed;
                failed = await StartAndWaitAsync(borrowed).ConfigureAwait(true);
            }
            else if (noInstallEngine is { Length: > 0 }) {
                AppendLog(LogLevel.Info, noInstallEngine);
            }
        }

        if (failed is not null) {
            LauncherDatabaseStatus = $"{failed} The launcher's own notes cannot be kept until it is fixed.";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            RefreshDerived();
            return;
        }

        LauncherDatabaseStatus = $"The launcher's own database is running on port {setup.Port}, in "
                                 + $"{setup.DataDirectory}.";
        AppendLog(LogLevel.Success, LauncherDatabaseStatus);

        // Which runtime and which engine are doing that is worth knowing, and is not something a user can see
        // for themselves: on a machine with no .NET of its own it is the runtime in the launcher's own folder,
        // and the engine is the launcher's own copy unless that failed and the install's was borrowed.
        if (_launcherDatabase.HostDescription is { Length: > 0 } runtime) {
            AppendLog(LogLevel.Info,
                $"It was started with {runtime}, on {LauncherDatabase.EngineDescription(setup, LauncherPaths.DataRoot)}.");
        }

        // The database the notes live in, made if this is the first run. Raven makes a database when a
        // document store first asks for it, so this is the ask.
        var client = new LauncherDatabaseClient(setup.Port);
        var persistence = new DatabaseProfilePersistence(client);

        if (!persistence.Prepare()) {
            client.Dispose();

            LauncherDatabaseStatus = $"{LauncherDatabaseStatus} Its own database could not be made, so the "
                                     + $"launcher's notes cannot be kept yet ({client.Problem}).";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            RefreshDerived();
            return;
        }

        _launcherDatabaseClient = client;

        // From here the profiles are read and written in the database rather than anywhere else.
        _profiles.Attach(persistence);

        // A launcher that has been used before has its notes in a file the database knows nothing about, so
        // they are moved in now - once, and recorded in the database as having been moved, so restoring a
        // backup cannot pour an old file over newer notes.
        var imported = await ProfilesImport.RunAsync(client, LauncherPaths.DataRoot).ConfigureAwait(true);

        if (imported.State is ProfilesImportState.Imported) {
            AppendLog(LogLevel.Success,
                $"Moved {imported.Profiles} profile(s) and {imported.Pictures} picture(s) out of "
                + $"{ProfilesImport.FileName} into the launcher's own database; the file is kept as "
                + $"{ProfilesImport.ImportedFileName}.");

            _profiles.Reload();
        }
        else if (imported.State is ProfilesImportState.Unreadable) {
            AppendLog(LogLevel.Warning,
                $"{ProfilesImport.FileName} was left alone: {imported.Message} The profiles already in the "
                + "database are the ones shown.");
        }

        // The ledger is told again because it took its copy of the learned worlds while the store was still
        // empty, and the screen is rebuilt because the profiles it was offering up to now were none.
        _ledger.ReloadFromStore();

        if (_profiles.Problem is { Length: > 0 } storeProblem) {
            AppendLog(LogLevel.Warning, storeProblem);
        }
        else {
            AppendLog(LogLevel.Success,
                $"The launcher's own notes are kept in that database: {_profiles.Profiles.Count} profile(s).");
        }

        RefreshProfiles();
        RefreshDerived();

        // Whether anybody is signed in is a document in this database, so the screen can say so from the moment
        // it is up - a launcher that is signed in must not look like one that has to be signed in again, however
        // long it takes for anything to sync.
        await ReadCloudSignInAsync().ConfigureAwait(true);

        // The cloud backup runs from here, and only now: it needs the sign-in kept in this database, and it
        // has to close it again to take the copy - so the earliest it can run is after this has finished.
        MaybeSyncWithCloudInBackground();
    }

    /// <summary>
    /// Starts a database and waits for it to answer: <c>null</c> when it did, and why not when it did not.
    /// </summary>
    private async Task<string?> StartAndWaitAsync(EmbeddedRavenSetup setup) {
        var failed = await _launcherDatabase.StartAsync(setup).ConfigureAwait(true);

        if (failed is not null) {
            ReportDatabaseOutput("The launcher's own database", setup, _launcherDatabase.OutputLines);
            return failed;
        }

        if (await _launcherDatabase.WaitUntilAnsweringAsync(setup).ConfigureAwait(true)) {
            return null;
        }

        var why = _launcherDatabase.Problem ?? "The database did not come up.";
        await _launcherDatabase.StopAsync().ConfigureAwait(true);

        // What the database printed on the way up, which is the only thing that knows why it never got there.
        ReportDatabaseOutput("The launcher's own database", setup, _launcherDatabase.OutputLines);
        return why;
    }

    /// <summary>
    /// Keeps the launcher's own copy of the database engine current, before anything is run on it.
    /// </summary>
    /// <remarks>
    /// It is made from the installed server, so it is the version Imlight ships and follows an update -
    /// nothing has to be downloaded for it and nothing has to be shipped with the launcher. Called on the way
    /// in, which is before the database opens, and again after an install, which is the other moment it can be:
    /// nothing is running out of the copy then, so the folder can be replaced wholesale. On the UI thread this
    /// would be a freeze of a second or two, so it is done off it.
    /// </remarks>
    private async Task MakeOwnEngineAsync() {
        if (_config is not { } config) {
            return;
        }

        var serverPath = ServerPath;
        var hadOne = LauncherRavenEngine.IsComplete(LauncherPaths.DataRoot);

        if (!hadOne) {
            LauncherDatabaseStatus = "Making the launcher's own copy of the server's database engine…";
            RefreshDerived();
        }

        var result = await Task.Run(() => LauncherRavenEngine.MakeFresh(config, serverPath, LauncherPaths.DataRoot))
            .ConfigureAwait(true);

        if (result.Problem is { Length: > 0 } problem) {
            // Not fatal on its own: with no copy, the engine inside the install is borrowed and the launcher
            // works exactly as it did before there was a copy.
            AppendLog(LogLevel.Info, problem);
            return;
        }

        if (result.Copied) {
            AppendLog(LogLevel.Success,
                $"Kept the launcher's own copy of the server's RavenDB ({result.Version ?? "version unknown"}) "
                + $"in {result.Directory}: {result.Files} files, "
                + $"{ServerInstallService.FormatBytes(LauncherRavenEngine.SizeOf(result.Directory))}. "
                + "The launcher's own notes now run on that copy rather than out of the server's folder; the "
                + "player database still uses the engine the server install ships, because its data is the "
                + "server's.");
        }
    }

    /// <summary>Lets go of the client the launcher's notes were read and written through.</summary>
    private void CloseLauncherDatabaseClient() {
        _launcherDatabaseClient?.Dispose();
        _launcherDatabaseClient = null;
    }

    /// <summary>
    /// Lets go of everything the launcher is running out of the server's own folder, because that folder is
    /// about to be written over.
    /// </summary>
    /// <remarks>
    /// <b>This is what an update used to fail on.</b> Both of the launcher's databases are RavenDB processes
    /// started with the server's own binaries, out of <c>RavenDBServer</c> under the server path - so while
    /// either is up, its DLLs are loaded, and an overlay that writes over them ends in "the process cannot
    /// access the file ... because it is being used by another process" and an install that did not happen.
    /// Neither database is anything the launcher cannot do without for a minute: they are read again the moment
    /// they are back, which <see cref="EnsureLauncherDatabaseAsync"/> and <see cref="EnsureIdleDatabaseAsync"/> do
    /// as soon as the writing is over.
    /// </remarks>
    private async Task ReleaseDatabasesForInstallAsync() {
        // The game is the one thing that is not the launcher's to end on the way past an install button.
        // Its server is about to be stopped and its files replaced, so an install that continued would
        // disconnect a session that looked like it was going to keep working.
        if (ClientIsRunning || ClientIsStarting) {
            throw new InvalidOperationException(
                "Wizard101 is running. Close it and press the install button again: the server has to stop "
                + "before the files it is running from can be replaced.");
        }

        // A running server holds the very binaries the overlay came to replace. It is stopped here, at
        // the last moment before the writing rather than before the download or the build, so a build
        // that fails or a download that is cancelled costs nothing that was running. Stopping it through
        // _server directly, and not through StopServerAsync, is deliberate: the latter settles the
        // launcher's own database back onto the folder that is about to be written.
        if (ServerIsBusyOrUp || _server.IsRunning) {
            AppendLog(LogLevel.Info,
                "Stopping the server so the new build can be written over the old one…");

            StopProfileWatch();
            EndPlayingSession();
            await _server.StopAsync().ConfigureAwait(true);

            Phase = LauncherPhase.Idle;
            RefreshDerived();
        }

        // A server started outside the launcher - from a shortcut, a terminal, or a previous launcher
        // session - is not _server, so nothing above has stopped it, and it is holding the very
        // Imlight.Director.exe and DLLs the overlay came to replace. That is the one case where a build
        // finishes cleanly and the files are still the old ones: the copy cannot open them, and the
        // install reports a locked file. The same guard Play uses applies here, and it is deliberately
        // limited to a Director started from this install - somebody else's server is not ours to end.
        if (ServerProcesses.StopAll(ImlightConfig.ExecutablePathFor(ServerPath), AppendLog).Count > 0) {
            Phase = LauncherPhase.Idle;
            RefreshDerived();
        }

        // Whether there was anything to let go of, worked out before the stopping rather than after it: a fresh
        // install has neither database up, and a log line claiming they were stopped would be a small lie in the
        // one place a user looks when something goes wrong.
        var wasHolding = _launcherDatabase.IsRunning || SomethingHoldsThePlayerDatabase(
            _idleDatabaseAttempt is { IsCompleted: false }, _idleDatabaseIsUp, _raven.IsRunning);

        // The client first, then the process it talks to - the order the cloud sync uses too. Letting go of the
        // client without stopping the database would leave the profiles attached to a connection nobody owns.
        CloseLauncherDatabaseClient();
        await _launcherDatabase.StopAsync().ConfigureAwait(true);

        // The player database is the server's, and it is held only while the server is not running - which is
        // exactly when an update tends to be made.
        await ReleaseIdleDatabaseAsync().ConfigureAwait(true);

        if (wasHolding) {
            AppendLog(LogLevel.Info, "Stopped the launcher's databases so the server's files can be replaced.");
        }
    }
}
