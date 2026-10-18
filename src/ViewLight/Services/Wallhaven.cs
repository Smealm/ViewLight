using System.Text.Json;

namespace ViewLight.Services;

/// <summary>
/// One wallpaper, as wallhaven describes it.
/// </summary>
/// <param name="Id">Wallhaven's own id, which its page for the wallpaper is keyed by.</param>
/// <param name="ThumbnailUrl">The copy the browsing grid shows.</param>
/// <param name="FullUrl">The wallpaper itself, at the resolution the site holds it at.</param>
/// <param name="Resolution">Its pixel size, which is worth reading before choosing one as a banner.</param>
/// <param name="FileSize">Its size in bytes, so a slow choice can be recognised as one.</param>
/// <param name="PageUrl">The wallpaper's own page on the site, for looking at it properly.</param>
public sealed record WallhavenWallpaper(
    string Id, string ThumbnailUrl, string FullUrl, string Resolution, long FileSize, string PageUrl) {

    /// <summary>What a grid cell says about it: the resolution, and how big the file behind it is.</summary>
    public string Caption => FileSize > 0
        ? $"{Resolution} · {Megabytes(FileSize)} MB"
        : Resolution;

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0");
}

/// <summary>
/// One page of results, and whatever was wrong with asking for it.
/// </summary>
/// <param name="Walls">The wallpapers on this page.</param>
/// <param name="Page">Which page this is.</param>
/// <param name="LastPage">How many pages there are in total, so the browser knows where the end is.</param>
/// <param name="Total">How many wallpapers the listing holds altogether.</param>
/// <param name="Problem">Why there are none, or <c>null</c> when there are some.</param>
public sealed record WallhavenPage(
    IReadOnlyList<WallhavenWallpaper> Walls, int Page, int LastPage, int Total, string? Problem = null) {

    public static WallhavenPage Failed(string problem) => new([], 1, 1, 0, problem);
}

/// <summary>
/// wallhaven.cc, asked for the one listing a banner is worth browsing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The weekly toplist, and not the whole site.</b> Wallhaven holds millions of wallpapers, and a browser
/// that could search all of them would be a worse way to find a banner than the site itself. The listing
/// asked for here is the site's own <i>toplist, last week</i>, SFW, general and anime categories - a few
/// hundred walls that change week to week, so there is something new to look at and the quality is high
/// enough to be worth putting behind a profile. Searching is the one addition, because a person who wants a
/// particular thing should be able to ask for it.
/// </para>
/// <para>
/// <b>Nothing is cached but the pictures.</b> The listing is a few kilobytes and changes weekly, so it is
/// fetched when it is looked at; the thumbnails and the wallpapers are the expensive part and are kept
/// beside the launcher's other fetched art - see <see cref="ImageCache"/> - which is what makes paging
/// through the grid a second time instant and offline.
/// </para>
/// </remarks>
public sealed class WallhavenClient {

    private const string SearchEndpoint = "https://wallhaven.cc/api/v1/search";

    /// <summary>
    /// The listing the issue behind this screen asked for: the weekly toplist.
    /// </summary>
    /// <remarks>
    /// <c>categories=110</c> is general and anime, <c>purity=100</c> is SFW only, and <c>topRange=1w</c> is
    /// the last week's toplist, newest first. Fixed rather than adjustable: an adjustable purity on a
    /// launcher that puts the picture behind a player's name is not a feature.
    /// </remarks>
    public const string ToplistQuery = "categories=110&purity=100&topRange=1w&sorting=toplist&order=desc";

    /// <summary>The same filters, for a search a person typed.</summary>
    private const string SearchFilters = "categories=110&purity=100&sorting=relevance&order=desc";

    private readonly HttpClient _http;
    private readonly ImageCache _pictures;

    public WallhavenClient(HttpClient? http = null, string? directory = null) {
        _http = http ?? Http.Files;
        _pictures = new ImageCache(directory ?? LauncherPaths.WallhavenDirectory, http);
    }

    /// <summary>
    /// One page of the weekly toplist.
    /// </summary>
    /// <param name="page">Which page, counting from one.</param>
    public Task<WallhavenPage> ToplistAsync(int page = 1, CancellationToken token = default)
        => ReadAsync($"{SearchEndpoint}?{ToplistQuery}&page={Math.Max(1, page)}", page, token);

    /// <summary>
    /// One page of a search, or the toplist when there is nothing to search for.
    /// </summary>
    /// <param name="query">What to look for.</param>
    /// <param name="page">Which page, counting from one.</param>
    public Task<WallhavenPage> SearchAsync(string query, int page = 1, CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(query)) {
            return ToplistAsync(page, token);
        }

        var url = $"{SearchEndpoint}?{SearchFilters}&q={Uri.EscapeDataString(query.Trim())}"
                  + $"&page={Math.Max(1, page)}";

        return ReadAsync(url, page, token);
    }

    /// <summary>The thumbnail for a wallpaper, fetched and kept on first sight.</summary>
    public Task<string?> ThumbnailAsync(WallhavenWallpaper wallpaper, CancellationToken token = default)
        => _pictures.LoadAsync(ImageCache.FileNameFor(wallpaper.ThumbnailUrl), wallpaper.ThumbnailUrl, token);

    /// <summary>The wallpaper itself, fetched and kept on first sight.</summary>
    public Task<string?> WallpaperAsync(WallhavenWallpaper wallpaper, CancellationToken token = default)
        => _pictures.LoadAsync(ImageCache.FileNameFor(wallpaper.FullUrl), wallpaper.FullUrl, token);

    /// <summary>
    /// One page of the API, read into the shape the browser uses.
    /// </summary>
    /// <remarks>
    /// Everything answers rather than throws, and a failure carries the sentence the screen shows: a
    /// launcher that cannot reach wallhaven must say so rather than show an empty grid, because an empty
    /// grid reads as "there is nothing to browse".
    /// </remarks>
    private async Task<WallhavenPage> ReadAsync(string url, int page, CancellationToken token) {
        try {
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                return WallhavenPage.Failed(response.StatusCode switch {
                    System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized =>
                        "Wallhaven refused the request. Its API needs an api key for some listings; the "
                        + "weekly toplist does not, so this is likely a temporary block.",
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "Wallhaven is rate limiting this machine. Wait a moment and look again.",
                    _ => $"Wallhaven answered {(int) response.StatusCode} instead of a listing.",
                });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            return Parse(await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false), page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or InvalidOperationException) {
            return WallhavenPage.Failed($"Wallhaven could not be reached ({ex.Message}).");
        }
    }

    /// <summary>
    /// Reads the API's own answer: the walls, and how many pages there are of them.
    /// </summary>
    /// <remarks>
    /// Every field is optional on purpose. This is somebody else's API and a wallpaper that is missing a
    /// thumbnail is still a wallpaper; the entry is skipped when it has nothing to fetch at all, which is
    /// the only case where showing it would be showing a blank card.
    /// </remarks>
    public static WallhavenPage Parse(JsonDocument document, int requestedPage) {
        var walls = new List<WallhavenWallpaper>();

        if (document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array) {

            foreach (var entry in data.EnumerateArray()) {
                var id = Text(entry, "id");
                var full = Text(entry, "path");

                if (id is null || full is null) {
                    continue;
                }

                var thumbnail = entry.TryGetProperty("thumbs", out var thumbs)
                                && thumbs.ValueKind == JsonValueKind.Object
                    ? Text(thumbs, "large") ?? Text(thumbs, "original") ?? Text(thumbs, "small")
                    : null;

                walls.Add(new WallhavenWallpaper(
                    id,
                    thumbnail ?? full,
                    full,
                    Text(entry, "resolution") ?? "unknown size",
                    Number(entry, "file_size"),
                    Text(entry, "url") ?? $"https://wallhaven.cc/w/{id}"));
            }
        }

        var page = requestedPage;
        var lastPage = requestedPage;
        var total = walls.Count;

        if (document.RootElement.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object) {

            var current = Number(meta, "current_page");
            var last = Number(meta, "last_page");
            var count = Number(meta, "total");

            page = current > 0 ? (int) current : requestedPage;
            lastPage = last > 0 ? (int) Math.Max(1, last) : page;
            total = count > 0 ? (int) count : walls.Count;
        }

        return new WallhavenPage(walls, page, lastPage, total, null);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static long Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : 0;
}
