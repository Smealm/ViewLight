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

    /// <summary>
    /// What the launcher's own database is doing, or why it is not doing anything. Shown on Profile, so a
    /// database that could not be started is explained rather than looking like an empty server.
    /// </summary>
    [ObservableProperty] private string _idleDatabaseStatus = string.Empty;

    public bool HasIdleDatabaseStatus => !string.IsNullOrWhiteSpace(IdleDatabaseStatus);

    partial void OnIdleDatabaseStatusChanged(string value) => OnPropertyChanged(nameof(HasIdleDatabaseStatus));

    /// <summary>Whether the launcher is the one holding the database open, rather than the server.</summary>
    public bool IsHoldingDatabaseOpen => _idleDatabaseIsUp;

    /// <summary>Whether the server is on its way up, up, or coming down: the database is its business then.</summary>
    private bool ServerIsBusyOrUp
        => ServerState is ServerState.Starting or ServerState.WarmingUp or ServerState.Ready or ServerState.Stopping;

    /// <summary>
    /// Opens the player database, if it can be opened without the server.
    /// </summary>
    /// <remarks>
    /// Called on start-up and again whenever the server stops, so the launcher settles back into holding
    /// the database. Every failure is a status rather than an exception: a server that has never been
    /// installed, a launcher with no .NET host, or a database that will not come up all leave the launcher
    /// working - it just reads the database while the server is running, the way it used to.
    /// </remarks>
    private async Task EnsureIdleDatabaseAsync() {
        if (_config is null || ServerIsBusyOrUp || _idleDatabaseIsUp) {
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

        IdleDatabaseStatus = "Starting the server's own RavenDB, so the player database can be read without the server…";
        RefreshDerived();

        var failed = await _raven.StartAsync(setup).ConfigureAwait(true);
        if (failed is not null) {
            IdleDatabaseStatus = $"{failed} The database is read while the server is running instead.";
            AppendLog(LogLevel.Warning, IdleDatabaseStatus);
            RefreshDerived();
            return;
        }

        if (!await _raven.WaitUntilAnsweringAsync(setup).ConfigureAwait(true)) {
            var why = _raven.Problem ?? "The database did not come up.";
            await _raven.StopAsync().ConfigureAwait(true);

            IdleDatabaseStatus = $"{why} The database is read while the server is running instead.";
            AppendLog(LogLevel.Warning, IdleDatabaseStatus);
            RefreshDerived();
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

        _idleDatabaseIsUp = true;
        IdleDatabaseStatus = $"The server's own RavenDB is running on port {setup.Port}, so profiles and "
                             + "accounts are read without starting the server.";
        AppendLog(LogLevel.Success, IdleDatabaseStatus);

        await ReadDatabaseAsync().ConfigureAwait(true);
        StartProfileWatch(ServerLogPath());
        RefreshDerived();
    }

    /// <summary>
    /// Lets go of the database, because something else is about to need it.
    /// </summary>
    /// <remarks>
    /// The server cannot open its data directory while this is open, and it would fail at boot with nothing
    /// but an exit code to show for it - so the handover happens before anything is started, and the port is
    /// waited on rather than assumed free.
    /// </remarks>
    private async Task ReleaseIdleDatabaseAsync() {
        if (!_idleDatabaseIsUp) {
            return;
        }

        _idleDatabaseIsUp = false;
        StopProfileWatch();

        await _raven.StopAsync().ConfigureAwait(true);

        IdleDatabaseStatus = _raven.Problem is { Length: > 0 } problem
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
