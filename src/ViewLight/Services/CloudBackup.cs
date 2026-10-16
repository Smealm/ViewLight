using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What a plan found: the decision, this machine's manifest, and the cloud's.</summary>
/// <param name="Plan">What the sync decided to do.</param>
/// <param name="Local">This machine's data, hashed just now.</param>
/// <param name="Remote">The cloud's manifest, or <c>null</c> when there is no backup up there.</param>
/// <param name="LocalProblem">
/// Why this machine's data could not be read, when it could not. Carried apart from the plan so a forced
/// upload can refuse too: the manifest built from a database that could not be read says the database is
/// gone, and putting that up would be worse than doing nothing.
/// </param>
public sealed record BackupPlanResult(
    BackupSyncPlan Plan, BackupManifest Local, BackupManifest? Remote, string? LocalProblem = null);

/// <summary>
/// One machine's backup: hashes what is here, reads what is there, and moves whichever side the plan says.
/// </summary>
/// <remarks>
/// <para>
/// The orchestration only. What decides anything is <see cref="BackupSync"/>, which is pure; what moves the
/// bytes is <see cref="BackupArchive"/> and the <see cref="ICloudBackupStore"/>; what is hashed is
/// <see cref="DirectoryFingerprint"/>. This class is the order those are put in, and it is where the one rule
/// that is not about data lives: the archive is uploaded before the manifest, and a restore is verified before
/// it is applied.
/// </para>
/// <para>
/// <b>Nothing here runs against a live database.</b> The directories are only read to be hashed, and the store
/// is only written to. Applying a restore is <see cref="BackupArchive.RestoreAsync"/>, which the caller runs
/// while the launcher is idle - the same window RavenDB's exclusive lock already makes the launcher manage.
/// </para>
/// </remarks>
public sealed class CloudBackup {

    private readonly ICloudBackupStore _store;
    private readonly IReadOnlyDictionary<string, string> _directories;
    private readonly string _workDirectory;

    /// <param name="store">Where the backup is kept.</param>
    /// <param name="directories">The database directories, keyed by <see cref="BackupDatabases"/>.</param>
    /// <param name="workDirectory">
    /// A scratch folder for the archive between the database directories and the store. Its own folder, so a
    /// backup in flight is never mistaken for data.
    /// </param>
    public CloudBackup(
        ICloudBackupStore store, IReadOnlyDictionary<string, string> directories, string workDirectory) {
        _store = store;
        _directories = directories;
        _workDirectory = workDirectory;
    }

    /// <summary>Why the last call did not do what it was asked to, or <c>null</c> when it did.</summary>
    public string? Problem { get; private set; }

    /// <summary>Where the archive is assembled on this machine before it goes up, or comes down before it is applied.</summary>
    public string ArchivePath => Path.Combine(_workDirectory, CloudBackupFiles.Archive);

    /// <summary>
    /// Hashes the database directories into a manifest, ready to be uploaded or compared.
    /// </summary>
    /// <remarks>
    /// A database with no directory is recorded as absent rather than left out, so a backup of a machine whose
    /// server has never run still says so plainly - and a restore of it does not invent an empty database.
    /// </remarks>
    public BackupManifest BuildLocalManifest() {
        Problem = null;

        var manifest = new BackupManifest { TakenAt = DateTimeOffset.UtcNow };

        foreach (var database in BackupDatabases.All) {
            _directories.TryGetValue(database, out var directory);
            var result = DirectoryFingerprint.Of(directory);

            manifest.Databases[database] = result.State;

            if (result.Problem is { Length: > 0 } problem) {
                Problem = problem;
            }
        }

        return manifest;
    }

    /// <summary>
    /// The cloud's manifest, or <c>null</c> when there is not one - or it could not be read.
    /// </summary>
    /// <remarks>
    /// The difference between "no backup up there" and "a backup that will not read" is kept in
    /// <see cref="Problem"/>: the first is the ordinary first-sync case and leaves it <c>null</c>, the second
    /// sets it - so <see cref="PlanAsync"/> never mistakes a manifest it could not read for one that is not
    /// there and plans an upload over it.
    /// </remarks>
    public async Task<BackupManifest?> ReadRemoteManifestAsync(CancellationToken token = default) {
        Problem = null;

        var bytes = await _store.DownloadAsync(CloudBackupFiles.Manifest, token).ConfigureAwait(false);

        // The store sets Problem when the fetch itself failed; a file that is simply not there leaves it null.
        if (bytes is null) {
            Problem = _store.Problem;
            return null;
        }

        var manifest = BackupManifest.TryParse(System.Text.Encoding.UTF8.GetString(bytes));

        if (manifest is null) {
            Problem = "The cloud's manifest is not one this launcher can read, so nothing was changed.";
        }

        return manifest;
    }

    /// <summary>Hashes what is here, reads what is there, and decides.</summary>
    /// <param name="ancestor">What the two sides were last equal at, or <c>null</c> when they never have been.</param>
    public async Task<BackupPlanResult> PlanAsync(BackupManifest? ancestor, CancellationToken token = default) {
        var local = BuildLocalManifest();

        // A database that is there but could not be read - a lock held by something else, most likely - is
        // recorded as absent, and absent is a change. Uploading that would tell the cloud a database has gone,
        // and the next restore would act on it. So the local read is checked before anything is decided.
        if (Problem is { Length: > 0 } localProblem) {
            return new BackupPlanResult(
                new BackupSyncPlan(BackupSyncAction.Conflict,
                    $"This machine's data could not be read, so nothing was changed: {localProblem}"),
                local, null, localProblem);
        }

        var remote = await ReadRemoteManifestAsync(token).ConfigureAwait(false);

        // A remote read that failed, or a manifest that would not read, is not "there is no backup up there":
        // treating it as one would plan an upload over a backup this machine simply could not see. Either is
        // turned into a conflict, which stops and asks rather than overwriting anything.
        if (remote is null && Problem is { Length: > 0 } readProblem) {
            return new BackupPlanResult(
                new BackupSyncPlan(BackupSyncAction.Conflict,
                    $"The cloud's backup could not be read, so nothing was changed: {readProblem}"),
                local, null);
        }

        // Nothing to read, and the read itself did not fail: either the cloud has no backup, or it has one whose
        // manifest is not beside it. Those are opposite answers and a download tells them apart only by pulling
        // the whole archive, so the store's index is asked instead.
        if (remote is null && Problem is null) {
            var archiveIsThere = await _store.ExistsAsync(CloudBackupFiles.Archive, token).ConfigureAwait(false);

            if (archiveIsThere) {
                return new BackupPlanResult(
                    new BackupSyncPlan(BackupSyncAction.Download,
                        "The cloud holds a backup with no manifest beside it - an upload that stopped between the "
                        + "two, or a manifest somebody removed - so it is brought down and its own manifest read "
                        + "from it. Nothing here is written over it."),
                    local, null);
            }

            // A search that failed is not "there is no archive". Reading it as one would plan an upload over a
            // backup this machine simply could not see, which is the one outcome worth stopping for. The store's
            // reason is lifted onto this object's own field, the same way a failed manifest read is, so a caller
            // reading it finds the same sentence the plan was built from.
            if (_store.Problem is { Length: > 0 } searchProblem) {
                Problem = searchProblem;

                return new BackupPlanResult(
                    new BackupSyncPlan(BackupSyncAction.Conflict,
                        $"The cloud could not be searched, so nothing was changed: {searchProblem}"),
                    local, null);
            }
        }

        return new BackupPlanResult(BackupSync.Plan(local, remote, ancestor), local, remote);
    }

    /// <summary>
    /// Writes the archive and puts it up, manifest last.
    /// </summary>
    /// <param name="local">The manifest to upload, which the caller has usually just built.</param>
    /// <returns><c>null</c> when it went up, or why it did not.</returns>
    public async Task<string?> UploadAsync(BackupManifest local, CancellationToken token = default) {
        Problem = null;

        try {
            Directory.CreateDirectory(_workDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Problem = $"A place to assemble the backup could not be made ({ex.Message}).";
        }

        var archiveProblem = await BackupArchive
            .CreateAsync(ArchivePath, local, _directories, token)
            .ConfigureAwait(false);

        if (archiveProblem is not null) {
            return Problem = archiveProblem;
        }

        byte[] archive;
        try {
            archive = await File.ReadAllBytesAsync(ArchivePath, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Problem = $"The backup could not be read back ({ex.Message}).";
        }

        // The archive first: a manifest that is up while its archive is not would tell the next sync that a
        // backup exists which cannot be restored.
        if (!await _store.UploadAsync(CloudBackupFiles.Archive, archive, token).ConfigureAwait(false)) {
            return Problem = _store.Problem ?? "The backup could not be uploaded.";
        }

        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(local.ToJson());
        if (!await _store.UploadAsync(CloudBackupFiles.Manifest, manifestBytes, token).ConfigureAwait(false)) {
            return Problem = _store.Problem ?? "The backup went up but its manifest did not.";
        }

        return null;
    }

    /// <summary>
    /// Brings the archive down, checks it, and applies it over the database directories.
    /// </summary>
    /// <returns>The restored manifest, so the caller can record it as the new ancestor - or <c>null</c> and a reason.</returns>
    /// <remarks>
    /// The archive is read back as a backup before a single file is written: a transfer that was truncated, or
    /// a manifest that names no databases, ends here with the directories exactly as they were.
    /// </remarks>
    public async Task<(BackupManifest? Manifest, string? Problem)> DownloadAndRestoreAsync(
        CancellationToken token = default) {

        Problem = null;

        var bytes = await _store.DownloadAsync(CloudBackupFiles.Archive, token).ConfigureAwait(false);

        if (bytes is null) {
            return (null, Problem = _store.Problem ?? "There is no backup in the cloud to restore.");
        }

        try {
            Directory.CreateDirectory(_workDirectory);
            await File.WriteAllBytesAsync(ArchivePath, bytes, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return (null, Problem = $"The backup could not be written to this machine ({ex.Message}).");
        }

        var contents = BackupArchive.Read(ArchivePath);
        if (contents is null) {
            return (null, Problem = "The cloud's backup could not be read as a backup, so nothing on this "
                                    + "machine was changed.");
        }

        var restoreProblem = await BackupArchive
            .RestoreAsync(ArchivePath, _directories, token)
            .ConfigureAwait(false);

        if (restoreProblem is not null) {
            return (null, Problem = restoreProblem);
        }

        return (contents.Manifest, null);
    }
}
