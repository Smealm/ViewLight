using System.IO.Compression;
using System.Text;

namespace ViewLight.Tests;

/// <summary>
/// Builds a KIWAD archive in memory using the layout Imcodec's <c>ArchiveParser</c> expects:
/// magic, version, file count, padding byte for version &gt;= 2, then the index, then the payloads.
/// </summary>
internal static class KiwadBuilder {

    public static byte[] Build(params (string Name, byte[] Data, bool Compressed)[] files) {
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
}
