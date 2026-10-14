using System.Text;
using System.Text.RegularExpressions;

namespace ViewLight.Services;

/// <summary>
/// What the client's <c>TemplateManifest.xml</c> turned out to hold.
/// </summary>
/// <param name="Found">Whether the manifest was in the archive at all.</param>
/// <param name="Decoded">Whether the deflate layer behind its header was unwrapped.</param>
/// <param name="TemplatePaths">How many distinct data paths the manifest names.</param>
/// <param name="BossNames">Distinct names of templates whose path says they are a boss.</param>
/// <param name="CreatureNames">Distinct names of templates that are a creature rather than a place or thing.</param>
/// <param name="Problem">Why nothing could be read, when nothing could be.</param>
public sealed record TemplateManifest(
    bool Found,
    bool Decoded,
    int TemplatePaths,
    IReadOnlyList<string> BossNames,
    IReadOnlyList<string> CreatureNames,
    string? Problem) {

    public static TemplateManifest Missing(string problem) => new(false, false, 0, [], [], problem);

    public int BossCount => BossNames.Count;

    public int CreatureCount => CreatureNames.Count;
}

/// <summary>
/// Reads the one file in <c>Root.wad</c> that can be made sense of: the template manifest.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the index of every template the client has - the wand, deck, robe, creature and boss
/// definitions the game is built from - and behind its <c>BINd</c> header it is a deflate stream, which
/// <see cref="KiWadBlob"/> unwraps. What comes out is the game's own path list: <i>which</i> templates
/// exist, spelled out.
/// </para>
/// <para>
/// <b>What this does not do, and why it is said plainly.</b> Each manifest entry also carries the numeric
/// template ID that Imlight's config is written in terms of - <c>87256</c> for <c>Wand-T1-016</c> - and that
/// number cannot be recovered here. The records around each path are a schema-driven object graph whose
/// field layout changes with the serialiser revision, and the ID is not stored next to its name; reading it
/// needs the same generated type cache that reading the templates themselves does. A guess at those numbers
/// would be worse than no answer, because a wrong ID is written straight into the server's starter-item list
/// and fails silently in the game.
/// </para>
/// <para>
/// So this reports what is certain - that the manifest is there, that it decoded, how many templates it
/// names, and which of them are bosses and creatures - and nothing that is not.
/// </para>
/// </remarks>
public static class RootWadTemplateManifest {

    /// <summary>The file Imlight itself reads template definitions from inside <c>Root.wad</c>.</summary>
    public const string FileName = "TemplateManifest.xml";

    /// <summary>The most boss or creature names kept, so a huge archive cannot fill a screen with them.</summary>
    private const int MaxNames = 2000;

    /// <summary>
    /// A path as the manifest writes it: an archive-relative name ending in a known asset extension. The
    /// paths are plain ASCII inside the decoded payload, which is what lets them be read without knowing
    /// the object graph around them.
    /// </summary>
    private static readonly Regex PathPattern = new(
        @"[A-Za-z0-9_\-/\. ]+\.(?:xml|nif|kfm|dds)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reads the manifest out of a game folder's archive, if there is one to read.</summary>
    public static TemplateManifest Read(string? gameFolder) {
        var path = RootWadItemService.FindRootWad(gameFolder);
        if (path is null) {
            return TemplateManifest.Missing(
                "No Root.wad was found in the selected game folder, so there is no template manifest to read.");
        }

        try {
            using var archive = KiWadArchive.Open(path);
            return Read(archive);
        }
        catch (Exception ex) {
            return TemplateManifest.Missing($"Root.wad could not be read: {ex.Message}");
        }
    }

    /// <summary>The same read against an archive that has already been opened.</summary>
    public static TemplateManifest Read(KiWadArchive archive) {
        var entry = archive.FindEntry(FileName);
        if (entry is null) {
            return TemplateManifest.Missing($"{FileName} is not in the archive.");
        }

        byte[] raw;
        try {
            raw = archive.ReadEntry(entry);
        }
        catch (Exception ex) {
            return TemplateManifest.Missing($"{FileName} could not be read out of the archive: {ex.Message}");
        }

        var decoded = KiWadBlob.IsDeflated(raw);
        var payload = KiWadBlob.Decode(raw);

        // Latin-1 rather than UTF-8 on purpose: the payload is a byte stream with ASCII paths in it, and
        // every byte has to map to exactly one character, so nothing shifts and a path is never corrupted
        // by a byte that happens not to be valid text.
        var text = Encoding.Latin1.GetString(payload);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bosses = new List<string>();
        var creatures = new List<string>();

        foreach (Match match in PathPattern.Matches(text)) {
            var value = match.Value.Trim().TrimStart('|');

            // A path that came out of the payload with a stray length byte glued to its front is dropped
            // rather than trimmed into shape: a name the game did not write is not worth having.
            if (value.Length == 0 || !paths.Add(value)) {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(value);
            if (name.Length == 0) {
                continue;
            }

            // Two trees are named after creatures without being them, and both are excluded: a spell
            // template like "Mass Infection - Unbeatable - MOB ONLY" is named for the mob that casts it,
            // and a cinematic is named for the creature it shows. Between them they would bury the real
            // creatures under a thousand cards and animations. Matched by containing rather than starting
            // with, because a path can pick up a length byte in front of it when the payload is read for
            // what it looks like - the byte before a path is often a printable one.
            if (value.Contains("Spells/", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Cinematics/", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (name.Contains("boss", StringComparison.OrdinalIgnoreCase)) {
                if (bosses.Count < MaxNames) {
                    bosses.Add(name);
                }
            }
            else if (name.Contains("creature", StringComparison.OrdinalIgnoreCase)) {
                if (creatures.Count < MaxNames) {
                    creatures.Add(name);
                }
            }

        }

        return new TemplateManifest(
            Found: true,
            Decoded: decoded,
            TemplatePaths: paths.Count,
            BossNames: [.. bosses.Order(StringComparer.OrdinalIgnoreCase)],
            CreatureNames: [.. creatures.Order(StringComparer.OrdinalIgnoreCase)],
            Problem: null);
    }
}
