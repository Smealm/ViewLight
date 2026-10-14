using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What the launcher found when it opened the client's <c>Root.wad</c>.</summary>
public sealed record RootWadProbe(
    bool Found,
    string? Path,
    uint Version,
    int FileCount,
    long SizeBytes,
    bool HasTemplateManifest,
    string? Error) {

    public string SizeDisplay => FileCount == 0 ? "—" : ServerInstallService.FormatBytes(SizeBytes);

    public static RootWadProbe Missing(string? error = null)
        => new(false, null, 0, 0, 0, false, error);
}

/// <summary>The item list offered by the starter-kit picker.</summary>
public sealed record ItemCatalog(IReadOnlyList<ItemTemplate> Items, bool IsStubbed, string Notice);

/// <summary>
/// Reads item information out of the game client's <c>Root.wad</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status: archive reading is real, item decoding is stubbed.</b> The archive *index* is parsed for
/// real by <see cref="KiWadArchive"/> - we verify the KIWAD header, report the archive revision and file
/// count, and confirm the template manifest is present. What is not implemented is turning the client's
/// template blobs into named items.
/// </para>
/// <para>
/// <b>Why item decoding is hard.</b> Item templates are not a simple list. <c>Root.wad</c> holds
/// <c>TemplateManifest.xml</c> plus per-template blobs encoded in KingsIsle's <c>ObjectProperty</c>
/// format, which is a hash-driven binary object graph: field names are replaced by 32-bit string
/// hashes and each behaviour block is type-tagged. Decoding it needs Imcodec's
/// <c>BindSerializer</c> plus the source-generated <c>PropertyClass</c> types for the exact client
/// revision - a compile-time code generation step, not something that can be recovered from raw bytes.
/// </para>
/// <para>
/// <b>What is read out of the archive already.</b> The blob container itself - the <c>BINd</c> header and
/// the deflate layer some entries carry behind it - is understood by <see cref="KiWadBlob"/>, and that is
/// enough to read the template manifest's own list of paths: see <see cref="RootWadTemplateManifest"/>,
/// which reports every template the client has and which of them are bosses. What that does <i>not</i>
/// give is the numeric template ID each name is stored under - the number this picker is built on and
/// <c>Imlight.ini</c>'s <c>DefaultItems</c> is written in terms of - because it lives inside the object
/// graph rather than beside the name.
/// </para>
/// <para>
/// <b>How this gets real, and the two ways it does not.</b> The item list the picker needs - template ID,
/// name and school per item - is Imlight's own <c>manifests/item.manifest</c>, written by Imlight's
/// <c>ManifestGenerator</c> through the same Imcodec type cache it runs the game with. That file is read
/// here, and a server folder that has one gets the game's real list.
/// </para>
/// <para>
/// Referencing <c>Imcodec.ObjectProperty</c> directly was considered and rejected. It is a git submodule
/// rather than a package - there is no <c>Imcodec.*</c> release on NuGet - and decoding an item template
/// needs the source-generated <c>PropertyClass</c> types for the exact client revision, which is a build
/// step Imlight performs and the launcher cannot. Pulling it in would also drag Imlight's own libraries,
/// and everything they depend on, into a launcher that ships as one executable.
/// </para>
/// <para>
/// Recovering the IDs from the archive by hand does not work either, and it was measured rather than
/// assumed: in the decoded 11.7 MB <c>TemplateManifest.xml</c> each template is a length-prefixed path with
/// no id beside it. The IDs exist in the payload but not adjacent to the path they belong to, so pairing
/// them means schema-aware decoding - which is the thing that needs the type cache. Until then the
/// catalogue below is a fixed, honest list of the item IDs referenced by Imlight's own stock config, so
/// the picker is still useful and never invents data.
/// </para>
/// </remarks>
public sealed class RootWadItemService {

    /// <summary>The file Imlight itself reads template definitions from inside <c>Root.wad</c>.</summary>
    public const string TemplateManifestFileName = "TemplateManifest.xml";

    /// <summary>
    /// Where the archive lives inside a game folder. The documented path comes first; the others are
    /// tolerated so an unusual install still works.
    /// </summary>
    private static readonly string[] RelativeSearchPaths = [
        GameFolderLocator.RelativeRootWad,          // Data\GameData\root.wad
        Path.Combine("Data", "root.wad"),
        Path.Combine("Data", "GameData", "Root.wad"),
        "root.wad",
    ];

    /// <summary>Locates the root archive inside a game folder, or returns <c>null</c>.</summary>
    public static string? FindRootWad(string? gameFolder) {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) {
            return null;
        }

        foreach (var relative in RelativeSearchPaths) {
            var candidate = Path.Combine(gameFolder, relative);
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Opens the archive index and reports what is there, without decoding templates.</summary>
    public RootWadProbe Probe(string? gameFolder) {
        var path = FindRootWad(gameFolder);
        if (path is null) {
            return RootWadProbe.Missing(
                string.IsNullOrWhiteSpace(gameFolder)
                    ? "No game folder selected."
                    : $"{GameFolderLocator.RelativeRootWad} was not found in the selected game folder.");
        }

        try {
            using var archive = KiWadArchive.Open(path);
            return new RootWadProbe(
                Found: true,
                Path: path,
                Version: archive.Version,
                FileCount: archive.Entries.Count,
                SizeBytes: archive.ArchiveSize,
                HasTemplateManifest: archive.FindEntry(TemplateManifestFileName) is not null,
                Error: null);
        }
        catch (Exception ex) {
            return RootWadProbe.Missing($"Root.wad could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Produces the item list for the starter-kit picker.
    /// </summary>
    /// <remarks>
    /// The server's own <c>manifests/item.manifest</c> is preferred whenever it is there, because it is the
    /// list of every item the game actually has, with the real template IDs - and the IDs are the whole
    /// point: they are what gets written into <c>Imlight.ini</c>, where a wrong number is an item nobody
    /// ever receives. With no manifest to read, the fixed list below is used instead, and the notice says
    /// so rather than presenting a dozen names as if they were the game's eighty thousand.
    /// </remarks>
    public Task<ItemCatalog> LoadCatalogAsync(
        string? gameFolder, string? serverPath = null, CancellationToken cancellationToken = default) {

        cancellationToken.ThrowIfCancellationRequested();

        var manifests = ImlightManifestReader.Read(serverPath);
        if (manifests.HasItems) {
            var items = manifests.Items
                .Select(i => new ItemTemplate(i.TemplateId, i.Name, i.Kind))
                .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.TemplateId)
                .ToList();

            var notice = $"Read from the server's own {ImlightManifestReader.ItemFileName}: "
                         + $"{items.Count:N0} items, with the template IDs Imlight.ini is written in.";

            return Task.FromResult(new ItemCatalog(items, IsStubbed: false, notice));
        }

        var probe = Probe(gameFolder);

        // The probe's own numbers go to the Log and to About; what the picker needs saying is only that
        // these names are the server's list rather than the game's - and now, that the server's list is
        // missing rather than unusable.
        var reason = manifests.Problem ?? $"{probe.Error}";
        var stubNotice = $"{reason} These {s_stubCatalog.Count} items are Imlight's own documented list "
                         + $"instead. Every item's template ID, name and school lives in Imlight's own "
                         + $"{ImlightManifestReader.RelativeDirectory}{Path.DirectorySeparatorChar}"
                         + $"{ImlightManifestReader.ItemFileName}, which Imlight's manifest generator "
                         + "writes - those numbers are not recoverable from the client's archive alone, "
                         + "so nothing here guesses at them.";

        return Task.FromResult(new ItemCatalog(s_stubCatalog, IsStubbed: true, stubNotice));
    }

    /// <summary>
    /// Item IDs and names taken from the comments in Imlight's stock <c>Config/Imlight.ini</c>, which
    /// is the only place in the project where item IDs are documented with their names.
    /// </summary>
    private static readonly List<ItemTemplate> s_stubCatalog = BuildStubCatalog();

    private static List<ItemTemplate> BuildStubCatalog() {
        var items = new List<ItemTemplate> {
            new(87256, "Wand-T1-016 (Tier 1 wand)", "Wand"),
            new(126983, "Deck-Tutorial-001", "Deck"),
        };

        // "ResistAll_Hat/Robe (QA/resist gear) 1317124-1317133"
        for (uint id = 1317124; id <= 1317133; id++) {
            items.Add(new ItemTemplate(id, $"ResistAll gear {id}", "QA / resist gear"));
        }

        items.Add(new ItemTemplate(1317234, "QA_SpeedBoots_100", "QA / resist gear"));
        items.Add(new ItemTemplate(284071, "Mount-Gryphon-Basic-001", "Mount"));

        return items.OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.TemplateId)
            .ToList();
    }
}
