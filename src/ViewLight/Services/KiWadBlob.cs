using System.IO.Compression;

namespace ViewLight.Services;

/// <summary>
/// The container KingsIsle wraps archive entries in, and the one piece of it that can be understood
/// without the game's own type cache.
/// </summary>
/// <remarks>
/// <para>
/// Every data file in <c>Root.wad</c> named <c>.xml</c> is not XML: it is a <c>BINd</c> blob, a binary
/// object graph whose field names are replaced by 32-bit string hashes. Reading the <i>fields</i> out of
/// one needs Imcodec's <c>BindSerializer</c> plus the generated type cache for the exact client revision -
/// a compile-time code-generation step that a launcher cannot do at runtime.
/// </para>
/// <para>
/// What <i>can</i> be understood is the box those graphs arrive in, and it is understood here: the
/// <c>BINd</c> header, and the zlib layer some blobs carry behind it. Large blobs - the template manifest
/// is 1.4 MB of it - are stored deflated inside the already-decompressed entry, and unwrapping that is the
/// difference between a screenful of noise and eleven megabytes of the game's own file paths. Everything
/// above this layer still has to read the bytes it gets back for what they look like; this layer is exact.
/// </para>
/// </remarks>
public static class KiWadBlob {

    /// <summary>The magic every <c>.xml</c> entry in the archive actually starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "BINd"u8;

    /// <summary>
    /// How long the header in front of an optional deflate layer is, worked out from the archive itself:
    /// the magic, a revision, a flag, and the length the payload decompresses to.
    /// </summary>
    public const int HeaderLength = 13;

    /// <summary>
    /// The first two bytes of a zlib stream, which is what says whether a blob is deflated behind its
    /// header. Not every blob is: small template files carry their object graph raw, and the two arrive
    /// with the same magic.
    /// </summary>
    private const byte ZlibFirstByte = 0x78;
    private const byte ZlibSecondByte = 0x9C;

    /// <summary>Whether this looks like a KingsIsle blob rather than a plain file.</summary>
    public static bool IsBlob(ReadOnlySpan<byte> data)
        => data.Length >= HeaderLength && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>
    /// Whether the blob carries a deflate layer that <see cref="Decode"/> would unwrap.
    /// </summary>
    public static bool IsDeflated(ReadOnlySpan<byte> data)
        => IsBlob(data) && data[HeaderLength] == ZlibFirstByte && data[HeaderLength + 1] == ZlibSecondByte;

    /// <summary>
    /// The usable bytes of a blob: the payload when it is deflated, and the blob itself when it is not.
    /// </summary>
    /// <remarks>
    /// Never throws on data that is simply unknown: a blob that will not inflate is handed back whole, so
    /// a caller scanning for text sees the raw bytes rather than an exception it has to handle. The one
    /// case that is reported is a header that promises a deflate layer which is genuinely broken, because
    /// that is a fact about the file rather than about the caller's patience.
    /// </remarks>
    public static byte[] Decode(byte[] blob) {
        if (!IsDeflated(blob)) {
            return blob;
        }

        try {
            using var compressed = new MemoryStream(blob, HeaderLength, blob.Length - HeaderLength, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream(DeclaredLength(blob));

            zlib.CopyTo(output);

            // A truncated stream does not throw on the way out - it simply ends - so the short read is
            // caught here instead: a payload the header says is not empty, which inflated to nothing, is a
            // file that did not decode, and saying so is the whole point of getting the bytes back whole.
            if (output.Length == 0 && DeclaredLength(blob) > 0) {
                return blob;
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) {
            return blob;
        }
    }

    /// <summary>
    /// How many bytes the payload decompresses to, as the header records it. Reported rather than trusted:
    /// it is a claim made by the file, and it is used only to size the buffer.
    /// </summary>
    public static int DeclaredLength(ReadOnlySpan<byte> blob)
        => IsBlob(blob)
            ? (int) Math.Min(int.MaxValue, BitConverter.ToUInt32(blob[(HeaderLength - 4)..HeaderLength]))
            : 0;
}
