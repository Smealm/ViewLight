using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class RootWadItemServiceTests {

    private static byte[] MinimalRootWad() {
        var name = Encoding.UTF8.GetBytes(RootWadItemService.TemplateManifestFileName);
        var indexLength = 5 + 4 + 4 + 1 + 4 + 4 + 4 + 1 + 4 + 4 + name.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("KIWAD"));
        writer.Write(2u);
        writer.Write(1u);
        writer.Write((byte)0);
        writer.Write((uint)indexLength);
        writer.Write(4u);
        writer.Write(4u);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write(name.Length);
        writer.Write(name);
        writer.Write(new byte[] { 1, 2, 3, 4 });

        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void FindRootWad_UsesTheDocumentedRelativePath() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        Assert.Null(RootWadItemService.FindRootWad(gameFolder));

        // Data\GameData\root.wad, relative to the game folder.
        var expected = workspace.WriteFile(
            Path.Combine("client", GameFolderLocator.RelativeRootWad), [1, 2, 3]);

        Assert.Equal(expected, RootWadItemService.FindRootWad(gameFolder));
    }

    [Fact]
    public void FindRootWad_ToleratesOtherLayouts() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        var besideTheExe = workspace.WriteFile(Path.Combine("client", "root.wad"), [1, 2, 3]);

        Assert.Equal(besideTheExe, RootWadItemService.FindRootWad(gameFolder));
    }

    [Fact]
    public void FindRootWad_ToleratesMissingFolders() {
        Assert.Null(RootWadItemService.FindRootWad(@"Z:\nope"));
        Assert.Null(RootWadItemService.FindRootWad(null));
    }

    [Fact]
    public void Probe_ReportsAMissingClientClearly() {
        using var workspace = new TempWorkspace();
        var service = new RootWadItemService();

        var probe = service.Probe(workspace.CreateDirectory("empty-client"));

        Assert.False(probe.Found);
        Assert.Contains(GameFolderLocator.RelativeRootWad, probe.Error);
        Assert.Contains("was not found", probe.Error);
    }

    [Fact]
    public void Probe_ReadsTheRealArchiveIndex() {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("client", "Root.wad"), MinimalRootWad());

        var probe = new RootWadItemService().Probe(workspace.Path("client"));

        Assert.True(probe.Found);
        Assert.Equal(2u, probe.Version);
        Assert.Equal(1, probe.FileCount);
        Assert.True(probe.HasTemplateManifest);
        Assert.Null(probe.Error);
    }

    [Fact]
    public void Probe_ReportsACorruptArchiveInsteadOfThrowing() {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("client", "Root.wad"), Encoding.ASCII.GetBytes("not a wad at all"));

        var probe = new RootWadItemService().Probe(workspace.Path("client"));

        Assert.False(probe.Found);
        Assert.Contains("could not be read", probe.Error);
    }

    [Fact]
    public async Task LoadCatalog_IsHonestAboutBeingStubbed() {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("client", "Root.wad"), MinimalRootWad());

        var catalog = await new RootWadItemService().LoadCatalogAsync(workspace.Path("client"));

        Assert.True(catalog.IsStubbed);

        // The notice is read by users, so it says in plain words that these names come from Imlight's own
        // list rather than out of the game, and names the file that would give the real ones instead of
        // leaving the picker looking like the whole of what the game has.
        Assert.Contains("Imlight's own documented list", catalog.Notice);
        Assert.Contains(ImlightManifestReader.ItemFileName, catalog.Notice);
        Assert.NotEmpty(catalog.Items);
    }

    [Fact]
    public async Task LoadCatalog_UsesTheServersOwnItemManifestWhenThereIsOne() {
        using var workspace = new TempWorkspace();
        var server = workspace.CreateDirectory("server");
        var manifests = Path.Combine("server", ImlightManifestReader.RelativeDirectory);

        workspace.WriteFile(Path.Combine(manifests, ImlightManifestReader.ItemFileName),
            "87256                | Myth     | Wand-T1-016 | ObjectData/Tier1/Wands/Wand-T1-016.xml\n" +
            "126983               | NoSchool | Deck-Tutorial-001 | ObjectData/Tier1/Decks/Deck-Tutorial-001.xml\n");

        var catalog = await new RootWadItemService().LoadCatalogAsync(workspace.Path("client"), server);

        Assert.False(catalog.IsStubbed);
        Assert.Contains("item.manifest", catalog.Notice);

        var wand = catalog.Items.Single(i => i.TemplateId == 87256);
        Assert.Equal("Wand-T1-016", wand.Name);
        Assert.Equal("Wand", wand.Category);

        var deck = catalog.Items.Single(i => i.TemplateId == 126983);
        Assert.Equal("Deck", deck.Category);
    }

    [Fact]
    public async Task LoadCatalog_FallsBackToTheDocumentedListWithoutAManifest() {
        using var workspace = new TempWorkspace();
        var server = workspace.CreateDirectory("server");

        var catalog = await new RootWadItemService().LoadCatalogAsync(workspace.Path("client"), server);

        Assert.True(catalog.IsStubbed);
        Assert.Contains(catalog.Items, i => i.TemplateId == 87256);
    }

    [Fact]
    public async Task LoadCatalog_ListsTheItemsImlightsStockConfigDocuments() {
        using var workspace = new TempWorkspace();

        var catalog = await new RootWadItemService().LoadCatalogAsync(workspace.Path("client"));

        // The two IDs the starter preset uses must always be selectable.
        Assert.Contains(catalog.Items, i => i.TemplateId == 87256);
        Assert.Contains(catalog.Items, i => i.TemplateId == 126983);
        Assert.Contains(catalog.Items, i => i.TemplateId == 284071);
    }
}
