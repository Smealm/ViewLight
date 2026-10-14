using System.Text.Json.Nodes;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What came of the one-time import of an existing <c>profiles.json</c>.</summary>
/// <param name="State">Whether it ran, was not needed, or could not be done.</param>
/// <param name="Profiles">How many profiles were moved, when any were.</param>
/// <param name="Pictures">How many pictures were moved with them.</param>
/// <param name="Message">What to say about it, when there is something to say.</param>
public sealed record ProfilesImportResult(
    ProfilesImportState State, int Profiles = 0, int Pictures = 0, string? Message = null);

/// <summary>Whether the launcher's old notes were moved into its database.</summary>
public enum ProfilesImportState {

    /// <summary>Nothing to do: there is no file, or the notes have already been moved once.</summary>
    NotNeeded,

    /// <summary>The notes were moved.</summary>
    Imported,

    /// <summary>The file is there and will not be read, so nothing was touched.</summary>
    Unreadable,
}

/// <summary>
/// Moves an existing launcher's <c>profiles.json</c> into the launcher's own database, once.
/// </summary>
/// <remarks>
/// <para>
/// A launcher that has been used has its profiles, playtime and badges in exactly one file, and the database
/// it now keeps them in starts empty - so without this, moving to the database would look like losing
/// everything the launcher had ever learned. It runs once, when the database is first attached.
/// </para>
/// <para>
/// <b>Once, and recorded in the database rather than in a file.</b> The marker is a document, so restoring a
/// backup cannot bring an old file back over newer notes: the restored database already says it was imported,
/// and the file is left exactly where it is. That is the whole reason the marker is not a flag in the
/// settings file - the settings are the one thing a backup does not carry.
/// </para>
/// <para>
/// <b>The file is renamed rather than deleted</b> - <c>profiles.json.imported</c> - because the file being
/// tidied away is the point, and a rename is tidying that the previous build can still read if somebody goes
/// back to it. A file that will not parse changes nothing at all and says so: half a file acted on is worse
/// than a file left alone.
/// </para>
/// </remarks>
public static class ProfilesImport {

    /// <summary>
    /// The document that says the file has been read, so that it is never read again.
    /// </summary>
    /// <remarks>
    /// In the launcher's database, beside the notes it is about: a marker in a file would be in the one place
    /// a restore does not put back.
    /// </remarks>
    public const string DocumentId = "LauncherData/imported-profiles";

    /// <summary>Its collection, so it is not mistaken for a profile.</summary>
    public const string Collection = "LauncherData";

    /// <summary>The file, as the launcher's old notes are kept.</summary>
    public static string FileName => "profiles.json";

    /// <summary>What it is renamed to once it has been read.</summary>
    public static string ImportedFileName => "profiles.json.imported";

    /// <summary>
    /// Runs the import if it has not been run before, which is every launcher's first start on the database.
    /// </summary>
    /// <param name="client">The launcher's own database, already made.</param>
    /// <param name="dataRoot">Where the launcher's own files are, and so where the file would be.</param>
    /// <param name="token">Cancellation for the writes.</param>
    public static async Task<ProfilesImportResult> RunAsync(
        LauncherDatabaseClient client, string dataRoot, CancellationToken token = default) {

        var path = Path.Combine(dataRoot, FileName);

        try {
            // Already done - or done before a backup was taken, which is the case this is for.
            if (await client.ReadDocumentAsync(DocumentId, token).ConfigureAwait(false) is not null) {
                return new ProfilesImportResult(ProfilesImportState.NotNeeded);
            }

            if (!File.Exists(path)) {
                // Nothing to move, and the marker is still written: the file is a one-time migration source,
                // so one that turns up later - out of an old backup - must not be poured over newer notes.
                await MarkAsync(client, 0, token).ConfigureAwait(false);
                return new ProfilesImportResult(ProfilesImportState.NotNeeded);
            }

            var file = new JsonProfilePersistence(path);
            var notes = file.Load(out var problem);

            if (notes is null || problem is not null) {
                // Left exactly as it is: an import that cannot be trusted is not performed at all, and it is
                // said rather than passed over - the notes are still there and still readable by the build
                // that wrote them.
                return new ProfilesImportResult(ProfilesImportState.Unreadable, Message: problem);
            }

            foreach (var profile in notes.Profiles) {
                if (!await client.WriteDocumentAsync(
                        LauncherProfileDocument.DocumentIdFor(profile.AccountId),
                        LauncherProfileDocument.Build(profile),
                        token).ConfigureAwait(false)) {
                    return new ProfilesImportResult(ProfilesImportState.Unreadable, Message: client.Problem);
                }
            }

            await client.WriteDocumentAsync(
                ZoneWorldsDocument.DocumentId, ZoneWorldsDocument.Build(notes.ZoneWorlds), token)
                .ConfigureAwait(false);

            var pictures = await MovePicturesAsync(client, dataRoot, notes, token).ConfigureAwait(false);

            await MarkAsync(client, notes.Profiles.Count, token).ConfigureAwait(false);

            // Renamed only once everything is in the database: a file that has been renamed is one the
            // launcher will never read again, so it must not be renamed before its notes are safe.
            File.Move(path, Path.Combine(dataRoot, ImportedFileName), overwrite: true);

            return new ProfilesImportResult(ProfilesImportState.Imported, notes.Profiles.Count, pictures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return new ProfilesImportResult(ProfilesImportState.Unreadable, Message: ex.Message);
        }
    }

    /// <summary>
    /// Moves the pictures the profiles name into their documents, so a profile that had one still has it.
    /// </summary>
    /// <remarks>
    /// The files are left where they are, for the same reason the notes file is renamed rather than deleted:
    /// the build that wrote them can still read them. What matters is that the profile in the database now
    /// shows the picture, and that a backup of the database carries it.
    /// </remarks>
    private static async Task<int> MovePicturesAsync(
        LauncherDatabaseClient client, string dataRoot, LauncherProfiles notes, CancellationToken token) {

        var moved = 0;

        foreach (var profile in notes.Profiles) {
            foreach (var name in new[] { profile.AvatarFile, profile.BannerFile }) {
                if (string.IsNullOrWhiteSpace(name)) {
                    continue;
                }

                var path = Path.Combine(LauncherPaths.ProfileDirectory(profile.AccountId), name);

                if (!File.Exists(path)) {
                    continue;
                }

                var content = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);

                if (await client
                        .PutAttachmentAsync(LauncherProfileDocument.DocumentIdFor(profile.AccountId), name, content, token)
                        .ConfigureAwait(false)) {
                    moved++;
                }
            }
        }

        return moved;
    }

    /// <summary>Writes the marker that says the file has been read.</summary>
    private static Task<bool> MarkAsync(LauncherDatabaseClient client, int profiles, CancellationToken token)
        => client.WriteDocumentAsync(DocumentId, new JsonObject {
            ["ImportedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["Profiles"] = profiles,
            ["From"] = FileName,
            ["@metadata"] = new JsonObject { ["@collection"] = Collection },
        }, token);
}
