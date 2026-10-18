using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>What a person answered when they were asked whether to go through with a full reset.</summary>
/// <param name="IncludeCloudBackup">
/// Whether the copy of the same two databases in their cloud drive goes as well. The one choice the reset
/// has, and the one the screen is built around: without it the cloud copy is left alone, and the next sync
/// brings it back.
/// </param>
public sealed record WipeRequest(bool IncludeCloudBackup);

/// <summary>
/// Starting over completely: both databases, and - if that is what was asked for - the copy of them in the
/// user's own cloud drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> A launcher that has been backed up has no way back to a clean slate. The
/// copy in the drive is older than anything a wiped database holds, and a sync weighs the two without an
/// ancestor to judge against - which the rules answer by taking the cloud's copy, because that is the safe
/// answer for a machine that has just been pointed at somebody's Drive. So the old data comes back, every
/// time, and the only way out used to be to go and delete the files in a Drive folder the user cannot see.
/// See <see cref="LauncherReset"/> for what that leaves and what it does not.
/// </para>
/// <para>
/// <b>The order is the whole design, and it is not the obvious one.</b> The cloud copy is deleted
/// <i>before</i> the launcher's database comes back, not after. A database that comes back empty while the
/// cloud still holds the old data is a machine that has never synced, and the rules for that take the
/// cloud's copy - so recreating first and deleting second would restore the very data that was just wiped,
/// in the seconds between the two. Deleting first leaves a fresh database with nothing in the cloud, which
/// the ordinary sync path uploads by itself: local side present, remote side absent, so the fresh state is
/// the one that goes up. That is the sequence the issue asked for - wipe remote, wipe local, recreate
/// local, back up to cloud - with the cloud deletion moved to the front where it cannot lose the race.
/// </para>
/// <para>
/// <b>The sign-in survives, and it has to.</b> The Google sign-in is a document in the very database that
/// is deleted, which would leave the launcher unable to delete the cloud copy and unable to put one back -
/// signed out, with the data still up there. So the refresh token is read out before anything is deleted
/// and kept in this run's field, which is exactly what the database coming back empty is healed from: the
/// document is written into the new database again, and the user stays signed in.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>Set while a reset is running, so nothing else is edited underneath it.</summary>
    [ObservableProperty] private bool _isResetting;

    /// <summary>
    /// Set by the window, so the two-step confirmation can be shown without the view model knowing about
    /// views - the same shape <see cref="PickImageAsync"/> and <see cref="BrowseImageAsync"/> have.
    /// </summary>
    /// <remarks>
    /// The arguments are what to read out on the warning: the things about to be deleted, and whether there
    /// is a cloud copy this launcher could delete as well. The answer is what was chosen, or <c>null</c>
    /// when the dialog was closed rather than answered - which means no.
    /// </remarks>
    public Func<IReadOnlyList<string>, bool, Task<WipeRequest?>>? ConfirmResetAsync { get; set; }

    /// <summary>
    /// The things a full reset would delete, named for the screen.
    /// </summary>
    /// <remarks>
    /// Read off the launcher's own configuration rather than written down twice: the server's database is
    /// wherever the server's config says it is, and a launcher with no server here has no second database to
    /// list.
    /// </remarks>
    public IReadOnlyList<string> ResetTargetNames => [.. ResetTargets().Select(target => target.Name)];

    /// <summary>What is not deleted, said out loud because that is half of what the screen is for.</summary>
    public string ResetKeepsText =>
        "Everything else stays: the Imlight install, the patch server and the revisions it has fetched, your "
        + "Wizard101 install, and ViewLight's own settings - your folders, your theme and your choices. Those "
        + "are downloaded or picked again cheaply; the two databases are the only things here that cannot be.";

    /// <summary>Whether a reset can be started at all, and what is in the way when it cannot.</summary>
    public bool CanResetNow => LauncherReset.WhyNot(ServerIsBusyOrUp, IsCloudSyncRunning) is null;

    public string ResetBlockedText => LauncherReset.WhyNot(ServerIsBusyOrUp, IsCloudSyncRunning) ?? string.Empty;

    public bool IsResetBlocked => !CanResetNow;

    /// <summary>Whether a cloud copy could be deleted from here at all, which needs somebody signed in.</summary>
    public bool CanWipeCloudCopy => IsCloudBackupSignedIn;

    public bool HasResetNotice => IsResetting;

    public string ResetNotice => "Starting over: the databases are being deleted and made again.";

    public string ResetNoticeDetail =>
        "Nothing can be edited until it has finished. The server is not running, and it is left to you to "
        + "start it again so that it makes itself a new database.";

    partial void OnIsResettingChanged(bool value) {
        OnPropertyChanged(nameof(IsEditingAllowed));
        OnPropertyChanged(nameof(HasResetNotice));
    }

    /// <summary>
    /// Asks, then does it.
    /// </summary>
    /// <remarks>
    /// Everything below happens in one go and is said in the Log line by line, because a reset that half
    /// worked is a thing the user has to be able to read afterwards - the reason a database would not delete
    /// is the line that tells them what is still holding it.
    /// </remarks>
    [RelayCommand]
    private async Task StartResetAsync() {
        if (IsResetting || ConfirmResetAsync is null) {
            return;
        }

        if (LauncherReset.WhyNot(ServerIsBusyOrUp, IsCloudSyncRunning) is { Length: > 0 } blocked) {
            AppendLog(LogLevel.Warning, blocked);
            RefreshDerived();
            return;
        }

        var targets = ResetTargets();
        var answered = await ConfirmResetAsync([.. targets.Select(target => target.Name)], CanWipeCloudCopy)
            .ConfigureAwait(true);

        if (answered is not { } request) {
            AppendLog(LogLevel.Info, "The reset was called off, so nothing was deleted.");
            return;
        }

        IsResetting = true;
        AppendLog(LogLevel.Warning, "Starting over: deleting this machine's databases.");

        try {
            // Read out before anything is deleted, because it lives in the database that is about to go - and
            // without it the cloud copy could neither be deleted nor put back. See the remarks on this class.
            var refresh = await CloudRefreshTokenAsync().ConfigureAwait(true);

            if (refresh is { Length: > 0 }) {
                // Kept on this run's field, which is what the database coming back empty is healed from.
                _cloudRefreshToken = refresh;
            }

            // Both databases have to be let go before their folders can be removed: RavenDB holds its data
            // directory under an exclusive lock, and a folder something is still holding is a folder that
            // cannot be deleted.
            CloseLauncherDatabaseClient();
            await _launcherDatabase.StopAsync().ConfigureAwait(true);
            await ReleaseIdleDatabaseAsync().ConfigureAwait(true);

            var report = await Task.Run(() => LauncherReset.Delete(targets, LauncherPaths.DataRoot))
                .ConfigureAwait(true);

            ReportReset(report);

            if (!report.IsComplete) {
                // Half a reset is worth stopping for: the databases that survived are about to be read again,
                // and the user is told what is still there rather than being left thinking it is gone.
                AppendLog(LogLevel.Error,
                    "The reset did not finish. What is left has been read again rather than deleted, and the "
                    + "next thing to do is stop whatever is holding the files - the game, the server, or "
                    + "another ViewLight - and try again.");
            }

            if (request.IncludeCloudBackup) {
                await DeleteCloudCopyAsync(refresh).ConfigureAwait(true);
            }
            else if (refresh is { Length: > 0 }) {
                AppendLog(LogLevel.Info,
                    $"The copy of these databases in {CloudBackupProviderName} was left exactly as it is, "
                    + "because that was not asked for. A launcher with no data of its own and a backup up "
                    + "there takes the backup, so the next sync will put it back.");
            }

            // The launcher makes itself a new database, which is the point of the whole thing - and the
            // reason the cloud copy had to go first: with nothing up there, the ordinary sync path uploads
            // this new, empty state rather than restoring over it.
            await EnsureLauncherDatabaseAsync().ConfigureAwait(true);

            AppendLog(LogLevel.Success,
                "ViewLight has made itself a new, empty database. Start your server when you are ready and it "
                + "will make itself a new one, with the admin account from Config/Imlight.ini - there will be "
                + "no accounts, no wizards and no profiles until then.");
        }
        catch (Exception ex) {
            AppendLog(LogLevel.Error, $"The reset did not finish ({ex.Message}).");
        }
        finally {
            IsResetting = false;
            RefreshDerived();
        }
    }

    /// <summary>Every line a wipe is worth, so the Log says what happened to each thing.</summary>
    private void ReportReset(ResetReport report) {
        foreach (var directory in report.Deleted) {
            AppendLog(LogLevel.Info, $"Deleted {directory}.");
        }

        foreach (var absent in report.Missing) {
            AppendLog(LogLevel.Info, $"{absent} was not there, so there was nothing to delete.");
        }

        foreach (var problem in report.Problems) {
            AppendLog(LogLevel.Warning, problem);
        }
    }

    /// <summary>
    /// Deletes the backup in the user's cloud drive, and says what that leaves.
    /// </summary>
    /// <remarks>
    /// Nothing here needs a database, which is the point of doing it when it is done: the folder being
    /// emptied belongs to the user's Drive, and the token that may write into it was read out before the
    /// launcher's own database was deleted. A failure is reported rather than thrown, because the local wipe
    /// has already happened and the one thing worth saying then is what the next sync will do.
    /// </remarks>
    private async Task DeleteCloudCopyAsync(string? refresh) {
        if (refresh is not { Length: > 0 }) {
            AppendLog(LogLevel.Warning,
                "There is no Google sign-in to delete the cloud copy of these databases with, so it has been "
                + "left where it is. Sign in under Settings and back up when you are ready to replace it.");
            return;
        }

        var refusal = string.Empty;

        var store = new GoogleDriveBackupStore(async _ => {
            var answer = await GoogleOAuth.RefreshAsync(CloudCredentials(refresh));
            refusal = answer.Problem ?? string.Empty;

            return (answer.Tokens, answer.Problem);
        });

        var archive = await store.DeleteAsync(CloudBackupFiles.Archive).ConfigureAwait(true);
        var manifest = await store.DeleteAsync(CloudBackupFiles.Manifest).ConfigureAwait(true);

        if (archive && manifest) {
            AppendLog(LogLevel.Success,
                $"The backup in {CloudBackupProviderName} has been deleted: the archive and the manifest "
                + "that described it are both gone, so there is nothing left up there to restore.");
            return;
        }

        var why = refusal.Length > 0 ? refusal : store.Problem;

        AppendLog(LogLevel.Warning,
            $"The backup in {CloudBackupProviderName} could not be fully deleted ({why}). It is still there, "
            + "and the next sync will bring it back down onto this new, empty database.");
    }

    /// <summary>
    /// The refresh token the launcher is signed in with: out of the database when it is still up, and out of
    /// this run's own copy when it is not.
    /// </summary>
    private async Task<string?> CloudRefreshTokenAsync() {
        if (_launcherDatabaseClient is { } client) {
            var stored = await CloudBackupState.ReadRefreshTokenAsync(client).ConfigureAwait(true);

            if (stored is { Length: > 0 }) {
                return stored;
            }
        }

        return _cloudRefreshToken;
    }

    /// <summary>The two databases a reset deletes, as the launcher's own configuration resolves them.</summary>
    private IReadOnlyList<ResetTarget> ResetTargets() {
        string? serverDatabase = null;

        if (_config is not null && !string.IsNullOrWhiteSpace(ServerPath)) {
            try {
                serverDatabase = _config.ResolveEmbeddedDatabaseDirectory(ServerPath);
            }
            catch (Exception) {
                // A config that will not resolve is a server database with nowhere to be deleted from, so it
                // is simply not part of the list - the launcher's own database still is.
            }
        }

        return LauncherReset.Targets(LauncherPaths.DataRoot, serverDatabase);
    }
}
