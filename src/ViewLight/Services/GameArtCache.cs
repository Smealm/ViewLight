namespace ViewLight.Services;

/// <summary>The pieces of Wizard101 art the launcher can show.</summary>
public enum GameArt {
    /// <summary>The wide scene behind the Home banner.</summary>
    Hero,

    /// <summary>The Wizard101 wordmark.</summary>
    Logo,

    BoxArt,

    Cover,
}

/// <summary>
/// Downloads the game's own art on first use and keeps it beside the launcher's settings, so later
/// starts need no connection at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why nothing is shipped in the launcher.</b> This art belongs to KingsIsle, and ViewLight is
/// free software that redistributes nothing of theirs - no game files, no artwork. Fetching it from
/// the CDN it is already published on, into the user's own machine, keeps the project on the right
/// side of that line while still letting the launcher look like the game it starts.
/// </para>
/// <para>
/// A file that is cached but is not really a PNG - a captive-portal login page, a truncated download -
/// is refetched rather than trusted, and a download that fails never leaves a broken file behind.
/// </para>
/// </remarks>
public sealed class GameArtCache {

    /// <summary>Where each piece of art lives. These are the addresses the art is published at.</summary>
    private static readonly Dictionary<GameArt, string> s_urls = new() {
        [GameArt.Hero] = "https://cdn2.steamgriddb.com/hero/0c839ceedf50cdf2db05dfee9af83666.png",
        [GameArt.Logo] = "https://cdn2.steamgriddb.com/logo/4de20dd98d2ca78fd643bdc4f0a6359b.png",
        [GameArt.BoxArt] = "https://cdn2.steamgriddb.com/grid/b99384ca795334360e5794bc1b04bd3d.png",
        [GameArt.Cover] = "https://cdn2.steamgriddb.com/grid/e80715615b98d8c47332a458788d4b8a.png",
    };

    private const string FileNamePrefix = "wizard101-";

    /// <summary>The eight bytes every PNG starts with.</summary>
    internal static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The IEND chunk, which is how a PNG ends.</summary>
    internal static readonly byte[] PngEnd = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    private readonly HttpClient _http;
    private readonly string _directory;

    /// <summary>One download at a time: the pieces are small and only fetched once.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameArtCache(HttpClient? http = null, string? directory = null) {
        _http = http ?? Http.Files;
        _directory = directory ?? LauncherPaths.ArtCacheDirectory;
    }

    /// <summary>The address <paramref name="art"/> is fetched from.</summary>
    public static string UrlFor(GameArt art) => s_urls[art];

    /// <summary>Where <paramref name="art"/> is kept once it has been fetched.</summary>
    public string PathFor(GameArt art)
        => Path.Combine(_directory, $"{FileNamePrefix}{art.ToString().ToLowerInvariant()}.png");

    /// <summary>The cached file, when there is a usable one already.</summary>
    public string? Cached(GameArt art) {
        var path = PathFor(art);
        return IsUsablePng(path) ? path : null;
    }

    /// <summary>
    /// The local file for <paramref name="art"/>, fetching it if it is not cached yet.
    /// </summary>
    /// <returns>
    /// The path, or <c>null</c> when it could not be fetched - in which case the caller shows whatever
    /// it shows without art, rather than an empty image or a broken one.
    /// </returns>
    /// <param name="onDownloaded">Told when the file came from the network rather than the cache.</param>
    public async Task<string?> LoadAsync(
        GameArt art,
        Action? onDownloaded = null,
        CancellationToken cancellationToken = default) {

        if (Cached(art) is { } cached) {
            return cached;
        }

        // Not cached, so this is going to the network. Holding the gate keeps a second caller from
        // starting the same download.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            // Another caller may have finished the download while this one waited.
            if (Cached(art) is { } nowCached) {
                return nowCached;
            }

            var path = PathFor(art);
            var partial = path + ".part";

            Directory.CreateDirectory(_directory);

            try {
                using var response = await _http
                    .GetAsync(UrlFor(art), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) {
                    return null;
                }

                await using (var file = File.Create(partial)) {
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                // A refused or intercepted request answers with something that is not the image, and a
                // half-finished transfer is not one either. Neither is worth caching.
                if (!IsUsablePng(partial)) {
                    File.Delete(partial);
                    return null;
                }

                File.Move(partial, path, overwrite: true);
                onDownloaded?.Invoke();

                return path;
            }
            catch (Exception) {
                // Offline, refused, or cancelled. Whatever is left of the attempt goes away.
                TryDelete(partial);
                return null;
            }
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when the file exists and is a complete PNG: the right signature at the start <i>and</i> the
    /// end marker at the end.
    /// </summary>
    /// <remarks>
    /// Both ends are checked because a download cut short still starts with the signature - and a file
    /// that is cached but incomplete would otherwise be trusted forever, leaving the banner permanently
    /// artless with nothing explaining why.
    /// </remarks>
    public static bool IsUsablePng(string path) {
        try {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < PngSignature.Length + PngEnd.Length + 12) {
                return false;
            }

            using var stream = File.OpenRead(path);

            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
                || !header.SequenceEqual(PngSignature)) {
                return false;
            }

            stream.Seek(-PngEnd.Length, SeekOrigin.End);

            Span<byte> tail = stackalloc byte[8];
            return stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false) == tail.Length
                   && tail.SequenceEqual(PngEnd);
        }
        catch (Exception) {
            return false;
        }
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
