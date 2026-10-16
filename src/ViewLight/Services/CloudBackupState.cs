using System.Text.Json;
using System.Text.Json.Nodes;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What the launcher remembers about the last sync: the manifest both sides were equal at.</summary>
/// <remarks>
/// This is the ancestor <see cref="BackupSync"/> weighs a changed local copy against a changed remote one
/// with. Without it, every launcher start would look like a first sync and the only safe decision would be to
/// ask - so it is the difference between a sync that is quiet and one that is a dialogue.
/// </remarks>
public sealed class CloudBackupSyncState {

    /// <summary>The manifest both sides were last seen equal at, or <c>null</c> when there has never been one.</summary>
    public string? AncestorJson { get; set; }

    /// <summary>When that happened, for saying so.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>The ancestor as a manifest, or <c>null</c> when there is not one this build can read.</summary>
    public BackupManifest? Ancestor => BackupManifest.TryParse(AncestorJson);
}

/// <summary>
/// Where the two things a cloud backup needs to remember are kept - and why they are kept in different places.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refresh token is a document in the launcher's own database.</b> It is a credential that has to work
/// while nobody is watching, so it has to outlive a run; the launcher's database is where its own long-lived
/// state goes, and it is already opened for the whole run. It is written by signing in and read by every sync.
/// </para>
/// <para>
/// <b>The sync ancestor is a file beside the settings, deliberately not in the database.</b> The manifest
/// records the content hash of that very database, so writing the ancestor into it would change the thing it
/// describes: every sync would move the launcher's own directory, the next sync would see a change that was
/// only its own bookkeeping, and the two sides would take turns uploading for ever. The ancestor is this
/// machine's relationship with the cloud rather than data about it, which is the same category the settings
/// are in - and it is the one thing a restore must not carry, because a machine that has just restored has
/// nothing to compare against until it has hashed what it restored.
/// </para>
/// </remarks>
public static class CloudBackupState {

    /// <summary>The document the signed-in account's refresh token lives in.</summary>
    public const string TokenDocumentId = "LauncherData/cloud-backup";

    /// <summary>Its collection, so it is not mistaken for a profile.</summary>
    public const string Collection = "LauncherData";

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    /// <summary>Where the last-synced manifest is kept, beside the settings rather than in the database.</summary>
    public static string SyncStatePath(string dataRoot) => Path.Combine(dataRoot, "backup-sync.json");

    /// <summary>The refresh token of the signed-in Google account, or <c>null</c> when nobody is signed in.</summary>
    public static async Task<string?> ReadRefreshTokenAsync(
        LauncherDatabaseClient client, CancellationToken token = default) {

        var document = await client.ReadDocumentAsync(TokenDocumentId, token).ConfigureAwait(false);

        if (document is null) {
            return null;
        }

        return document.Value.TryGetProperty("RefreshToken", out var refresh)
               && refresh.ValueKind == JsonValueKind.String
            ? refresh.GetString()
            : null;
    }

    /// <summary>
    /// The refresh token the launcher is signed in with, put back if the database has lost the one it had.
    /// </summary>
    /// <param name="client">The launcher's own database.</param>
    /// <param name="knownThisRun">
    /// The sign-in this run already has, or <c>null</c> when it has none. It is what a database that has just
    /// come back empty is healed from.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>A restore writes the whole database directory, and the sign-in is a document in it.</b> The copy in
    /// the cloud can be one taken before this machine signed in - the first backup of a fresh install, or the
    /// backup of a machine that never signed in at all - and putting it back would sign this machine out. That
    /// is not what restoring data means, so the sign-in this run still holds is written back over the copy that
    /// came with the restore.
    /// </para>
    /// <para>
    /// Deliberately only the sign-in this run knows about: a launcher that starts with no sign-in anywhere must
    /// stay signed out, or the sign-out button would not hold and a token would come back from nowhere.
    /// </para>
    /// </remarks>
    public static async Task<string?> ReadRefreshTokenOrRestoreAsync(
        LauncherDatabaseClient client, string? knownThisRun, CancellationToken token = default) {

        var stored = await ReadRefreshTokenAsync(client, token).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(knownThisRun)) {
            return stored;
        }

        await WriteRefreshTokenAsync(client, knownThisRun, account: null, token).ConfigureAwait(false);

        return knownThisRun;
    }

    /// <summary>
    /// Records the refresh token of the account that just signed in.
    /// </summary>
    /// <remarks>
    /// Nothing else about the sign-in is kept: the access token is short-lived and fetched when it is needed,
    /// so storing one would be storing something already out of date by the next run.
    /// </remarks>
    public static Task<bool> WriteRefreshTokenAsync(
        LauncherDatabaseClient client, string? refreshToken, string? account,
        CancellationToken token = default) {

        var document = new JsonObject {
            ["RefreshToken"] = refreshToken,
            ["Account"] = account,
            ["SignedInAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["@metadata"] = new JsonObject { ["@collection"] = Collection },
        };

        return client.WriteDocumentAsync(TokenDocumentId, document, token);
    }

    /// <summary>Forgets the sign-in, so the next sync does not try to use it.</summary>
    public static Task<bool> ClearRefreshTokenAsync(
        LauncherDatabaseClient client, CancellationToken token = default)
        => client.DeleteDocumentAsync(TokenDocumentId, token);

    /// <summary>The last-synced manifest, or <c>null</c> when there is not one to read.</summary>
    public static CloudBackupSyncState ReadSyncState(string dataRoot) {
        try {
            var path = SyncStatePath(dataRoot);

            if (!File.Exists(path)) {
                return new CloudBackupSyncState();
            }

            return JsonSerializer.Deserialize<CloudBackupSyncState>(File.ReadAllText(path), s_options)
                   ?? new CloudBackupSyncState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // A state that will not read is treated as never having synced, which asks rather than guesses -
            // the safe direction when the alternative is overwriting one side or the other.
            return new CloudBackupSyncState();
        }
    }

    /// <summary>Records what both sides were just seen equal at.</summary>
    public static bool WriteSyncState(string dataRoot, BackupManifest ancestor) {
        try {
            var directory = Path.GetDirectoryName(SyncStatePath(dataRoot));
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            var state = new CloudBackupSyncState {
                AncestorJson = ancestor.ToJson(),
                LastSyncedAt = DateTimeOffset.UtcNow,
            };

            File.WriteAllText(SyncStatePath(dataRoot), JsonSerializer.Serialize(state, s_options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return false;
        }
    }
}
