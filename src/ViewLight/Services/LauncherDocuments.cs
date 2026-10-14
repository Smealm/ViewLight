using System.Text.Json;
using System.Text.Json.Nodes;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// One profile as a document in the launcher's own database.
/// </summary>
/// <remarks>
/// <para>
/// The document <i>is</i> the profile: the same fields, written by the same serializer that wrote
/// <c>profiles.json</c> before it, so a profile is one document with a name rather than one record inside a
/// file that has to be read whole. Named by account id, because that is the key everywhere else - it is what
/// Play signs in with, and what the server's own records are keyed by - so a profile can also be fetched
/// without reading every profile, which is the point of the move.
/// </para>
/// <para>
/// The collection is the launcher's own: nothing in the player database is called this, and nothing here is
/// ever read by Imlight. A profile is what a Wizard101 account is to <i>this machine</i>.
/// </para>
/// </remarks>
public static class LauncherProfileDocument {

    /// <summary>The collection every profile document is written to.</summary>
    public const string Collection = "Profiles";

    /// <summary>The document a profile lives in, keyed by the account it belongs to.</summary>
    public static string DocumentIdFor(ulong accountId)
        => $"{Collection}/{accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The document, ready to be written.</summary>
    public static JsonObject Build(LauncherProfile profile) {
        // Built from the profile itself rather than field by field. Every field is already written and read
        // by this serializer - profiles.json was nothing else - so a document cannot drift from the model it
        // mirrors, and a field added there is in the database the moment it is written.
        var document = JsonSerializer.SerializeToNode(profile) as JsonObject ?? new JsonObject();

        document["@metadata"] = new JsonObject { ["@collection"] = Collection };
        return document;
    }

    /// <summary>
    /// The profile a document holds, or <c>null</c> when it is not one this launcher can read.
    /// </summary>
    /// <remarks>
    /// A document that will not read is not repaired and not guessed at: it is skipped, the way a profile
    /// file that will not parse was replaced by an empty one rather than acted on in halves. What is read
    /// still loads, and what could not be is reported by the caller that asked.
    /// </remarks>
    public static LauncherProfile? Inspect(JsonElement document) {
        try {
            return document.Deserialize<LauncherProfile>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) {
            return null;
        }
    }
}

/// <summary>
/// Which world each zone belongs to, as one document in the launcher's own database.
/// </summary>
/// <remarks>
/// <para>
/// The launcher learns this by watching the server load a zone, and it is the launcher's own note about a
/// server's layout rather than anything the server keeps - so it lives with the profiles, in the same
/// database, and moves with a portable folder for the same reason.
/// </para>
/// <para>
/// One document rather than one per zone: it is always read whole (a lookup by zone is a lookup in a map, not
/// a query) and written whole, and the number of zones is the game's own - a few hundred at most, learned
/// only as they are visited. It is the one part of the old file that was genuinely a map rather than a
/// collection of records.
/// </para>
/// </remarks>
public static class ZoneWorldsDocument {

    /// <summary>The collection it is written to, and the property the map is nested under.</summary>
    public const string Collection = "ZoneWorlds";

    /// <summary>Its document id. One document, so there is nothing to key it by but itself.</summary>
    public const string DocumentId = "ZoneWorlds/all";

    /// <summary>The document, ready to be written.</summary>
    public static JsonObject Build(IReadOnlyDictionary<string, string> zoneWorlds) {
        var worlds = new JsonObject();

        // In order, so two runs that learned the same zones write the same bytes: a diff of two backups
        // should show what was learned, not what order the log happened to arrive in.
        foreach (var (zone, world) in zoneWorlds.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            worlds[zone] = world;
        }

        return new JsonObject {
            ["ZoneWorlds"] = worlds,
            ["@metadata"] = new JsonObject { ["@collection"] = Collection },
        };
    }

    /// <summary>
    /// The map a document holds, compared without regard to case - the same rule the file it replaces used.
    /// </summary>
    /// <remarks>
    /// A document that will not read leaves an empty map rather than an exception: what the launcher has
    /// learned is relearned from the server, and a launcher that refuses to read its own note would refuse
    /// to start over something it can simply learn again.
    /// </remarks>
    public static Dictionary<string, string> Inspect(JsonElement document) {
        var worlds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!document.TryGetProperty("ZoneWorlds", out var map) || map.ValueKind != JsonValueKind.Object) {
            return worlds;
        }

        foreach (var entry in map.EnumerateObject()) {
            if (entry.Value.ValueKind == JsonValueKind.String) {
                worlds[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }

        return worlds;
    }
}
