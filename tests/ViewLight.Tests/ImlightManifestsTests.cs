using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class ImlightManifestsTests {

    [Fact]
    public void ReadsItemsWithTheirIdsSchoolsAndNames() {
        using var workspace = new TempWorkspace();
        var manifests = Path.Combine("server", ImlightManifestReader.RelativeDirectory);

        workspace.WriteFile(Path.Combine(manifests, ImlightManifestReader.ItemFileName),
            "87256                | Myth     | Wand-T1-016 | ObjectData/Tier1/Wands/Wand-T1-016.xml\n"
            + "126983               | NoSchool | Deck-Tutorial-001 | ObjectData/Tier1/Decks/Deck-Tutorial-001.xml\n");

        var read = ImlightManifestReader.Read(workspace.Path("server"));

        Assert.True(read.HasItems);
        Assert.Null(read.Problem);

        var wand = read.Items[0];
        Assert.Equal(87256u, wand.TemplateId);
        Assert.Equal("Myth", wand.School);
        Assert.Equal("Wand-T1-016", wand.Name);
        Assert.Equal("Wand", wand.Kind);
    }

    [Fact]
    public void MarksTheCreaturesTheGameNamesAsBosses() {
        using var workspace = new TempWorkspace();
        var manifests = Path.Combine("server", ImlightManifestReader.RelativeDirectory);

        workspace.WriteFile(Path.Combine(manifests, ImlightManifestReader.CreatureFileName),
            "35099                | Wraith-WCBoss-L05\n"
            + "35085                | Ghost-Blue-L01\n"
            + "35166                | Scarecrow-WCBoss-L03\n");

        var read = ImlightManifestReader.Read(workspace.Path("server"));

        Assert.True(read.HasCreatures);
        Assert.Equal(3, read.Creatures.Count);
        Assert.Equal(2, read.Bosses.Count);
        Assert.Contains(read.Bosses, b => b.Name == "Wraith-WCBoss-L05");
        Assert.DoesNotContain(read.Bosses, b => b.Name == "Ghost-Blue-L01");
    }

    [Fact]
    public void ReadsSpells() {
        using var workspace = new TempWorkspace();
        var manifests = Path.Combine("server", ImlightManifestReader.RelativeDirectory);

        workspace.WriteFile(Path.Combine(manifests, ImlightManifestReader.SpellFileName),
            "2066                 | Ra\n78544                | Pwn\n");

        var read = ImlightManifestReader.Read(workspace.Path("server"));

        Assert.True(read.HasSpells);
        Assert.Contains(read.Spells, s => s.TemplateId == 2066 && s.Name == "Ra");
    }

    [Fact]
    public void AServerWithNoManifestsSaysSoRatherThanReadingNothing() {
        using var workspace = new TempWorkspace();
        var server = workspace.CreateDirectory("server");

        var read = ImlightManifestReader.Read(server);

        Assert.False(read.HasItems);
        Assert.Contains(ImlightManifestReader.RelativeDirectory, read.Problem);

        Assert.False(ImlightManifestReader.Read(workspace.Path("nowhere")).HasItems);
        Assert.False(ImlightManifestReader.Read(null).HasItems);
    }

    [Fact]
    public void AnOddLineCostsOneEntryRatherThanTheWholeList() {
        using var workspace = new TempWorkspace();
        var manifests = Path.Combine("server", ImlightManifestReader.RelativeDirectory);

        workspace.WriteFile(Path.Combine(manifests, ImlightManifestReader.ItemFileName),
            "not an id | Myth | Nonsense | somewhere\n"
            + "87256 | Myth | Wand-T1-016 | ObjectData/Tier1/Wands/Wand-T1-016.xml\n"
            + "1234 | Myth |\n"                       // no name
            + "garbage\n"                              // not enough fields
            + "5 | NoSchool | LacedBoots | ObjectData/LacedBoots.xml\n");

        var read = ImlightManifestReader.Read(workspace.Path("server"));

        Assert.Equal(2, read.Items.Count);
        Assert.Contains(read.Items, i => i.Name == "Wand-T1-016");
        Assert.Contains(read.Items, i => i.Name == "LacedBoots");
    }

    [Theory]
    [InlineData("Wand-T1-016", "Wand")]
    [InlineData("Deck-Tutorial-001", "Deck")]
    [InlineData("LadiesFloppyHat", "Hat")]
    [InlineData("JesterCoat", "Other")]
    [InlineData("Mount-Gryphon-Basic-001", "Mount")]
    [InlineData("Resist-Hat-Boss-Fire-R02-001", "Hat")]
    public void AnItemsKindComesFromTheGamesOwnNameForIt(string name, string expected)
        => Assert.Equal(expected, ItemManifestEntry.KindOf(name));
}
