using System.Globalization;

namespace ViewLight.Services;

/// <summary>One item from Imlight's <c>item.manifest</c>: its template ID, school, name and source path.</summary>
/// <param name="TemplateId">The numeric template ID, which is what <c>Imlight.ini</c> is written in.</param>
/// <param name="School">The magic school the item belongs to, or <c>NoSchool</c>.</param>
/// <param name="Name">The item's name as the game knows it.</param>
/// <param name="Path">Where the template lives in the client's archive, when the manifest records it.</param>
public sealed record ItemManifestEntry(uint TemplateId, string School, string Name, string Path) {

    /// <summary>
    /// The kind of item this is, worked out from its name - which is what the game itself names it by:
    /// a wand is a <c>Wand-…</c>, a deck is a <c>Deck-…</c>, and so on.
    /// </summary>
    /// <remarks>
    /// Read from the name rather than the folder because the folders are inconsistent - the same wand
    /// lives under <c>Tier1/Wands</c> in one era and <c>Holiday/Wands</c> in another - while the name
    /// carries the game's own prefix in every case. Anything unrecognised is its own kind rather than
    /// being forced into a category it does not belong to.
    /// </remarks>
    public string Kind => KindOf(Name);

    /// <summary>The kind of an item, from its name.</summary>
    public static string KindOf(string name) {
        // Longest first, so "Amulet" is not matched as "A" and "Deck" is not shadowed by anything else.
        string[] kinds = [
            "Wand", "Deck", "Hat", "Robe", "Boots", "Shoes", "Ring", "Amulet", "Athame", "Dagger",
            "Sword", "Mount", "Pet", "Snack", "Reagent", "Seed", "Treasure", "House",
        ];

        foreach (var kind in kinds) {
            if (name.Contains(kind, StringComparison.OrdinalIgnoreCase)) {
                return kind;
            }
        }

        return "Other";
    }
}

/// <summary>One creature from Imlight's <c>creature.manifest</c>: a template ID and the name it is fought under.</summary>
public sealed record CreatureManifestEntry(uint TemplateId, string Name) {

    /// <summary>
    /// Whether the game names this creature as a boss. The manifest carries no such flag, so the name is
    /// what says it - and it does say it, consistently: <c>Wraith-WCBoss-L05</c>, <c>Scarecrow-WCBoss-L03</c>.
    /// </summary>
    public bool IsBoss => Name.Contains("boss", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One spell from Imlight's <c>spell.manifest</c>.</summary>
public sealed record SpellManifestEntry(uint TemplateId, string Name);

/// <summary>
/// Imlight's own manifests: the answer to what the client's data will not say.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists instead of reading the client.</b> Everything named here - an item's ID and name, a
/// creature's name, a spell's - is stored inside <c>Root.wad</c> as a <c>BINd</c> object graph that only
/// Imcodec's generated type cache can open (see <see cref="KiWadBlob"/> for how far the launcher gets on
/// its own). Imlight already has that cache, and already walks the archive with it: its
/// <c>ManifestGenerator</c> writes exactly these three lists out as plain text, with the numeric template
/// IDs the launcher needs. Reading the server's own output is both simpler and more trustworthy than
/// re-deriving it, and it cannot disagree with the server, because it is the server's own answer.
/// </para>
/// <para>
/// The manifests are optional: a server folder that has never run the generator simply has none, and
/// everything that reads them falls back rather than failing.
/// </para>
/// </remarks>
public sealed record ImlightManifests(
    IReadOnlyList<ItemManifestEntry> Items,
    IReadOnlyList<CreatureManifestEntry> Creatures,
    IReadOnlyList<SpellManifestEntry> Spells,
    string? Problem) {

    public static ImlightManifests Missing(string problem) => new([], [], [], problem);

    /// <summary>Whether the item list is there, which is the one everything else hangs off.</summary>
    public bool HasItems => Items.Count > 0;

    public bool HasCreatures => Creatures.Count > 0;

    public bool HasSpells => Spells.Count > 0;

    /// <summary>The creatures the game itself names as bosses.</summary>
    public IReadOnlyList<CreatureManifestEntry> Bosses => [.. Creatures.Where(c => c.IsBoss)];
}

/// <summary>Reads Imlight's manifests out of a server folder.</summary>
public static class ImlightManifestReader {

    /// <summary>Where Imlight's <c>ManifestGenerator</c> writes them, relative to the server folder.</summary>
    public const string RelativeDirectory = "manifests";

    public const string ItemFileName = "item.manifest";
    public const string CreatureFileName = "creature.manifest";
    public const string SpellFileName = "spell.manifest";

    /// <summary>
    /// A line of a manifest: fields separated by a pipe, padded with spaces on both sides.
    /// </summary>
    private static readonly char[] Separator = ['|'];

    /// <summary>Reads whichever manifests the server folder has, and says what was missing.</summary>
    public static ImlightManifests Read(string? serverPath) {
        if (string.IsNullOrWhiteSpace(serverPath) || !Directory.Exists(serverPath)) {
            return ImlightManifests.Missing("There is no server folder to read manifests from.");
        }

        var directory = Path.Combine(serverPath, RelativeDirectory);
        if (!Directory.Exists(directory)) {
            return ImlightManifests.Missing(
                $"The server has no {RelativeDirectory}{Path.DirectorySeparatorChar} folder, so its item, "
                + "creature and spell lists are not available.");
        }

        var items = ReadItems(Path.Combine(directory, ItemFileName));
        var creatures = ReadCreatures(Path.Combine(directory, CreatureFileName));
        var spells = ReadSpells(Path.Combine(directory, SpellFileName));

        if (items.Count == 0 && creatures.Count == 0 && spells.Count == 0) {
            return ImlightManifests.Missing(
                $"{RelativeDirectory}{Path.DirectorySeparatorChar} has nothing in it the launcher can read.");
        }

        return new ImlightManifests(items, creatures, spells, null);
    }

    private static List<ItemManifestEntry> ReadItems(string path) {
        var items = new List<ItemManifestEntry>();

        foreach (var fields in ReadFields(path, expected: 3)) {
            var sourcePath = fields.Length > 3 ? fields[3] : string.Empty;
            items.Add(new ItemManifestEntry(IdOf(fields[0]), fields[1], fields[2], sourcePath));
        }

        return items;
    }

    private static List<CreatureManifestEntry> ReadCreatures(string path)
        => [.. ReadFields(path, expected: 2).Select(f => new CreatureManifestEntry(IdOf(f[0]), f[1]))];

    private static List<SpellManifestEntry> ReadSpells(string path)
        => [.. ReadFields(path, expected: 2).Select(f => new SpellManifestEntry(IdOf(f[0]), f[1]))];

    /// <summary>
    /// The trimmed fields of every usable line, skipping anything malformed.
    /// </summary>
    /// <remarks>
    /// A line that does not fit is skipped rather than failing the read: these files are written by another
    /// program, and one odd line should cost one item rather than the whole list.
    /// </remarks>
    private static IEnumerable<string[]> ReadFields(string path, int expected) {
        if (!File.Exists(path)) {
            yield break;
        }

        var lines = ReadAllLinesOrNothing(path);

        foreach (var line in lines) {
            if (line.Length == 0) {
                continue;
            }

            var fields = line.Split(Separator);

            if (fields.Length < expected) {
                continue;
            }

            for (var i = 0; i < fields.Length; i++) {
                fields[i] = fields[i].Trim();
            }

            if (fields[0].Length == 0
                || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)) {
                continue;
            }

            // The last expected field is the name, which is the field every caller cares about, so a line
            // without one is not worth keeping.
            if (fields[expected - 1].Length == 0) {
                continue;
            }

            yield return fields;
        }
    }

    private static string[] ReadAllLinesOrNothing(string path) {
        try {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private static uint IdOf(string field)
        => uint.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
}
