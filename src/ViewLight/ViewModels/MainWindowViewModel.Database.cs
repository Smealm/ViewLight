using CommunityToolkit.Mvvm.ComponentModel;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The player database without the server: while ViewLight is running and Imlight is not, the launcher
/// keeps the server's own RavenDB going, so accounts and profiles are there the moment the launcher opens
/// rather than only once a server has warmed up.
/// </summary>
/// <remarks>
/// <para>
/// What this is <i>not</i> is a second database. It is the server's own database, opened with the server's
/// own binaries against the server's own data directory - the same documents Imlight reads and writes. The
/// launcher holds it open exactly while nothing else needs it, and gives it up before anything is started.
/// </para>
/// <para>
/// That handover is the whole design: one process at a time, because RavenDB locks the data directory. So
/// the launcher starts the database when it is idle, and stops it before Play, before the Settings "Start
/// the server" button, and before any client is launched - including joining somebody else's server, where
/// the local database is not needed at all and simply should not be held.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    private readonly EmbeddedRaven _raven = new();

    /// <summary>Whether the launcher itself is holding the database open right now.</summary>
    private bool _idleDatabaseIsUp;

    /// <summary>The attempt at opening the player database that is running, if one is.</summary>
    private Task? _idleDatabaseAttempt;

    /// <summary>
    /// Set when the launcher has decided it does not want the player database open after all.
    /// </summary>
    /// <remarks>
    /// Opening it is a RavenDB start, which is tens of seconds in which the launcher has already moved on: the
    /// user may have pressed Play, or the server may have been started from Settings. The open reads this at
    /// every step and stops rather than answering a question nobody is asking any more - and the process it
    /// had already spawned is killed rather than left holding the data directory the server is about to open.
    /// </remarks>
    private bool _idleDatabaseAbandoned;

    /// <summary>
    /// Whether the player database is in a state a copy can be taken of: nothing in this process is opening it
    /// or holding it.
    /// </summary>
    /// <remarks>
    /// This is what keeps a cloud backup away from a database that is being written: an open that is still
    /// running is a directory something is writing, where a hash taken now would be recorded as the truth about
    /// a database that was never in that state. Holding it open is not the same refusal - the sync stops it and
    /// copies it - but an open in flight has to be stopped first, which is what
    /// <see cref="ReleaseIdleDatabaseAsync"/> does.
    /// </remarks>
    private bool PlayerDatabaseIsSettled => _idleDatabaseAttempt is null or { IsCompleted: true };

    /// <summary>
    /// Whether letting go of the player database has anything to let go of.
    /// </summary>
    /// <remarks>
    /// Pure, because the case it exists for is the one that used to be missed: an open that is still running
    /// holds nothing the launcher has set a flag for, and is still a RavenDB on its way onto the very port and
    /// data directory the server is about to take. It counts.
    /// </remarks>
    public static bool SomethingHoldsThePlayerDatabase(bool opening, bool isUp, bool isRunning)
        => opening || isUp || isRunning;

    /// <summary>
    /// What the launcher's own database is doing, or why it is not doing anything. Shown on Profile, so a
    /// database that could not be started is explained rather than looking like an empty server.
    /// </summary>
    [ObservableProperty] private string _idleDatabaseStatus = string.Empty;

    public bool HasIdleDatabaseStatus => !string.IsNullOrWhiteSpace(IdleDatabaseStatus);

    partial void OnIdleDatabaseStatusChanged(string value) => OnPropertyChanged(nameof(HasIdleDatabaseStatus));

    /// <summary>Whether the launcher is the one holding the database open, rather than the server.</summary>
    public bool IsHoldingDatabaseOpen => _idleDatabaseIsUp;

    /// <summary>
    /// The database's own words in the Log, so a database that did not come up says why.
    /// </summary>
    /// <remarks>
    /// The database is started with its console drained rather than left to fill its own pipe, and its log
    /// file only exists once it gets as far as writing one - so for the ones that never come up, what they
    /// printed is the only thing that knows why: a port something else has taken, a data directory it cannot
    /// open, a runtime it was not given. All of it is about this machine and nothing else: the database is
    /// reached on <c>127.0.0.1</c>, with the system proxy switched off, so a machine with no network at all
    /// still reaches the process beside it - and if it cannot, the reason is in these lines.
    /// </remarks>
    private void ReportDatabaseOutput(string what, EmbeddedRavenSetup setup, IReadOnlyList<string> lines) {
        var kept = lines.TakeLast(12).ToList();

        if (kept.Count == 0) {
            AppendLog(LogLevel.Info,
                $"{what} printed nothing before it stopped. Its own log, if it ever wrote one, is in "
                + $"{setup.LogsDirectory}.");
            return;
        }

        AppendLog(LogLevel.Info, $"{what} said, before it stopped:");

        foreach (var line in kept) {
            AppendLog(LogLevel.Info, $"  {line}");
        }

        AppendLog(LogLevel.Info, $"Its own log, if it ever wrote one, is in {setup.LogsDirectory}.");
    }

    /// <summary>Whether the server is on its way up, up, or coming down: the database is its business then.</summary>
    private bool ServerIsBusyOrUp => IsServerBusyOrUp(ServerState);

    /// <summary>
    /// Whether a server in this state is on its way up, up, or coming down - so the database is its business and
    /// nobody else's. Pure, so the cloud sync's gate can ask it without a launcher around it.
    /// </summary>
    public static bool IsServerBusyOrUp(ServerState state)
        => state is ServerState.Starting or ServerState.WarmingUp or ServerState.Ready or ServerState.Stopping;

    /// <summary>
    /// Opens the player database, if it can be opened without the server.
    /// </summary>
    /// <remarks>
    /// Called on start-up and again whenever the server stops, so the launcher settles back into holding
    /// the database. Every failure is a status rather than an exception: a server that has never been
    /// installed, a launcher with no .NET host, or a database that will not come up all leave the launcher
    /// working - it just reads the database while the server is running, the way it used to.
    /// </remarks>
    private Task EnsureIdleDatabaseAsync() {
        // One open at a time: the caller of a second ask is answered with the first answer, which is what keeps
        // two RavenDBs from being spawned onto the same port and the same data directory.
        if (_idleDatabaseAttempt is { IsCompleted: false } attempt) {
            return attempt;
        }

        _idleDatabaseAbandoned = false;
        _idleDatabaseAttempt = OpenIdleDatabaseAsync();
        return _idleDatabaseAttempt;
    }

    /// <summary>The open itself, which is what <see cref="EnsureIdleDatabaseAsync"/> remembers.</summary>
    private async Task OpenIdleDatabaseAsync() {
        if (_config is null || ServerIsBusyOrUp || _idleDatabaseIsUp) {
            return;
        }

        // A launcher stopped on its first screen is not reading anything: there is no install to read it
        // with, and holding a database open for a launcher nobody can use is work for nothing.
        if (RequiresImlight) {
            return;
        }

        var setup = EmbeddedRavenSetup.For(_config, ServerPath, LauncherPaths.DataRoot, out var problem);
        if (setup is null) {
            IdleDatabaseStatus = $"{problem} It is read while the server is running instead.";
            return;
        }

        // Something already answers on that port: the server's database, or another launcher's. Both are
        // the same database, so there is nothing to start and nothing to take.
        if (await EmbeddedRaven.AnswersAsync(setup.Port).ConfigureAwait(true)) {
            _idleDatabaseIsUp = true;
            IdleDatabaseStatus = $"The player database is answering on port {setup.Port}.";
            await ReadDatabaseAsync().ConfigureAwait(true);
            StartProfileWatch(ServerLogPath());
            RefreshDerived();
            return;
        }

        // Nothing answers on the port, but a server this launcher did not start can still own that data
        // directory: its login port opens before its RavenDB does, which is exactly the window in which
        // starting a second database against the same folder fails with "in use by another process" and
        // looks like a broken database. Read it over HTTP while it is up instead of fighting it for the
        // files.
        if (ServerProcesses.BelongingTo(
                ImlightConfig.ExecutablePathFor(ServerPath), ServerProcesses.Running()).Count > 0) {

            IdleDatabaseStatus =
                "The server is running, so its player database is read over HTTP while it is up.";
            AppendLog(LogLevel.Info, IdleDatabaseStatus);
            RefreshDerived();
            return;
        }

        IdleDatabaseStatus = "Starting the server's own RavenDB, so the player database can be read without the server…";
        RefreshDerived();

        var failed = await _raven.StartAsync(setup).ConfigureAwait(true);
        if (failed is not null) {
            IdleDatabaseStatus = $"{failed} The database is read while the server is running instead.";
            AppendLog(LogLevel.Warning, IdleDatabaseStatus);
            ReportDatabaseOutput("The player database", setup, _raven.OutputLines);
            RefreshDerived();
            return;
        }

        // The process exists now, so a change of mind has to end it: leaving it running would have it come up
        // onto the port and the data directory the server is about to need.
        if (_idleDatabaseAbandoned) {
            await _raven.StopAsync().ConfigureAwait(true);
            return;
        }

        if (!await _raven.WaitUntilAnsweringAsync(setup).ConfigureAwait(true)) {
            var why = _raven.Problem ?? "The database did not come up.";
            await _raven.StopAsync().ConfigureAwait(true);

            // A database stopped on purpose is not a failure to report: whoever stopped it has said why.
            if (_idleDatabaseAbandoned) {
                return;
            }

            IdleDatabaseStatus = $"{why} The database is read while the server is running instead.";
            AppendLog(LogLevel.Warning, IdleDatabaseStatus);
            ReportDatabaseOutput("The player database", setup, _raven.OutputLines);
            RefreshDerived();
            return;
        }

        if (_idleDatabaseAbandoned) {
            await _raven.StopAsync().ConfigureAwait(true);
            return;
        }

        // The database itself is Imlight's to make on boot - but a server that has never been started has
        // never made it, and accounts cannot be created in a database that is not there. Making it now is
        // the same database Imlight would make, and Imlight still creates its admin account on its next
        // boot because it creates that account on every boot it takes.
        var (exists, creationProblem) = await _playerDatabase.EnsureDatabaseAsync(setup.Port).ConfigureAwait(true);
        if (!exists) {
            await _raven.StopAsync().ConfigureAwait(true);
            IdleDatabaseStatus = $"{creationProblem} Start the server once and its database appears.";
            AppendLog(LogLevel.Warning, IdleDatabaseStatus);
            RefreshDerived();
            return;
        }

        // Answered, waited for and made - and then thrown away, because something was started in the meantime
        // that needs the data directory more than a screen does.
        if (_idleDatabaseAbandoned) {
            await _raven.StopAsync().ConfigureAwait(true);
            return;
        }

        _idleDatabaseIsUp = true;
        IdleDatabaseStatus = $"The server's own RavenDB is running on port {setup.Port}, so profiles and "
                             + "accounts are read without starting the server.";
        AppendLog(LogLevel.Success, IdleDatabaseStatus);

        await ReadDatabaseAsync().ConfigureAwait(true);
        StartProfileWatch(ServerLogPath());
        RefreshDerived();

        // The launcher has the player database back and no server is running, which is one of the two moments a
        // cloud backup may take a copy: the other is its own database coming up, in EnsureLauncherDatabaseAsync.
        MaybeSyncWithCloudInBackground();
    }

    /// <summary>
    /// Lets go of the database, because something else is about to need it.
    /// </summary>
    /// <remarks>
    /// The server cannot open its data directory while this is open, and it would fail at boot with nothing
    /// but an exit code to show for it - so the handover happens before anything is started, and the port is
    /// waited on rather than assumed free. That includes a database that is still being started: waiting for it
    /// to finish would be waiting for a database the server is about to need, so it is stopped where it stands
    /// and the open is told to give up - which is what stops it coming up a moment later, onto the very port
    /// the server is taking.
    /// </remarks>
    private async Task ReleaseIdleDatabaseAsync() {
        var attempt = _idleDatabaseAttempt;
        var wasOpening = attempt is { IsCompleted: false };

        if (wasOpening) {
            _idleDatabaseAbandoned = true;
            await _raven.StopAsync().ConfigureAwait(true);

            // Let the open unwind before this returns, so it cannot decide to open the database after the
            // launcher has let go of it.
            await attempt!.ConfigureAwait(true);
        }

        // Nothing was being held: no open is in flight, the launcher is not holding it, and no process of ours
        // is running. There is nothing to hand over - the server takes a free database either way.
        if (!SomethingHoldsThePlayerDatabase(wasOpening, _idleDatabaseIsUp, _raven.IsRunning)) {
            return;
        }

        _idleDatabaseIsUp = false;
        StopProfileWatch();

        await _raven.StopAsync().ConfigureAwait(true);

        IdleDatabaseStatus = !wasOpening && _raven.Problem is { Length: > 0 } problem
            ? $"{problem} Start the server and it takes the database from here."
            : "The launcher's copy of the player database is stopped; the server has it from here.";

        AppendLog(LogLevel.Info, "Gave the player database back to the server.");
        RefreshDerived();
    }

    /// <summary>
    /// One read of everything the database holds, wherever it is being served from.
    /// </summary>
    private async Task ReadDatabaseAsync() {
        await RefreshAccountsAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        await ReadProfilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// The server's log file, for the profile watch, without needing a running server to find it.
    /// </summary>
    private string ServerLogPath()
        => _config?.ResolveLogFilePath(ServerPath) ?? Path.Combine(ServerPath, "logs", "log.txt");
}
