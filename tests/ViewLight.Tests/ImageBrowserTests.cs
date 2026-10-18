using System.Net;
using System.Text;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The browser a profile picture or a banner is chosen in: a page of pictures at a time, a search, and the
/// bytes of the one that was settled on.
/// </summary>
/// <remarks>
/// The archive holds around 32,500 pictures, so what is checked here is the part that decides whether the
/// window is usable: that a page is a page rather than everything, that a search is a filter over what was
/// already read rather than a walk of the repository, and that the picture taken out of the window is the
/// one that was clicked - as bytes, because that is what the launcher keeps.
/// </remarks>
public class ImageBrowserTests {

    /// <summary>
    /// A listing with exactly the number of entries asked for: numbered ones, and one whose name a search
    /// can find and the numbers cannot.
    /// </summary>
    private static string Index(int entries) {
        var text = new StringBuilder("title_id,title_name,image_url,category,local_path\n");

        for (var n = 0; n < entries - 1; n++) {
            text.Append($"4e4d08{n % 16:x2},Keflings {n},https://example/{n}.png,Games,Games\\Keflings\\{n}.png\n");
        }

        text.Append("5841083c,Aegis Wing,https://example/aw.png,Games,Games\\Aegis Wing\\aw.png\n");

        return text.ToString();
    }

    /// <summary>How many times the archive's index was asked for, which is once per browser and no more.</summary>
    private static int IndexReads(FakeHttpHandler handler)
        => handler.Requests.Count(url => url.EndsWith(".csv"));

    /// <summary>How many times one picture was asked for.</summary>
    private static int PictureReads(FakeHttpHandler handler, string fragment)
        => handler.Requests.Count(url => Uri.UnescapeDataString(url).Contains(fragment));

    [Fact]
    public async Task TheArchiveIsShownAPageAtATime() {
        using var workspace = new TempWorkspace();
        var (browser, _) = Gamerpics(workspace, entries: 300);

        await browser.StartAsync();

        Assert.Equal(ImageBrowserViewModel.GamerpicsPerPage, browser.Items.Count);
        Assert.True(browser.HasItems);

        // Three hundred of them, a page at a time: 120, 120, and the last 60.
        Assert.Equal(3, browser.LastPage);
        Assert.True(browser.CanGoForward);
        Assert.False(browser.CanGoBack);
        Assert.Contains("300 gamerpics", browser.Status);

        browser.NextPageCommand.Execute(null);
        await WaitFor(() => browser.Page == 2);

        Assert.Equal(2, browser.Page);
        Assert.True(browser.CanGoBack);

        browser.PreviousPageCommand.Execute(null);
        await WaitFor(() => browser.Page == 1);

        Assert.False(browser.CanGoBack);

        // The first page is the first of the archive, in the order the archive lists it.
        Assert.Equal("Keflings 0", browser.Items[0].Caption);
        Assert.Equal("Games", browser.Items[0].Detail);
    }

    [Fact]
    public async Task ASearchFiltersWhatWasAlreadyRead() {
        using var workspace = new TempWorkspace();
        var (browser, handler) = Gamerpics(workspace, entries: 300);

        await browser.StartAsync();

        browser.Search = "aegis";

        browser.SearchCommand.Execute(null);
        await WaitFor(() => browser.Items.Count == 1);

        Assert.Equal("Aegis Wing", browser.Items[0].Caption);

        // The archive was read once for the whole session: a search is a filter over what is already in
        // memory, not another walk of a repository that holds thirty-two thousand pictures.
        Assert.Equal(1, IndexReads(handler));

        // And a search that finds nothing says so rather than showing an empty grid.
        browser.Search = "no such game";

        browser.SearchCommand.Execute(null);
        await WaitFor(() => !browser.HasItems);

        Assert.Contains("Nothing in the archive matches that", browser.Status);

        // The search also set the page back to the first, so the results are not on a page that no longer
        // exists.
        Assert.Equal(1, browser.Page);
    }

    [Fact]
    public async Task TheChosenPictureComesBackAsBytes() {
        using var workspace = new TempWorkspace();
        var picture = Png("a gamerpic");

        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".csv")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index(4)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) });

        var browser = new ImageBrowserViewModel(
            ImageBrowserSource.Gamerpics,
            new GamerpicArchive(new HttpClient(handler), workspace.Path("art/gamerpics.csv"),
                workspace.CreateDirectory("art/gamerpics")));

        await browser.StartAsync();

        // Nothing selected is nothing to take, and the window's own button says so.
        Assert.False(browser.CanChoose);
        Assert.Null(await browser.TakeSelectedAsync());

        browser.Selected = browser.Items[2];
        Assert.True(browser.CanChoose);

        var chosen = await browser.TakeSelectedAsync();

        Assert.NotNull(chosen);
        Assert.Equal(picture, chosen!.Bytes);

        // Kept under a name that says what it is, from the address it came from - the picture is written into
        // the profile's own document, and a name with no type in it is a picture nothing can read back.
        Assert.Equal(".png", chosen.Extension);
        Assert.Contains("Keflings 2", chosen.Description);
    }

    [Fact]
    public async Task PicturesAreFetchedOncePerSessionAndKept() {
        using var workspace = new TempWorkspace();
        var picture = Png("a gamerpic");

        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".csv")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index(2)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) });

        var browser = new ImageBrowserViewModel(
            ImageBrowserSource.Gamerpics,
            new GamerpicArchive(new HttpClient(handler), workspace.Path("art/gamerpics.csv"),
                workspace.CreateDirectory("art/gamerpics")));

        await browser.StartAsync();

        browser.Selected = browser.Items[0];

        await browser.TakeSelectedAsync();
        await browser.TakeSelectedAsync();

        // A second look at the same picture is off the disk, and so is the grid's own copy of it: one
        // request for it, whatever asked for it and however many times.
        Assert.Equal(1, PictureReads(handler, "/Keflings/0.png"));
    }

    [Fact]
    public async Task AnArchiveThatCannotBeReadIsSaidRatherThanShownAsAnEmptyGrid() {
        using var workspace = new TempWorkspace();

        var browser = new ImageBrowserViewModel(
            ImageBrowserSource.Gamerpics,
            new GamerpicArchive(
                FakeHttpHandler.Returning(HttpStatusCode.ServiceUnavailable),
                workspace.Path("art/gamerpics.csv"),
                workspace.Path("art/gamerpics")));

        await browser.StartAsync();

        Assert.False(browser.HasItems);
        Assert.True(browser.IsStatusProblem);
        Assert.Contains("connection", browser.Status);
    }

    [Fact]
    public async Task TheWallpaperBrowserAsksForTheToplistAndTakesTheWholeWallpaper() {
        using var workspace = new TempWorkspace();
        var wallpaper = Jpeg("a wallpaper");

        // The API is the only thing under /api/: the thumbnails and the wallpaper itself are pictures.
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Listing) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wallpaper) });

        var browser = new ImageBrowserViewModel(
            ImageBrowserSource.Wallpapers,
            wallhaven: new WallhavenClient(new HttpClient(handler), workspace.CreateDirectory("art/wallhaven")));

        await browser.StartAsync();

        Assert.Single(browser.Items);
        Assert.Contains("2074x1166", browser.Items[0].Caption);
        Assert.Contains("topRange=1w", handler.Requests[0]);

        browser.Selected = browser.Items[0];
        var chosen = await browser.TakeSelectedAsync();

        Assert.NotNull(chosen);
        Assert.Equal(wallpaper, chosen!.Bytes);

        // The wallpaper, not the thumbnail: a banner is the whole point of choosing one.
        Assert.Equal(".jpg", chosen.Extension);
        Assert.EndsWith("wallhaven-yqkl97.jpg", handler.Requests[^1]);
    }

    [Fact]
    public async Task AListingThatFailsIsSaidRatherThanShownAsAnEmptyGrid() {
        using var workspace = new TempWorkspace();

        var browser = new ImageBrowserViewModel(
            ImageBrowserSource.Wallpapers,
            wallhaven: new WallhavenClient(FakeHttpHandler.Returning(HttpStatusCode.Forbidden)));

        await browser.StartAsync();

        Assert.False(browser.HasItems);
        Assert.True(browser.IsStatusProblem);
        Assert.Contains("refused", browser.Status);
    }

    /// <summary>A browser over a scratch archive of the given size, with the handler it was built on.</summary>
    private static (ImageBrowserViewModel Browser, FakeHttpHandler Handler) Gamerpics(
        TempWorkspace workspace, int entries) {

        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".csv")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index(entries)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png("a gamerpic")) });

        var archive = new GamerpicArchive(new HttpClient(handler), workspace.Path("art/gamerpics.csv"),
            workspace.CreateDirectory("art/gamerpics"));

        return (new ImageBrowserViewModel(ImageBrowserSource.Gamerpics, archive), handler);
    }

    /// <summary>Waits for something the browser is doing in the background to have happened.</summary>
    private static async Task WaitFor(Func<bool> condition) {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the browser did not get there in two seconds");
    }

    private const string Listing = """
    {
      "data": [
        {
          "id": "yqkl97",
          "url": "https://wallhaven.cc/w/yqkl97",
          "resolution": "2074x1166",
          "file_size": 976307,
          "path": "https://w.wallhaven.cc/full/yq/wallhaven-yqkl97.jpg",
          "thumbs": { "large": "https://th.wallhaven.cc/lg/yq/yqkl97.jpg" }
        }
      ],
      "meta": { "current_page": 1, "last_page": 15, "per_page": 24, "total": 338 }
    }
    """;

    /// <summary>Bytes shaped like a whole PNG, which is what the cache judges a file by.</summary>
    private static byte[] Png(string body) {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(Encoding.ASCII.GetBytes(body.PadRight(64, '.')));
        bytes.AddRange([0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }

    /// <summary>Bytes shaped like a whole JPEG, which is the other thing wallhaven serves.</summary>
    private static byte[] Jpeg(string body) {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF };
        bytes.AddRange(Encoding.ASCII.GetBytes(body.PadRight(64, '.')));
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }
}
