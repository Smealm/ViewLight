using System.IO.Compression;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class KiWadArchiveTests {

    /// <summary>
    /// Builds a KIWAD archive in memory using the layout Imcodec's <c>ArchiveParser</c> expects:
    /// magic, version, file count, padding byte for version &gt;= 2, then the index, then the payloads.
    /// </summary>
    private static byte[] BuildArchive(params (string Name, byte[] Data, bool Compressed)[] files) {
        var indexLength = 5 + 4 + 4 + 1;
        foreach (var file in files) {
            indexLength += 4 + 4 + 4 + 1 + 4 + 4 + Encoding.UTF8.GetByteCount(file.Name);
        }

        var payloads = new List<byte[]>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("KIWAD"));
        writer.Write(2u);
        writer.Write((uint)files.Length);
        writer.Write((byte)0);

        var offset = indexLength;
        foreach (var file in files) {
            var payload = file.Compressed ? Deflate(file.Data) : file.Data;
            payloads.Add(payload);

            writer.Write((uint)offset);
            writer.Write((uint)file.Data.Length);
            writer.Write((uint)payload.Length);
            writer.Write((byte)(file.Compressed ? 1 : 0));
            writer.Write(0u);

            var nameBytes = Encoding.UTF8.GetBytes(file.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            offset += payload.Length;
        }

        foreach (var payload in payloads) {
            writer.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Deflate(byte[] data) {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    [Fact]
    public void Open_ReadsTheArchiveIndex() {
        var archiveBytes = BuildArchive(
            ("Config/Imlight.ini", Encoding.UTF8.GetBytes("hello"), true),
            ("TemplateManifest.xml", [1, 2, 3, 4], false));

        using var archive = KiWadArchive.Open(archiveBytes);

        Assert.Equal(2u, archive.Version);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.Name == "Config/Imlight.ini");
        Assert.Contains(archive.Entries, e => e.Name == "TemplateManifest.xml");
    }

    [Fact]
    public void Open_RecordsSizesAndCompressionFlags() {
        var payload = Encoding.UTF8.GetBytes(new string('x', 500));

        using var archive = KiWadArchive.Open(BuildArchive(("big.txt", payload, true)));

        var entry = Assert.Single(archive.Entries);
        Assert.True(entry.IsCompressed);
        Assert.Equal((uint)payload.Length, entry.UncompressedSize);
        Assert.True(entry.CompressedSize < entry.UncompressedSize);
    }

    [Fact]
    public void ReadEntry_InflatesCompressedData() {
        var payload = Encoding.UTF8.GetBytes("the quick brown fox");

        using var archive = KiWadArchive.Open(BuildArchive(("file.txt", payload, true)));

        var entry = Assert.Single(archive.Entries);
        Assert.Equal(payload, archive.ReadEntry(entry));
    }

    [Fact]
    public void ReadEntry_ReturnsRawDataWhenStored() {
        byte[] payload = [10, 20, 30];

        using var archive = KiWadArchive.Open(BuildArchive(("file.bin", payload, false)));

        var entry = Assert.Single(archive.Entries);
        Assert.Equal(payload, archive.ReadEntry(entry));
    }

    [Fact]
    public void FindEntry_IsCaseInsensitive() {
        using var archive = KiWadArchive.Open(BuildArchive(("TemplateManifest.xml", [1], false)));

        Assert.NotNull(archive.FindEntry("templatemanifest.xml"));
        Assert.Null(archive.FindEntry("nope.xml"));
    }

    [Fact]
    public void Open_RejectsAFileThatIsNotAKiwad() {
        var junk = Encoding.ASCII.GetBytes("PK\u0003\u0004 this is a zip, not a wad");

        var error = Assert.Throws<InvalidDataException>(() => KiWadArchive.Open(junk));
        Assert.Contains("KIWAD", error.Message);
    }

    [Fact]
    public void Open_RejectsAnImplausibleFileCount() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Encoding.ASCII.GetBytes("KIWAD"));
            writer.Write(2u);
            writer.Write(uint.MaxValue);
        }

        Assert.Throws<InvalidDataException>(() => KiWadArchive.Open(stream.ToArray()));
    }

    [Fact]
    public void Open_RejectsAnIndexTruncatedMidEntry() {
        var full = BuildArchive(("abcdefgh.bin", [1, 2, 3, 4], false));

        // Cut into the file-name field, which the index writes ahead of the payload.
        var truncated = full[..(full.Length - 4 - 2)];

        Assert.Throws<InvalidDataException>(() => KiWadArchive.Open(truncated));
    }

    [Fact]
    public void Open_RejectsAFileTruncatedInsideTheHeader() {
        var full = BuildArchive(("a.txt", [1, 2, 3], false));

        // Not even the magic, version and file count are all present.
        Assert.Throws<InvalidDataException>(() => KiWadArchive.Open(full[..10]));
    }

    [Fact]
    public void Open_ReadsFromDisk() {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile("Root.wad", BuildArchive(("a.txt", [1, 2, 3], false)));

        using var archive = KiWadArchive.Open(path);

        Assert.Single(archive.Entries);
        Assert.Equal(path, archive.SourceName);
        Assert.Equal(new FileInfo(path).Length, archive.ArchiveSize);
    }
}
