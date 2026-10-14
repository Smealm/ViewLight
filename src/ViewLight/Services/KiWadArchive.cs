using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ViewLight.Services;

/// <summary>One file inside a KIWAD archive, as described by the archive index.</summary>
public sealed class KiWadEntry {

    public required string Name { get; init; }
    public uint Offset { get; init; }
    public uint UncompressedSize { get; init; }
    public uint CompressedSize { get; init; }
    public bool IsCompressed { get; init; }
    public uint Crc32 { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Reader for KingsIsle <c>.wad</c> archives (the client's <c>Root.wad</c> and friends).
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> The layout is taken from Imcodec's <c>ArchiveParser</c> (BSD-3-Clause, Revive101),
/// the same code Imlight uses to open this archive:
/// </para>
/// <list type="bullet">
///   <item><description>5 bytes magic <c>KIWAD</c></description></item>
///   <item><description><c>uint32</c> version, <c>uint32</c> file count</description></item>
///   <item><description>one padding byte when version &gt;= 2</description></item>
///   <item><description>per file: <c>uint32</c> offset, <c>uint32</c> size, <c>uint32</c> compressed size,
///     <c>byte</c> compressed flag, <c>uint32</c> crc32, <c>int32</c> name length, UTF-8 name bytes</description></item>
/// </list>
/// <para>
/// Opening an archive only reads the index, which is what this launcher needs today. Reading an
/// individual file is supported too (zlib-wrapped deflate), but the *contents* of the client's
/// template files are a binary object graph that only Imcodec's generated type cache can decode - see
/// <see cref="RootWadItemService"/> for where that gap is documented.
/// </para>
/// </remarks>
public sealed class KiWadArchive : IDisposable {

    private const string Magic = "KIWAD";
    private const int MaxReasonableFileCount = 5_000_000;
    private const int MaxReasonableNameLength = 4096;

    private readonly Stream? _stream;
    private readonly bool _ownsStream;

    public uint Version { get; private init; }

    public IReadOnlyList<KiWadEntry> Entries { get; private init; } = [];

    /// <summary>Where the archive was loaded from, for diagnostics.</summary>
    public string SourceName { get; private init; } = string.Empty;

    public long ArchiveSize { get; private init; }

    private KiWadArchive(Stream? stream, bool ownsStream) {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>Opens an archive and reads its index.</summary>
    /// <exception cref="InvalidDataException">The file is not a KIWAD archive.</exception>
    public static KiWadArchive Open(string path) {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            return ReadIndex(stream, ownsStream: true, sourceName: path);
        }
        catch {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads an archive index from an in-memory copy (used by tests).</summary>
    public static KiWadArchive Open(byte[] data, string sourceName = "<memory>") {
        var stream = new MemoryStream(data, writable: false);
        return ReadIndex(stream, ownsStream: true, sourceName: sourceName);
    }

    private static KiWadArchive ReadIndex(Stream stream, bool ownsStream, string sourceName) {
        try {
            return ReadIndexCore(stream, ownsStream, sourceName);
        }
        catch (EndOfStreamException ex) {
            // Surface truncation as the same error type callers already handle.
            throw new InvalidDataException(
                $"'{sourceName}' ends before its index does; the archive is truncated.", ex);
        }
    }

    private static KiWadArchive ReadIndexCore(Stream stream, bool ownsStream, string sourceName) {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || Encoding.ASCII.GetString(magic) != Magic) {
            throw new InvalidDataException(
                $"'{sourceName}' is not a KIWAD archive (bad magic header).");
        }

        var version = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();

        if (fileCount > MaxReasonableFileCount) {
            throw new InvalidDataException(
                $"'{sourceName}' declares an implausible file count ({fileCount}); the archive looks corrupt.");
        }

        // Newer archive revisions insert a padding byte after the header.
        if (version >= 2) {
            _ = reader.ReadByte();
        }

        var entries = new List<KiWadEntry>((int)fileCount);
        for (var i = 0; i < fileCount; i++) {
            var offset = reader.ReadUInt32();
            var uncompressedSize = reader.ReadUInt32();
            var compressedSize = reader.ReadUInt32();
            var isCompressed = reader.ReadByte() != 0;
            var crc32 = reader.ReadUInt32();
            var nameLength = reader.ReadInt32();

            if (nameLength is < 0 or > MaxReasonableNameLength) {
                throw new InvalidDataException(
                    $"'{sourceName}' has an invalid file name length ({nameLength}) at entry {i}.");
            }

            var nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength) {
                throw new InvalidDataException(
                    $"'{sourceName}' ended early while reading file name for entry {i}.");
            }

            var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
            if (name.Length > 0) {
                entries.Add(new KiWadEntry {
                    Name = name,
                    Offset = offset,
                    UncompressedSize = uncompressedSize,
                    CompressedSize = compressedSize,
                    IsCompressed = isCompressed,
                    Crc32 = crc32,
                });
            }
        }

        return new KiWadArchive(ownsStream ? stream : null, ownsStream) {
            Version = version,
            Entries = entries,
            SourceName = sourceName,
            ArchiveSize = stream.Length,
        };
    }

    public KiWadEntry? FindEntry(string name)
        => Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads and decompresses a single entry.</summary>
    public byte[] ReadEntry(KiWadEntry entry) {
        if (_stream is null) {
            throw new InvalidOperationException("Archive was opened without a seekable backing stream.");
        }

        lock (_stream) {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            var length = entry.IsCompressed ? entry.CompressedSize : entry.UncompressedSize;

            if (length > int.MaxValue) {
                throw new InvalidDataException($"Entry '{entry.Name}' is too large to read into memory.");
            }

            var buffer = new byte[length];
            _stream.ReadExactly(buffer);

            if (!entry.IsCompressed) {
                return buffer;
            }

            using var compressed = new MemoryStream(buffer, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream(entry.UncompressedSize > 0 ? (int)entry.UncompressedSize : 0);
            zlib.CopyTo(output);
            return output.ToArray();
        }
    }

    public void Dispose() {
        if (_ownsStream) {
            _stream?.Dispose();
        }
    }
}
