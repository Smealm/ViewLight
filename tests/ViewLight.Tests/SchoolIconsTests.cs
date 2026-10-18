using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The game's own school element logos: the seven a wizard can be, where each one comes from, and what
/// happens when one cannot be fetched.
/// </summary>
/// <remarks>
/// The addresses are pinned down here rather than left to whatever the code happens to build, because the
/// whole point of the feature is that these are KingsIsle's own symbols: a fetch that quietly started
/// returning somebody else's artwork, or the wiki's WebP instead of the PNG it was uploaded as, would be a
/// wizard card wearing the wrong school's logo - which is worse than no logo at all.
/// </remarks>
public class SchoolIconsTests {

    [Fact]
    public void EverySchoolAWizardCanBeHasItsOwnLogo() {
        Assert.Equal(WizardSchool.Names, SchoolIcons.Names);
        Assert.Equal(7, SchoolIcons.Names.Count);

        var urls = SchoolIcons.Names.Select(SchoolIcons.UrlFor).ToList();

        Assert.All(urls, url => Assert.NotNull(url));
        Assert.Equal(urls.Count, urls.Distinct().Count());
    }

    [Fact]
    public void EachLogoIsTheWikisOwnFileAskedForAsItsOriginalPng() {
        Assert.Equal(
            "https://static.wikia.nocookie.net/wizard101/images/e/e5/FireLogo.png/revision/latest?format=original",
            SchoolIcons.UrlFor("Fire"));

        Assert.Equal(
            "https://static.wikia.nocookie.net/wizard101/images/4/4f/BalanceLogo.png/revision/latest?format=original",
            SchoolIcons.UrlFor("Balance"));

        foreach (var school in SchoolIcons.Names) {
            var url = SchoolIcons.UrlFor(school)!;

            Assert.StartsWith("https://static.wikia.nocookie.net/wizard101/images/", url);

            // The school's logo - the emblem its own page is headed with - rather than the 25×25 marker the
            // wiki puts in its tables, which is a smudge at the size a wizard card draws it.
            Assert.Contains($"/{school}Logo.png/revision/latest", url);

            // Without this the CDN negotiates WebP, which is not what the wiki's file is.
            Assert.EndsWith("?format=original", url);
        }
    }

    [Fact]
    public void TheAddressIsTheSchoolsOwnWhateverItsCaseAndNothingForAnythingElse() {
        Assert.Equal(SchoolIcons.UrlFor("Storm"), SchoolIcons.UrlFor("  storm "));

        // An id, a creature's school, an empty field: none of them is one of the seven, so none of them is
        // given a logo - and none of them is given somebody else's.
        Assert.Null(SchoolIcons.UrlFor("83375795"));
        Assert.Null(SchoolIcons.UrlFor("Sun"));
        Assert.Null(SchoolIcons.UrlFor("Shadow"));
        Assert.Null(SchoolIcons.UrlFor(string.Empty));
        Assert.Null(SchoolIcons.UrlFor(null));
    }

    [Fact]
    public async Task ALlogoIsFetchedOnceAndReadOffDiskFromThenOn() {
        using var workspace = new TempWorkspace();

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(Png("fire symbol")),
        });

        var cache = new SchoolIconCache(new HttpClient(handler), workspace.CreateDirectory("schools"));

        await cache.LoadAsync("Fire");
        await cache.LoadAsync("Fire");

        // One request for two looks, and the file is where the next run would find it.
        Assert.Single(handler.Requests);
        Assert.StartsWith(SchoolIcons.UrlFor("Fire"), handler.Requests[0]);
        Assert.True(cache.IsCached("Fire"));
        Assert.True(GameArtCache.IsUsablePng(cache.PathFor("Fire")));
    }

    [Fact]
    public async Task ASchoolTheLauncherDoesNotKnowAsksTheNetworkForNothing() {
        using var workspace = new TempWorkspace();

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(Png("somebody else's art")),
        });

        var cache = new SchoolIconCache(new HttpClient(handler), workspace.CreateDirectory("schools"));

        Assert.Null(await cache.LoadAsync("Sun"));
        Assert.Null(await cache.LoadAsync(null));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFetchThatFailsIsAskedForOnceAndNotOncePerCard() {
        using var workspace = new TempWorkspace();

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) {
            Content = new StringContent("no"),
        });

        var cache = new SchoolIconCache(new HttpClient(handler), workspace.CreateDirectory("schools"));

        // Six wizards of one school on the profile, and one poll per ten seconds.
        Assert.Null(await cache.LoadAsync("Ice"));
        Assert.Null(await cache.LoadAsync("Ice"));
        Assert.Null(await cache.LoadAsync("Ice"));

        Assert.Single(handler.Requests);
        Assert.False(cache.IsCached("Ice"));
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImageIsNotKept() {
        using var workspace = new TempWorkspace();

        // What a captive portal answers with: 200 OK, and a login page.
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("<html><body>Sign in</body></html>"),
        });

        var cache = new SchoolIconCache(new HttpClient(handler), workspace.CreateDirectory("schools"));

        Assert.Null(await cache.LoadAsync("Myth"));
        Assert.False(cache.IsCached("Myth"));
        Assert.Empty(Directory.GetFiles(workspace.Path("schools")));
    }

    /// <summary>Bytes shaped like a whole PNG, which is what the cache judges a file by.</summary>
    private static byte[] Png(string body) {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(body.PadRight(64, '.')));
        bytes.AddRange([0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }
}
