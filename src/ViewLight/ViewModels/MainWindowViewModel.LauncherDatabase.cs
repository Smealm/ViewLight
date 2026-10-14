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
    /// Starts the launcher's own database, if it is not already running and can be.
    /// </summary>
    /// <remarks>
    /// The status is written before the wait, because a database takes tens of seconds to come up and a
    /// screen that says nothing for that long looks like one that is not trying. Nothing here blocks: the
    /// caller fires this and carries on, which is why it is a task rather than a property.
    /// </remarks>
    private async Task EnsureLauncherDatabaseAsync() {
        if (_launcherDatabase.IsRunning) {
            return;
        }

        // A launcher stopped on the requirement screen is not keeping notes about anything yet, and has no
        // install to run a database from.
        if (RequiresImlight || _config is null) {
            return;
        }

        var setup = LauncherDatabase.For(_config, ServerPath, LauncherPaths.DataRoot, out var unusable);
        if (setup is null) {
            LauncherDatabaseStatus = $"{unusable} The launcher's own notes cannot be kept until it is fixed.";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            return;
        }

        LauncherDatabaseStatus = "Starting the launcher's own database…";
        RefreshDerived();

        var failed = await _launcherDatabase.StartAsync(setup).ConfigureAwait(true);
        if (failed is not null) {
            LauncherDatabaseStatus = $"{failed} The launcher's own notes cannot be kept until it is fixed.";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            RefreshDerived();
            return;
        }

        if (!await _launcherDatabase.WaitUntilAnsweringAsync(setup).ConfigureAwait(true)) {
            var why = _launcherDatabase.Problem ?? "The database did not come up.";
            await _launcherDatabase.StopAsync().ConfigureAwait(true);

            LauncherDatabaseStatus = $"{why} The launcher's own notes cannot be kept until it is fixed.";
            AppendLog(LogLevel.Warning, LauncherDatabaseStatus);
            RefreshDerived();
            return;
        }

        LauncherDatabaseStatus = $"The launcher's own database is running on port {setup.Port}, in "
                                 + $"{setup.DataDirectory}.";
        AppendLog(LogLevel.Success, LauncherDatabaseStatus);

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
    }

    /// <summary>Lets go of the client the launcher's notes were read and written through.</summary>
    private void CloseLauncherDatabaseClient() {
        _launcherDatabaseClient?.Dispose();
        _launcherDatabaseClient = null;
    }
}
