using System.Text.Json;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Where the launcher's own notes are kept: the profiles, their playtime and badges, and which world each
/// zone turned out to belong to.
/// </summary>
/// <remarks>
/// <para>
/// Narrow on purpose. The launcher keeps its notes in a database of its own; the suite keeps them in a file,
/// because a real RavenDB per test is not something to ask of a test run - and the code that reads and writes
/// the notes should not have to care which of the two it is talking to. So the two live behind this, and
/// <see cref="ProfileStore"/> is the one surface above it that everything else uses.
/// </para>
/// <para>
/// Reads and writes of the notes themselves are synchronous and the pictures are not. That is not symmetry for
/// its own sake: a save happens where the note was learned - inside the pass that read the server's log - and
/// making every one of those asynchronous would ripple through the whole profile system to buy nothing on a
/// local database. A picture is picked by a person pressing a button, which is already asynchronous.
/// </para>
/// </remarks>
public interface IProfilePersistence {

    /// <summary>
    /// Everything that is kept, or <c>null</c> when it could not be read.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty set: a launcher that cannot read its own notes must leave the ones it already
    /// holds alone. Empty is indistinguishable from a first run, and would be written back over everything the
    /// database holds the moment anything changed. The problem is what to say about it - and also carries a
    /// note about anything that was skipped rather than failing outright.
    /// </remarks>
    LauncherProfiles? Load(out string? problem);

    /// <summary>Writes everything. The problem is what to say when it did not work.</summary>
    bool Save(LauncherProfiles data, out string? problem);

    /// <summary>A profile's picture, and why there is none when that is the answer.</summary>
    Task<ProfilePicture> ReadImageAsync(ulong accountId, string fileName, CancellationToken token = default);

    /// <summary>Keeps a profile's picture under that name, replacing one that is already there.</summary>
    Task<bool> WriteImageAsync(ulong accountId, string fileName, byte[] content, CancellationToken token = default);

    /// <summary>Forgets a profile's picture. A picture that is not there is the outcome asked for.</summary>
    Task DeleteImageAsync(ulong accountId, string fileName, CancellationToken token = default);
}

/// <summary>
/// What a read of a profile's picture found.
/// </summary>
/// <param name="Bytes">The picture, or <c>null</c>.</param>
/// <param name="Problem">
/// Why it could not be read, or <c>null</c> when the answer is simply that there is none.
/// </param>
/// <remarks>
/// The two are not the same answer, which is the whole reason this is a pair rather than bytes alone. A
/// profile with no picture is the ordinary case and the screen draws its initial-letter circle; a database
/// that would not hand one back is a moment, and the screen tries again - see
/// <c>MainWindowViewModel.LoadProfileImages</c>. Returning <c>null</c> for both is what used to leave a
/// picture blank until the launcher was restarted.
/// </remarks>
public readonly record struct ProfilePicture(byte[]? Bytes, string? Problem) {

    /// <summary>A profile with no picture, which is not a failure.</summary>
    public static ProfilePicture None => new(null, null);

    /// <summary>Whether there is simply no picture, rather than one that could not be read.</summary>
    public bool IsMissing => Bytes is null && Problem is null;
}

/// <summary>
/// The notes in a file, which is what <c>profiles.json</c> was.
/// </summary>
/// <remarks>
/// Still here because it is what the suite runs against, and because a launcher that has never had a database
/// has notes in exactly this shape - which is what makes importing them a copy rather than a translation
/// (task 4a). The launcher itself no longer writes it: a launcher writing its notes to a file and to a
/// database would have two answers to every question, and no way to know which was the newer.
/// </remarks>
public sealed class JsonProfilePersistence : IProfilePersistence {

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true,
    };

    public string FilePath { get; }

    public JsonProfilePersistence(string? filePath = null)
        => FilePath = filePath ?? LauncherPaths.ProfilesFile;

    /// <summary>
    /// Where a picture is kept, beside the file: one folder per account under the launcher's own root.
    /// </summary>
    /// <remarks>
    /// Only the file name is stored, and it is resolved against the profile's own folder, which is what keeps
    /// a portable launcher portable - nothing wrote down where the folder was.
    /// </remarks>
    public static string? ImagePath(ulong accountId, string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(LauncherPaths.ProfileDirectory(accountId), fileName);

    public LauncherProfiles? Load(out string? problem) {
        problem = null;

        try {
            if (!File.Exists(FilePath)) {
                return new LauncherProfiles();
            }

            var json = File.ReadAllText(FilePath);

            // Written from the launcher, read back by it: a file that will not parse is one this launcher did
            // not write, or one that was cut short. Either way an empty set that collects from here is better
            // than refusing to start - and it is said out loud rather than looking like a first run.
            var loaded = JsonSerializer.Deserialize<LauncherProfiles>(json, s_options);

            if (loaded is null) {
                problem = $"{Path.GetFileName(FilePath)} held nothing that could be read, so it starts empty.";
                return new LauncherProfiles();
            }

            return loaded;
        }
        catch (JsonException) {
            problem = $"{Path.GetFileName(FilePath)} could not be read, so the profiles start empty.";
            return new LauncherProfiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            problem = $"{Path.GetFileName(FilePath)} could not be read ({ex.Message}).";
            return null;
        }
    }

    public bool Save(LauncherProfiles data, out string? problem) {
        problem = null;

        try {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, s_options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            problem = $"{Path.GetFileName(FilePath)} could not be written ({ex.Message}).";
            return false;
        }
    }

    /// <remarks>
    /// A file that is not there is a profile with no picture and nothing more: there is no second copy of
    /// this one to try, so there is nothing a later look would find that this one did not.
    /// </remarks>
    public async Task<ProfilePicture> ReadImageAsync(
        ulong accountId, string fileName, CancellationToken token = default) {

        var path = ImagePath(accountId, fileName);

        try {
            return File.Exists(path)
                ? new ProfilePicture(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), null)
                : ProfilePicture.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return ProfilePicture.None;
        }
    }

    public async Task<bool> WriteImageAsync(
        ulong accountId, string fileName, byte[] content, CancellationToken token = default) {

        try {
            var directory = LauncherPaths.ProfileDirectory(accountId);
            Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), content, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return false;
        }
    }

    /// <summary>
    /// Nothing: a file is left where it is, because forgetting a picture is forgetting which one this profile
    /// shows rather than deleting bytes that may still be wanted. The database deletes its attachment, because
    /// there a picture exists for exactly one profile and a backup should not carry one nothing shows.
    /// </summary>
    public Task DeleteImageAsync(ulong accountId, string fileName, CancellationToken token = default)
        => Task.CompletedTask;
}

/// <summary>
/// The notes in the launcher's own database: one document per profile, one for the zone-to-world map, and the
/// pictures as attachments on the profile they belong to.
/// </summary>
/// <remarks>
/// <para>
/// This is the launcher's implementation, and the reason task 4 exists: a file has to be read whole, changed
/// in memory and written whole, and it only ever grows - while a profile here is one document that can be
/// fetched by the account it belongs to, and a badge is a write to that document rather than a rewrite of
/// every profile the account has ever had.
/// </para>
/// <para>
/// It talks to <see cref="LauncherDatabaseClient"/>, whose calls are asynchronous, through the two synchronous
/// reads and writes above. That is deliberate and safe: the client never resumes on a captured context, so
/// waiting for it cannot deadlock the launcher's UI thread, and what it is waiting for is a local database
/// answering in milliseconds.
/// </para>
/// </remarks>
public sealed class DatabaseProfilePersistence : IProfilePersistence {

    private readonly LauncherDatabaseClient _client;

    /// <summary>
    /// What was last written for each profile, so a profile that has not changed is not written again.
    /// </summary>
    /// <remarks>
    /// Every note the launcher learns is saved by writing the whole set, which is what makes one profile's
    /// playtime tick cost a write of every profile there is - a document per account, one after the other,
    /// while the launcher is holding the UI. Keeping what was written turns that back into the one document
    /// that actually changed. Cleared whenever the notes are read again, because a read is what says what the
    /// database holds now - which is not necessarily what this launcher put there.
    /// </remarks>
    private readonly Dictionary<ulong, string> _writtenProfiles = [];

    /// <summary>The same, for the zone-to-world map.</summary>
    private string? _writtenZoneWorlds;

    public DatabaseProfilePersistence(LauncherDatabaseClient client) => _client = client;

    /// <summary>Makes sure the database the notes are kept in exists.</summary>
    public bool Prepare() => _client.EnsureDatabaseAsync().GetAwaiter().GetResult();

    public LauncherProfiles? Load(out string? problem) {
        problem = null;

        var documents = _client.ReadDocumentsAsync().GetAwaiter().GetResult();

        if (documents is null) {
            problem = _client.Problem;
            return null;
        }

        // What the database holds now is what this read says, so what was remembered as written is no longer
        // evidence about it: a restore from the cloud replaces documents this launcher never wrote.
        _writtenProfiles.Clear();
        _writtenZoneWorlds = null;

        var loaded = new LauncherProfiles();
        var skipped = 0;

        foreach (var document in documents) {
            if (!document.TryGetProperty("@metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("@collection", out var collection)
                || collection.ValueKind != JsonValueKind.String) {
                continue;
            }

            switch (collection.GetString()) {
                case LauncherProfileDocument.Collection:
                    var profile = LauncherProfileDocument.Inspect(document);

                    if (profile is null) {
                        skipped++;
                    }
                    else {
                        loaded.Profiles.Add(profile);
                    }

                    break;

                case ZoneWorldsDocument.Collection:
                    loaded.ZoneWorlds = ZoneWorldsDocument.Inspect(document);
                    break;
            }
        }

        // Ordered by when each profile was first seen, which is the order they were created in - the order the
        // file kept them in, and the order the profile screen shows them in. The document listing itself comes
        // back keyed by account id, which is an arbitrary order to show a person's accounts in.
        loaded.Profiles.Sort((one, other) => Nullable.Compare(one.FirstPlayedAt, other.FirstPlayedAt));

        if (skipped > 0) {
            problem = skipped == 1
                ? "One profile in the launcher's own database could not be read and was skipped."
                : $"{skipped} profiles in the launcher's own database could not be read and were skipped.";
        }

        return loaded;
    }

    public bool Save(LauncherProfiles data, out string? problem) {
        problem = null;

        foreach (var profile in data.Profiles) {
            var document = LauncherProfileDocument.Build(profile);
            var json = document.ToJsonString();

            // Nothing about this profile has changed since it was last written, so writing it again would be
            // the same document arriving at the same place - which the poll would do once per account per
            // look, while the server is running and one profile's playtime is ticking.
            if (_writtenProfiles.TryGetValue(profile.AccountId, out var written) && written == json) {
                continue;
            }

            if (!_client.WriteDocumentAsync(
                    LauncherProfileDocument.DocumentIdFor(profile.AccountId), document)
                    .GetAwaiter().GetResult()) {
                problem = _client.Problem;
                return false;
            }

            _writtenProfiles[profile.AccountId] = json;
        }

        // The map goes with them: it is the launcher's own note about the same server, learned the same way.
        var worlds = ZoneWorldsDocument.Build(data.ZoneWorlds);
        var worldsJson = worlds.ToJsonString();

        if (worldsJson != _writtenZoneWorlds) {
            if (!_client.WriteDocumentAsync(ZoneWorldsDocument.DocumentId, worlds).GetAwaiter().GetResult()) {
                problem = _client.Problem;
                return false;
            }

            _writtenZoneWorlds = worldsJson;
        }

        return true;
    }

    /// <remarks>
    /// A database that would not answer is told apart from a profile with no picture: the first is worth
    /// looking again at, and the second is the ordinary case.
    /// </remarks>
    public async Task<ProfilePicture> ReadImageAsync(
        ulong accountId, string fileName, CancellationToken token = default) {

        var bytes = await _client
            .ReadAttachmentAsync(LauncherProfileDocument.DocumentIdFor(accountId), fileName, token)
            .ConfigureAwait(false);

        return new ProfilePicture(bytes, bytes is null ? _client.Problem : null);
    }

    public Task<bool> WriteImageAsync(
        ulong accountId, string fileName, byte[] content, CancellationToken token = default)
        => _client.PutAttachmentAsync(LauncherProfileDocument.DocumentIdFor(accountId), fileName, content, token);

    public Task DeleteImageAsync(ulong accountId, string fileName, CancellationToken token = default)
        => _client.DeleteAttachmentAsync(LauncherProfileDocument.DocumentIdFor(accountId), fileName, token);
}
