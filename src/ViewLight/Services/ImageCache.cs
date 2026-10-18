namespace ViewLight.Services;

/// <summary>
/// Keeps images the launcher fetched rather than shipped: download once, keep beside the rest of its
/// cache, and hand the file back from then on.
/// </summary>
/// <remarks>
/// <para>
/// The same rule as <see cref="GameArtCache"/> and for the same reason - the art belongs to whoever made
/// it, and this launcher redistributes nothing of theirs. What differs is the shape: that one knows a
/// fixed handful of pieces by name, while the school icons, the gamerpics and the wallpapers are sets that
/// are looked up by name at the moment they are wanted. So this is a directory, a name, an address, and a
/// cache file - and the caller decides what the names mean.
/// </para>
/// <para>
/// <b>A file that downloaded badly is not kept.</b> A captive portal answers with a login page, a transfer
/// that stopped halfway answers with half an image, and both would otherwise be handed back forever as the
/// picture of something. So a download only becomes the cached file once it reads as a whole PNG or JPEG,
/// and anything else is deleted and reported as nothing.
/// </para>
/// </remarks>
public sealed class ImageCache {

    private readonly HttpClient _http;
    private readonly string _directory;

    /// <summary>
    /// One download at a time per cache.
    /// </summary>
    /// <remarks>
    /// A grid of thumbnails asks for dozens of files at once, and two looks at the same picture - a page
    /// that is looked at twice, a click while the page is still filling in - must not become two downloads
    /// of it. Serialising them also keeps the launcher from opening sixty sockets to one host.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ImageCache(string directory, HttpClient? http = null) {
        _directory = directory;
        _http = http ?? Http.Files;
    }

    /// <summary>Where a cached image lives.</summary>
    public string PathFor(string fileName) => Path.Combine(_directory, fileName);

    /// <summary>The cached file, when there is a usable one already.</summary>
    public string? Cached(string fileName) {
        var path = PathFor(fileName);
        return IsUsableImage(path) ? path : null;
    }

    /// <summary>
    /// The local file for a name, fetching it from <paramref name="url"/> if it is not cached yet.
    /// </summary>
    /// <returns>The path, or <c>null</c> when there is no usable file to hand back.</returns>
    /// <param name="fileName">The name to keep it under, which must be unique to the address.</param>
    /// <param name="url">Where it is published.</param>
    public async Task<string?> LoadAsync(
        string fileName, string url, CancellationToken token = default) {

        if (Cached(fileName) is { } cached) {
            return cached;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);

        try {
            // Another caller may have finished the same download while this one waited.
            if (Cached(fileName) is { } nowCached) {
                return nowCached;
            }

            var path = PathFor(fileName);
            var partial = path + ".part";

            Directory.CreateDirectory(_directory);

            try {
                using var response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) {
                    return null;
                }

                await using (var file = File.Create(partial)) {
                    await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
                }

                if (!IsUsableImage(partial)) {
                    TryDelete(partial);
                    return null;
                }

                File.Move(partial, path, overwrite: true);
                return path;
            }
            catch (Exception) {
                // Offline, refused, or cancelled: whatever is left of the attempt goes away rather than
                // being mistaken for a picture next time.
                TryDelete(partial);
                return null;
            }
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether a file exists and reads as a whole PNG or JPEG.
    /// </summary>
    /// <remarks>
    /// Both ends are checked, because a download cut short still starts with the right signature - and a
    /// file that is cached but incomplete would be trusted forever, leaving whatever it was for permanently
    /// blank with nothing saying why.
    /// </remarks>
    public static bool IsUsableImage(string path) {
        try {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 16) {
                return false;
            }

            using var stream = File.OpenRead(path);

            Span<byte> header = stackalloc byte[3];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) {
                return false;
            }

            // JPEG: FFD8FF at the front, FFD9 at the end.
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) {
                stream.Seek(-2, SeekOrigin.End);

                Span<byte> end = stackalloc byte[2];
                return stream.ReadAtLeast(end, end.Length, throwOnEndOfStream: false) == end.Length
                       && end[0] == 0xFF && end[1] == 0xD9;
            }

            // PNG: the signature at the front and IEND at the end.
            stream.Seek(0, SeekOrigin.Begin);

            Span<byte> png = stackalloc byte[8];
            if (stream.ReadAtLeast(png, png.Length, throwOnEndOfStream: false) != png.Length
                || !png.SequenceEqual(GameArtCache.PngSignature)) {
                return false;
            }

            stream.Seek(-8, SeekOrigin.End);

            Span<byte> tail = stackalloc byte[8];
            return stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false) == tail.Length
                   && tail.SequenceEqual(GameArtCache.PngEnd);
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>The extension a cached file should carry, from the address it is published at.</summary>
    public static string ExtensionOf(string url) {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : url;
        var extension = Path.GetExtension(path);

        return extension.ToLowerInvariant() switch {
            ".jpg" or ".jpeg" => ".jpg",
            ".gif" => ".gif",
            _ => ".png",
        };
    }

    /// <summary>
    /// A name that is safe as a file name and unique to the address it is fetched from.
    /// </summary>
    /// <remarks>
    /// Titles and wallpaper ids are not file names: they carry slashes, colons and quotes, and two
    /// wallpapers can share a resolution. So the name is a short hash of the address - unique by
    /// construction - with the extension the address ends in.
    /// </remarks>
    public static string FileNameFor(string url) {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));

        return Convert.ToHexStringLower(hash)[..32] + ExtensionOf(url);
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (Exception) {
            // A leftover .part file is untidy, not a problem: the next attempt overwrites it.
        }
    }
}
