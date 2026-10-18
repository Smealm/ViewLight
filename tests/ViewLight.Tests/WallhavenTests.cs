using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Wallhaven, asked for the one listing a banner is worth browsing - and what the screen says when it
/// cannot answer.
/// </summary>
/// <remarks>
/// The listing is the issue's own: the weekly toplist, the site's general and anime categories, SFW only. It
/// is pinned down here because it is the difference between a browser of a few hundred walls somebody can
/// actually choose from and a browser of millions - and because an empty grid and a refused request look
/// identical on screen, which is what the problem field exists to keep apart.
/// </remarks>
public class WallhavenTests {

    private const string Answer = """
    {
      "data": [
        {
          "id": "yqkl97",
          "url": "https://wallhaven.cc/w/yqkl97",
          "resolution": "2074x1166",
          "file_size": 976307,
          "file_type": "image/jpeg",
          "path": "https://w.wallhaven.cc/full/yq/wallhaven-yqkl97.jpg",
          "thumbs": { "large": "https://th.wallhaven.cc/lg/yq/yqkl97.jpg",
                      "original": "https://th.wallhaven.cc/orig/yq/yqkl97.jpg" }
        },
        {
          "id": "nothumb",
          "url": "https://wallhaven.cc/w/nothumb",
          "resolution": "1920x1080",
          "path": "https://w.wallhaven.cc/full/no/wallhaven-nothumb.png"
        },
        { "id": "nopath", "url": "https://wallhaven.cc/w/nopath" }
      ],
      "meta": { "current_page": 2, "last_page": 15, "per_page": 24, "total": 338 }
    }
    """;

    [Fact]
    public async Task TheListingIsTheWeeksToplistSfvOnly() {
        var (_, requests) = await ListingAsync(query: null);

        Assert.Single(requests);
        var url = requests[0];

        Assert.StartsWith("https://wallhaven.cc/api/v1/search?", url);
        Assert.Contains("categories=110", url);
        Assert.Contains("purity=100", url);
        Assert.Contains("topRange=1w", url);
        Assert.Contains("sorting=toplist", url);
        Assert.Contains("order=desc", url);
        Assert.Contains("page=1", url);
    }

    [Fact]
    public async Task SearchingAsksForTheThingAndKeepsTheSameFilters() {
        var (_, requests) = await ListingAsync("space station");
        var url = requests[0];

        // The request is written down as a display string, which unescapes what was asked for - so the
        // term is checked decoded, and what matters is that it arrived whole.
        Assert.Contains("q=", url);
        Assert.Contains("space station", Uri.UnescapeDataString(url));
        Assert.Contains("purity=100", url);
        Assert.Contains("categories=110", url);
        Assert.DoesNotContain("topRange", url);

        // An empty search is not a search: it is the toplist, unchanged.
        var (_, topList) = await ListingAsync("   ");

        Assert.Contains("topRange=1w", topList[0]);
    }

    [Fact]
    public async Task TheAnswerIsReadIntoWallsAndPages() {
        var page = await Client(Answer).ToplistAsync();

        Assert.Null(page.Problem);
        Assert.Equal(2, page.Page);
        Assert.Equal(15, page.LastPage);
        Assert.Equal(338, page.Total);

        // The entry without a path is not shown: a cell with nothing to fetch is a blank card.
        Assert.Equal(2, page.Walls.Count);

        var first = page.Walls[0];

        Assert.Equal("yqkl97", first.Id);
        Assert.Equal("https://th.wallhaven.cc/lg/yq/yqkl97.jpg", first.ThumbnailUrl);
        Assert.Equal("https://w.wallhaven.cc/full/yq/wallhaven-yqkl97.jpg", first.FullUrl);
        Assert.Equal("2074x1166", first.Resolution);
        Assert.Contains("0.9 MB", first.Caption);

        // No thumbnail is not an entry that cannot be shown: the wallpaper itself stands in for it.
        Assert.Equal(page.Walls[1].FullUrl, page.Walls[1].ThumbnailUrl);
    }

    [Fact]
    public async Task ARefusalIsSaidRatherThanShownAsAnEmptyGrid() {
        var refused = await new WallhavenClient(FakeHttpHandler.Returning(HttpStatusCode.Forbidden))
            .ToplistAsync();

        Assert.Empty(refused.Walls);
        Assert.NotNull(refused.Problem);
        Assert.Contains("refused", refused.Problem!);

        var rateLimited = await new WallhavenClient(FakeHttpHandler.Returning(HttpStatusCode.TooManyRequests))
            .ToplistAsync();

        Assert.Contains("rate limiting", rateLimited.Problem!);

        // And an answer that is not a listing at all - a captive portal, most likely - is a sentence too.
        var nonsense = await new WallhavenClient(FakeHttpHandler.Serving("not json"u8.ToArray())).ToplistAsync();

        Assert.NotNull(nonsense.Problem);
        Assert.Contains("could not be reached", nonsense.Problem!);
    }

    [Fact]
    public async Task AnEmptyPageIsAPageAndNotAProblem() {
        var page = await Client("""{"data":[],"meta":{"current_page":1,"last_page":1,"total":0}}""")
            .ToplistAsync(page: 3);

        Assert.Empty(page.Walls);

        // The listing says which page it answered with, and that is what the browser shows: a page number
        // taken from the request rather than the answer would make the buttons disagree with the grid.
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.LastPage);
        Assert.Null(page.Problem);

        // An answer with no meta at all falls back to what was asked for, so the grid is still pageable.
        var bare = await Client("""{"data":[]}""").ToplistAsync(page: 4);

        Assert.Equal(4, bare.Page);
        Assert.Equal(4, bare.LastPage);
    }

    /// <summary>
    /// Asks for a page - the toplist when there is nothing to search for, a search otherwise - and hands back
    /// the client with the requests it made.
    /// </summary>
    private static async Task<(WallhavenClient Client, List<string> Requests)> ListingAsync(string? query) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(Answer),
        });

        var client = new WallhavenClient(new HttpClient(handler));

        if (query is null) {
            await client.ToplistAsync();
        }
        else {
            await client.SearchAsync(query);
        }

        return (client, handler.Requests);
    }

    /// <summary>A client that answers every request with the given listing.</summary>
    private static WallhavenClient Client(string answer) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(answer),
        });

        return new WallhavenClient(new HttpClient(handler));
    }
}
