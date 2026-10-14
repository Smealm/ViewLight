using System.Net;
using System.Text.Json;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The patch server is downloaded from a release page, so which asset counts as the Windows build is
/// what decides whether the download works at all.
/// </summary>
public class PatchServerReleaseServiceTests {

    private static byte[] ReleaseJson(params (string Tag, string Published, string[] Assets)[] releases) {
        var payload = releases.Select(r => new {
            tag_name = r.Tag,
            name = r.Tag,
            published_at = r.Published,
            created_at = r.Published,
            prerelease = false,
            draft = false,
            assets = r.Assets.Select(a => new {
                name = a,
                browser_download_url = $"https://example.test/{r.Tag}/{a}",
                size = 1234,
            }).ToArray(),
        });

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    [Fact]
    public async Task PicksTheNewestReleaseThatShipsTheWindowsBuild() {
        // Newest release only ships the Linux build; the one below it has both.
        var json = ReleaseJson(
            ("v4.1.0", "2026-11-01T00:00:00Z", ["aurorium"]),
            ("v4.0.2", "2026-09-29T00:46:45Z", ["aurorium", "aurorium.exe"]),
            ("v4.0.1", "2026-07-27T13:22:10Z", ["aurorium.exe"]));

        var service = new PatchServerReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync();

        Assert.Equal("v4.0.2", release.Tag);
        Assert.Equal("aurorium.exe", release.AssetName);
        Assert.Contains("v4.0.2", release.AssetUrl);
    }

    [Fact]
    public async Task ThrowsHelpfullyWhenNoWindowsBuildExists() {
        var json = ReleaseJson(("v4.0.2", "2026-09-29T00:00:00Z", ["aurorium"]));

        var service = new PatchServerReleaseService(FakeHttpHandler.Serving(json));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync());
        Assert.Contains("aurorium.exe", error.Message);
    }

    [Fact]
    public async Task ReportsRateLimitingClearly() {
        var service = new PatchServerReleaseService(FakeHttpHandler.Returning(HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync());
        Assert.Contains("rate-limited", error.Message);
    }

    [Fact]
    public async Task AsksTheRevive101RepositoryForItsReleases() {
        var payload = ReleaseJson(("v4.0.2", "2026-09-29T00:00:00Z", ["aurorium.exe"]));
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(payload),
        });

        var service = new PatchServerReleaseService(new HttpClient(handler));
        await service.GetLatestWindowsReleaseAsync();

        Assert.Contains("api.github.com/repos/Revive101/Aurorium/releases", Assert.Single(handler.Requests));
    }

    [Fact]
    public void TheWindowsAssetIsTheExecutableAuroriumPublishes() {
        Assert.Equal("Revive101/Aurorium", PatchServerReleaseService.DefaultRepository);
        Assert.Equal("aurorium.exe", PatchServerReleaseService.WindowsAssetName);
    }
}
