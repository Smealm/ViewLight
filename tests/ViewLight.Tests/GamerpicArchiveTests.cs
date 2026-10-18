using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The gamerpic archive: its own index of what it holds, the paths inside it, and what happens when the
/// index cannot be read.
/// </summary>
/// <remarks>
/// Two things about this file are load-bearing. The archive's index points every picture at a host that no
/// longer resolves, so the picture has to be fetched from the repository instead - the file's own path,
/// exactly as the index spells it, however ugly the folder names in it are. And the index is one file of
/// thirty-two thousand lines, so it is read once and searched in memory: a browser that asked the archive
/// for a page at a time would be a request per row.
/// </remarks>
public class GamerpicArchiveTests {

    /// <summary>The index's own header, and two rows: one plain, one with a comma in the title.</summary>
    private const string Index =
        "title_id,title_name,image_url,category,local_path\n"
        + "4e4d081c,Ace Combat: Assault Horizon,https://download.xboxgamer.pics/titles/4e4d081c/20401.png,"
        + "Games,Games\\Ace Combat_ Assault Horizon\\20401.png\n"
        + "465607d4,\"11eyes CrossOver, Limited\",https://download.xboxgamer.pics/titles/465607d4/28016.png,"
        + "Games,Games\\11eyes CrossOver\\28016.png\n";

    [Fact]
    public void TheIndexIsReadIntoWhatTheBrowserShows() {
        var entries = GamerpicArchive.Parse(Index);

        Assert.Equal(2, entries.Count);

        // The header is not an entry, and the title is the index's own - not the folder, which the site it
        // was backed up from mangled into "Ace Combat_ Assault Horizon".
        Assert.Equal("Ace Combat: Assault Horizon", entries[0].Title);
        Assert.Equal("4e4d081c", entries[0].TitleId);
        Assert.Equal("Games", entries[0].Category);

        // A comma inside a quoted title does not shift the fields after it.
        Assert.Equal("11eyes CrossOver, Limited", entries[1].Title);
        Assert.Equal(GamerpicArchive.UrlFor("Games\\11eyes CrossOver\\28016.png"), entries[1].Url);
    }

    [Fact]
    public void APictureComesFromTheRepositoryRatherThanTheHostTheIndexNames() {
        // The address the index records is dead - the host does not resolve - so what is used is the
        // repository's own copy of the same file, at the path the index gives.
        var url = GamerpicArchive.UrlFor("Games\\0D Beat Drop\\2000d.png");

        Assert.Equal(
            "https://raw.githubusercontent.com/Smealm/xbox-stuff/main/Games/0D%20Beat%20Drop/2000d.png",
            url);
    }

    [Fact]
    public void TheUglyFolderNamesAreUsedAsTheyAre() {
        // "Ace Combat_ Assault Horizon" is what the backup dumped; nothing invents a nicer path, because a
        // nicer path is a file that is not there.
        var url = GamerpicArchive.UrlFor("Games\\Ace Combat_ Assault Horizon\\20401.png");

        Assert.EndsWith("/Games/Ace%20Combat_%20Assault%20Horizon/20401.png", url);

        // The colon the title really has is an underscore in the repository, and the only colon left in the
        // address is the one in its scheme.
        Assert.Equal(1, url.Count(character => character == ':'));
        Assert.DoesNotContain("Combat:", url);
    }

    [Fact]
    public void EntriesWithoutAPathAreSkippedRatherThanShownAsBlankCells() {
        var entries = GamerpicArchive.Parse(
            "title_id,title_name,image_url,category,local_path\n"
            + "4e4d081c,Ace Combat,,Games,\n"
            + "465607d4,11eyes,https://example/1.png,Games,Games\\11eyes\\1.png\n");

        Assert.Single(entries);
        Assert.Equal("11eyes", entries[0].Title);
    }

    [Fact]
    public async Task TheIndexIsFetchedOnceAndKeptBesideTheLaunchersOwnArt() {
        using var workspace = new TempWorkspace();
        var indexPath = workspace.Path("art/gamerpics.csv");

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(Index),
        });

        var first = new GamerpicArchive(new HttpClient(handler), indexPath, workspace.Path("art/gamerpics"));
        Assert.Equal(2, (await first.LoadAsync()).Count);
        Assert.Single(handler.Requests);
        Assert.True(File.Exists(indexPath));

        // The next run, with no network at all: the index is read off disk and nothing is asked for.
        var second = new GamerpicArchive(
            new HttpClient(new FakeHttpHandler(
                _ => throw new InvalidOperationException("the network must not be touched"))),
            indexPath,
            workspace.Path("art/gamerpics"));

        Assert.Equal(2, (await second.LoadAsync()).Count);
        Assert.Null(second.Problem);
    }

    [Fact]
    public async Task AnIndexThatCannotBeReadSaysSoRatherThanLookingEmpty() {
        using var workspace = new TempWorkspace();        var archive = new GamerpicArchive(
            FakeHttpHandler.Returning(HttpStatusCode.ServiceUnavailable),
            workspace.Path("art/gamerpics.csv"),
            workspace.Path("art/gamerpics"));


        Assert.Empty(await archive.LoadAsync());
        Assert.NotNull(archive.Problem);
        Assert.Contains("connection", archive.Problem!);
    }

    [Fact]
    public async Task APictureIsFetchedOnceAndKept() {
        using var workspace = new TempWorkspace();
        var picture = Png("a gamerpic");

        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".csv")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) });

        var archive = new GamerpicArchive(new HttpClient(handler), workspace.Path("art/gamerpics.csv"),
            workspace.CreateDirectory("art/gamerpics"));

        var entry = (await archive.LoadAsync())[0];

        var first = await archive.PictureAsync(entry);
        var second = await archive.PictureAsync(entry);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(picture, await File.ReadAllBytesAsync(first!));

        // The index, and one picture - not the same picture twice.
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>Bytes shaped like a whole PNG, which is what the cache judges a file by.</summary>
    private static byte[] Png(string body) {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(body.PadRight(64, '.')));
        bytes.AddRange([0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }
}
