namespace ViewLight.Services;

/// <summary>The two names a backup uses in the cloud, and what they are for.</summary>
/// <remarks>
/// <para>
/// Two files rather than one. The archive is the backup - tens of gigabytes of database - and the manifest is
/// a few hundred bytes saying what the archive holds. Splitting them is what lets the ordinary question, "is
/// there anything to do?", be answered by fetching the small file and nothing else: a sync that had to
/// download the archive to compare it would spend a whole backup to find out it did not need one.
/// </para>
/// <para>
/// The archive is uploaded first and the manifest second, so a reader that arrives in the middle of an upload
/// finds an old manifest with a new archive rather than a new manifest with a half-written archive.
/// </para>
/// </remarks>
public static class CloudBackupFiles {

    /// <summary>The manifest: what the archive holds, per database, hashed and dated.</summary>
    public const string Manifest = "manifest.json";

    /// <summary>The backup itself: one zip holding the database directories and the manifest.</summary>
    public const string Archive = "backup.zip";
}

/// <summary>
/// A place a backup can be kept: the two files above, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow, and deliberately not a file system. A store can list nothing, read nothing and write
/// nothing outside the two names a backup uses, which is the whole safety property of the app-scoped cloud
/// folder: a bug here cannot reach a user's other files, whatever it is told.
/// </para>
/// <para>
/// Everything answers rather than throws. A signed-out store, a refused upload and a download that never
/// arrived are all <c>null</c> or <c>false</c> with the reason in <see cref="Problem"/>, the same shape
/// <see cref="LauncherDatabaseClient"/> has, so a caller never has to catch to keep the launcher working.
/// </para>
/// </remarks>
public interface ICloudBackupStore {

    /// <summary>Why the last call did not do what it was asked to, or <c>null</c> when it did.</summary>
    string? Problem { get; }

    /// <summary>Whether the user has signed in and their sign-in still works.</summary>
    Task<bool> IsSignedInAsync(CancellationToken token = default);

    /// <summary>The named file's bytes, or <c>null</c> when it is not there or could not be fetched.</summary>
    Task<byte[]?> DownloadAsync(string name, CancellationToken token = default);

    /// <summary>
    /// Whether the named file is there, without fetching it.
    /// </summary>
    /// <remarks>
    /// Asked about the archive and only about the archive, and for the one question a missing manifest cannot
    /// answer: "no backup up there" and "a backup whose manifest is gone" look identical to a download, and
    /// they are opposite answers. Tens of gigabytes is what downloading the archive to find out would cost, so
    /// this asks the store's own index instead.
    /// </remarks>
    Task<bool> ExistsAsync(string name, CancellationToken token = default);

    /// <summary>Writes the named file, replacing whatever was there.</summary>
    Task<bool> UploadAsync(string name, byte[] content, CancellationToken token = default);

    /// <summary>Removes the named file. One that is not there is the outcome asked for.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken token = default);
}
