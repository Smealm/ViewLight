using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher ships none of the game's art - it fetches it once and keeps it - so what happens when
/// the fetch is refused, cut short, or answered with something that is not an image is the whole
/// contract. Every one of those has to end in "no art", never in a broken or permanently poisoned
/// cache.
/// </summary>
public class GameArtCacheTests {

    [Fact]
    public async Task DownloadsAPieceAndKeepsIt() {
        using var workspace = new TempWorkspace();
        var payload = Png("banner scene");

        var handler = new FakeHttpHandler(_ => Ok(payload));
        var cache = new GameArtCache(new HttpClient(handler), workspace.Path("art"));

        var path = await cache.LoadAsync(GameArt.Hero);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path!));
        Assert.Single(handler.Requests);
        Assert.StartsWith(GameArtCache.UrlFor(GameArt.Hero), handler.Requests[0]);
    }

    [Fact]
    public async Task UsesTheCachedCopyWithoutAskingTheNetworkAgain() {
        using var workspace = new TempWorkspace();
        var artDirectory = workspace.CreateDirectory("art");
        var cache = new GameArtCache(ThrowingClient(), artDirectory);

        // What a previous run would have left behind.
        var cached = cache.PathFor(GameArt.Logo);
        await File.WriteAllBytesAsync(cached, Png("wordmark"));

        var path = await cache.LoadAsync(GameArt.Logo);

        Assert.Equal(cached, path);
        Assert.Equal(cached, cache.Cached(GameArt.Logo));
    }

    [Fact]
    public async Task RefusesAnAnswerThatIsNotAnImage() {
        using var workspace = new TempWorkspace();

        // What a captive portal or an error page looks like: 200 OK, and HTML.
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("<html><body>Sign in to the network</body></html>"),
        });

        var cache = new GameArtCache(new HttpClient(handler), workspace.Path("art"));

        Assert.Null(await cache.LoadAsync(GameArt.Hero));
        Assert.Null(cache.Cached(GameArt.Hero));

        // Nothing is left behind for the next run to trip over, partial file included.
        Assert.Empty(Directory.GetFiles(workspace.Path("art")));
    }

    [Fact]
    public async Task RefusesARefusedDownload() {
        using var workspace = new TempWorkspace();
        var cache = new GameArtCache(FakeHttpHandler.Returning(HttpStatusCode.NotFound), workspace.Path("art"));

        Assert.Null(await cache.LoadAsync(GameArt.Cover));
    }

    [Fact]
    public async Task RefusesATruncatedDownload() {
        using var workspace = new TempWorkspace();

        // Right signature, no end marker: a transfer that stopped half way.
        var truncated = Png("wordmark")[..^8];
        var cache = new GameArtCache(FakeHttpHandler.Serving(truncated), workspace.Path("art"));

        Assert.Null(await cache.LoadAsync(GameArt.Logo));
        Assert.Empty(Directory.GetFiles(workspace.Path("art")));
    }

    [Fact]
    public async Task ReplacesACachedFileThatIsNotAWholeImage() {
        using var workspace = new TempWorkspace();
        var artDirectory = workspace.CreateDirectory("art");

        // A previous version, an interrupted write, or a hand-placed file that is not a PNG at all.
        var cache = new GameArtCache(FakeHttpHandler.Serving(Png("the real one")), artDirectory);
        await File.WriteAllBytesAsync(cache.PathFor(GameArt.Hero), "not a png"u8.ToArray());

        Assert.Null(cache.Cached(GameArt.Hero));

        var path = await cache.LoadAsync(GameArt.Hero);

        Assert.NotNull(path);
        Assert.True(GameArtCache.IsUsablePng(path!));
    }

    [Fact]
    public async Task SurvivesBeingOfflineWithACachedCopyAlreadyOnDisk() {
        using var workspace = new TempWorkspace();
        var artDirectory = workspace.CreateDirectory("art");

        // The first run fetched it; the second has no connection at all.
        var cache = new GameArtCache(ThrowingClient(), artDirectory);
        await File.WriteAllBytesAsync(cache.PathFor(GameArt.BoxArt), Png("box art"));

        Assert.Equal(cache.PathFor(GameArt.BoxArt), await cache.LoadAsync(GameArt.BoxArt));
    }

    [Fact]
    public void EveryPieceHasItsOwnAddressAndItsOwnFile() {
        using var workspace = new TempWorkspace();
        var cache = new GameArtCache(ThrowingClient(), workspace.Path("art"));

        var art = Enum.GetValues<GameArt>();

        Assert.Equal(art.Length, art.Select(GameArtCache.UrlFor).Distinct().Count());
        Assert.Equal(art.Length, art.Select(cache.PathFor).Distinct().Count());
        Assert.All(art, piece => Assert.StartsWith("https://", GameArtCache.UrlFor(piece)));
    }

    [Fact]
    public void OnlyAWholePngCounts() {
        using var workspace = new TempWorkspace();

        var good = workspace.WriteFile("good.png", Png("a whole image"));
        var empty = workspace.WriteFile("empty.png", []);
        var html = workspace.WriteFile("html.png", "<html>nope</html>"u8.ToArray());
        var truncated = workspace.WriteFile("truncated.png", Png("half").AsSpan(0, 40).ToArray());

        Assert.True(GameArtCache.IsUsablePng(good));
        Assert.False(GameArtCache.IsUsablePng(empty));
        Assert.False(GameArtCache.IsUsablePng(html));
        Assert.False(GameArtCache.IsUsablePng(truncated));
        Assert.False(GameArtCache.IsUsablePng(workspace.Path("missing.png")));
    }

    /// <summary>
    /// Bytes shaped like a real PNG: the signature, a body of the given length, and the end marker. The
    /// cache judges a file by its two ends, which is what these exercise.
    /// </summary>
    private static byte[] Png(string body) {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(body.PadRight(64, '.')));
        bytes.AddRange([0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }

    private static HttpResponseMessage Ok(byte[] payload)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    /// <summary>A client that fails the test if it is used at all.</summary>
    private static HttpClient ThrowingClient()
        => new(new FakeHttpHandler(_ => throw new InvalidOperationException(
            "The network must not be touched when a usable copy is already cached.")));
}
