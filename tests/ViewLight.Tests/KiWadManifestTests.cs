using System.IO.Compression;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class KiWadBlobTests {

    /// <summary>A blob in the shape the archive actually stores: header, then an optional deflate layer.</summary>
    private static byte[] Blob(byte[] payload) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("BINd"u8);
        writer.Write(7u);                        // revision
        writer.Write((byte)1);                   // flag
        writer.Write((uint) payload.Length);     // the length it decompresses to

        using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true)) {
            zlib.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void RecognisesTheBlobHeaderAndItsDeflateLayer() {
        var blob = Blob(Encoding.ASCII.GetBytes("ObjectData/Wand.xml"));

        Assert.True(KiWadBlob.IsBlob(blob));
        Assert.True(KiWadBlob.IsDeflated(blob));
        Assert.Equal("ObjectData/Wand.xml", Encoding.ASCII.GetString(KiWadBlob.Decode(blob)));
    }

    [Fact]
    public void ReturnsAnUndecompressedBlobUnchanged() {
        // A small template carries its object graph raw - the same magic, no deflate layer - and it must
        // come back whole rather than be mangled.
        var raw = new byte[] { (byte)'B', (byte)'I', (byte)'N', (byte)'d', 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7 };
        var blob = new byte[16];
        raw.CopyTo(blob, 0);

        Assert.True(KiWadBlob.IsBlob(blob));
        Assert.False(KiWadBlob.IsDeflated(blob));
        Assert.Same(blob, KiWadBlob.Decode(blob));
    }

    [Fact]
    public void SaysWhatALengthItClaims() {
        var payload = new byte[4321];
        var blob = Blob(payload);

        Assert.Equal(payload.Length, KiWadBlob.DeclaredLength(blob));
    }

    [Fact]
    public void APromisedButBrokenPayloadIsHandedBackRatherThanThrown() {
        var short_ = new byte[] { (byte)'B', (byte)'I', (byte)'N', (byte)'d', 7, 0, 0, 0, 1, 0, 0, 1, 0, 0x78, 0x9C, 0x01 };

        // The header promises deflate and the bytes are not a stream: the caller gets them back instead of
        // an exception, because a file that will not decode is a fact to report, not a failure to crash on.
        Assert.True(KiWadBlob.IsDeflated(short_));
        Assert.Same(short_, KiWadBlob.Decode(short_));
    }
}

public class RootWadTemplateManifestTests {

    /// <summary>Builds a KIWAD archive holding one uncompressed file, in the layout the reader expects.</summary>
    private static byte[] ArchiveWith(string name, byte[] data) {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var indexLength = 5 + 4 + 4 + 1 + 4 + 4 + 4 + 1 + 4 + 4 + nameBytes.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("KIWAD"));
        writer.Write(2u);
        writer.Write(1u);
        writer.Write((byte)0);
        writer.Write((uint) indexLength);
        writer.Write((uint) data.Length);
        writer.Write((uint) data.Length);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write(nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(data);

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BlobOf(string manifestText) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("BINd"u8);
        writer.Write(15u);
        writer.Write((byte)1);
        writer.Write((uint) manifestText.Length);

        using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true)) {
            zlib.Write(Encoding.ASCII.GetBytes(manifestText));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static TemplateManifest ReadFrom(string manifestText) {
        using var archive = KiWadArchive.Open(ArchiveWith(RootWadTemplateManifest.FileName, BlobOf(manifestText)));
        return RootWadTemplateManifest.Read(archive);
    }

    [Fact]
    public void ReadsThePathsOutOfADecodedManifest() {
        var manifest = ReadFrom(
            "ObjectData/Tier1/Decks/Deck-T1-001.xml\0ObjectData/Tier1/Decks/Deck-T1-002.xml\0"
            + "ObjectData/Wands/Wand-T1-016.xml\0");

        Assert.True(manifest.Found);
        Assert.True(manifest.Decoded);
        Assert.Equal(3, manifest.TemplatePaths);
    }

    [Fact]
    public void CountsBossTemplatesByTheirOwnNames() {
        var manifest = ReadFrom(
            "ObjectData/AV/AV-BogWitch-Boss-R12-01.xml\0ObjectData/AV/AV-BogWitch-Boss-R12-02.xml\0"
            + "ObjectData/AV/AV-Deer-Knight-Lancer-Boss-R11-01.xml\0ObjectData/PlainObject.xml\0");

        Assert.Equal(3, manifest.BossCount);
        Assert.Contains(manifest.BossNames, n => n == "AV-BogWitch-Boss-R12-01");
        Assert.DoesNotContain(manifest.BossNames, n => n == "PlainObject");
    }

    [Fact]
    public void DoesNotMistakeSpellsOrCinematicsForCreatures() {
        // Both trees are named after the creature they belong to without being one, which is exactly how a
        // creature list ends up full of card animations instead of monsters.
        var manifest = ReadFrom(
            "Spells/Mobs/Anubis Heal - MOB Only - Anubis.xml\0"
            + "Cinematics/Shadow Creature Big Bomb.xml\0"
            + "ObjectData/SummonedCreature.xml\0");

        Assert.Single(manifest.CreatureNames);
        Assert.Equal("SummonedCreature", manifest.CreatureNames[0]);
    }

    [Fact]
    public void SaysSoWhenThereIsNoManifestToRead() {
        using var archive = KiWadArchive.Open(ArchiveWith("SomethingElse.xml", [1, 2, 3, 4]));
        var manifest = RootWadTemplateManifest.Read(archive);

        Assert.False(manifest.Found);
        Assert.Contains(RootWadTemplateManifest.FileName, manifest.Problem);
    }

    [Fact]
    public void TheWordingSaysWhatWasNotReadAsWellAsWhatWas() {
        var manifest = new TemplateManifest(true, true, 137_321, ["AV-Boss-01"], ["SummonedCreature"], null);

        var text = ViewLight.ViewModels.MainWindowViewModel.DescribeManifest(manifest);

        Assert.Contains("137,321", text);
        Assert.Contains("1 of them bosses", text);
        Assert.Contains("not in it in a form this can read", text);
    }
}
