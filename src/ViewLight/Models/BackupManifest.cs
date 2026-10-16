using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViewLight.Models;

/// <summary>
/// The names of the databases a backup carries.
/// </summary>
/// <remarks>
/// <para>
/// A backup is a set of database directories plus a manifest, and nothing else. The two sets here are the
/// only things that cannot be downloaded again: the launcher's own notes (the profiles, the playtime, the
/// badges - the directory task 4 moved them into), and the server's own RavenDB, which is where the
/// accounts, the wizards and everything they own live.
/// </para>
/// <para>
/// The game's files, the client, the server binaries, the cached art and downloads and <c>Imlight.ini</c>
/// are all deliberately left out: the first four can be fetched again, and the last is machine-specific
/// paths that would be wrong on the machine doing the restoring.
/// </para>
/// </remarks>
public static class BackupDatabases {

    /// <summary>The launcher's own database directory - <see cref="Services.LauncherDatabase.DataDirectoryFor"/>.</summary>
    public const string Launcher = "launcher";

    /// <summary>The server's own RavenDB data directory, where accounts and wizards live.</summary>
    public const string Server = "server";

    /// <summary>Both, in the order a backup lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Launcher, Server];

    /// <summary>What to call a set on screen.</summary>
    public static string Describe(string database) => database switch {
        Launcher => "the launcher's own data",
        Server => "the server's database",
        _ => database,
    };
}

/// <summary>
/// What one database looked like when a backup was taken: its content hash, and how big it was.
/// </summary>
/// <param name="Hash">
/// The directory's content hash (see <see cref="Services.DirectoryFingerprint"/>). This is the thing the
/// sync compares - not a timestamp, which two machines disagree about and a clock change can move.
/// </param>
/// <param name="Bytes">How many bytes of files the directory held, for saying what a transfer will cost.</param>
/// <param name="Files">How many files it held, for the same reason.</param>
/// <param name="Present">Whether the directory existed at all when the manifest was taken.</param>
public sealed record BackupDatabaseState(
    string Hash,
    long Bytes = 0,
    int Files = 0,
    bool Present = true) {

    /// <summary>A database that was not there - a server that has never run, or a portable folder with no data yet.</summary>
    public static BackupDatabaseState Absent { get; } = new(string.Empty, 0, 0, Present: false);
}

/// <summary>
/// The manifest a backup carries: for every database, its content hash and the time it was taken.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes the sync automatic.</b> Every backup carries, per database, a content hash and the
/// time it was taken. That is enough to answer the ordinary question without asking anybody: the remote
/// copy newer than the last thing this machine uploaded, with the local copy unchanged, means the local
/// copy is behind and is restored; a local copy that has changed means the change is uploaded. Only a real
/// conflict - both sides changed since the common ancestor they were last seen equal at - stops and asks.
/// </para>
/// <para>
/// <b>The hashes are the truth and the times are only ever said.</b> The decision compares content, never
/// clocks, because two machines disagree about the time and a clock change or a time zone move would
/// otherwise be enough to make one side look newer than it is. <see cref="TakenAt"/> is carried because a
/// person asked when their last backup was, and that is the only thing it is for.
/// </para>
/// <para>
/// It is small and it is written on its own, apart from the archive, so the question "is there anything to
/// do?" costs one small download rather than the tens of gigabytes a backup can weigh.
/// </para>
/// </remarks>
public sealed class BackupManifest {

    /// <summary>The manifest format this build writes. A later version can refuse or upgrade an older one.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The manifest's own file name, both in the archive and in the cloud.</summary>
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The format version, so a manifest written by a newer launcher is recognisable rather than misread.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// When the backup was taken, for saying so. Never used to decide anything.
    /// </summary>
    public DateTimeOffset TakenAt { get; set; }

    /// <summary>
    /// The databases the backup holds, keyed by <see cref="BackupDatabases"/>. A database that was not there
    /// when the backup was taken is still listed, marked absent - so "the server has never run" is a fact the
    /// manifest records rather than a key it omits.
    /// </summary>
    public Dictionary<string, BackupDatabaseState> Databases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The state recorded for a database, or <c>null</c> when the manifest does not mention it.</summary>
    public BackupDatabaseState? For(string database)
        => Databases.TryGetValue(database, out var state) ? state : null;

    /// <summary>How many bytes the whole backup holds, for saying what it weighs.</summary>
    public long TotalBytes => Databases.Values.Where(s => s.Present).Sum(s => s.Bytes);

    /// <summary>
    /// Whether two manifests hold the same content - the same databases with the same hashes.
    /// </summary>
    /// <remarks>
    /// Deliberately blind to <see cref="Version"/>, <see cref="TakenAt"/> and the sizes: two backups of the
    /// same data taken twice have the same content, and a sync that treated them as different would upload
    /// the same bytes again for nothing. A manifest with no databases is equal to another with none, which is
    /// the honest answer for two empty sets.
    /// </remarks>
    public bool HasSameContent(BackupManifest? other) {
        if (other is null) {
            return false;
        }

        foreach (var database in BackupDatabases.All) {
            var mine = For(database);
            var theirs = other.For(database);

            var mineHash = mine?.Present == true ? mine.Hash : null;
            var theirsHash = theirs?.Present == true ? theirs.Hash : null;

            if (!string.Equals(mineHash, theirsHash, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The manifest as the JSON that is written into the cloud and into the archive.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    /// <summary>
    /// The manifest a document holds, or <c>null</c> when it is not one this build can read.
    /// </summary>
    /// <remarks>
    /// A manifest that will not read is treated as no manifest rather than guessed at - the same shape every
    /// other read of strange data in this project has. No manifest means "nothing known about the remote",
    /// which the sync answers by uploading rather than by restoring over a local copy it cannot compare to.
    /// </remarks>
    public static BackupManifest? TryParse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize<BackupManifest>(json, s_options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) {
            return null;
        }
    }
}
