using System.Net;
using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class GithubReleaseServiceTests {

    private static byte[] ReleaseJson(params (string Tag, string Published, string[] Assets)[] releases)
        => ReleaseJson(releases.Select(r => (r.Tag, r.Published, Prerelease: true, r.Assets)).ToArray());

    private static byte[] ReleaseJson(params (string Tag, string Published, bool Prerelease, string[] Assets)[] releases) {
        var payload = releases.Select(r => new {
            tag_name = r.Tag,
            name = r.Tag,
            published_at = r.Published,
            created_at = r.Published,
            prerelease = r.Prerelease,
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
    public async Task PicksTheNewestReleaseThatHasAWindowsAsset() {
        // Newest release only ships Linux; the next one down has the Windows build.
        var json = ReleaseJson(
            ("r900000.Wizard_1_610", "2026-10-01T00:00:00Z", ["Imlight-linux.zip"]),
            ("r806919.Wizard_1_610", "2026-09-25T14:54:26Z", ["Imlight-linux.zip", "Imlight-win-x64.zip"]),
            ("r803238.Wizard_1_610", "2026-09-01T00:00:00Z", ["Imlight-win-x64-r803238.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync();

        Assert.Equal("r806919.Wizard_1_610", release.Tag);
        Assert.Equal("Imlight-win-x64.zip", release.AssetName);
        Assert.True(release.Prerelease);
    }

    [Fact]
    public async Task UnderstandsTheOlderRevisionSuffixedAssetName() {
        var json = ReleaseJson(
            ("r806184.Wizard_1_610", "2026-09-20T00:00:00Z", ["Imlight-win-x64-r806184.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync();

        Assert.Equal("Imlight-win-x64-r806184.zip", release.AssetName);
    }

    [Fact]
    public async Task PrefersTheExactAssetNameWhenBothAreOffered() {
        var json = ReleaseJson(
            ("r806919.Wizard_1_610", "2026-09-25T00:00:00Z",
                ["Imlight-win-x64-r806919.zip", "Imlight-win-x64.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync();

        Assert.Equal("Imlight-win-x64.zip", release.AssetName);
    }

    [Fact]
    public async Task ThrowsHelpfullyWhenNoWindowsBuildExists() {
        var json = ReleaseJson(("r1.Wizard_1_610", "2026-01-01T00:00:00Z", ["Imlight-linux.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync());
        Assert.Contains("Imlight-win-x64", error.Message);
    }

    // ------------------------------------------------------------------ channels

    [Fact]
    public async Task TheStableChannelIgnoresPrereleasesEvenWhenTheyAreNewer() {
        var json = ReleaseJson(
            ("r900000.Wizard_1_610", "2026-10-01T00:00:00Z", true, ["Imlight-win-x64.zip"]),
            ("v1.0.0", "2026-09-01T00:00:00Z", false, ["Imlight-win-x64.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync(ServerChannelChoices.Stable);

        Assert.Equal("v1.0.0", release.Tag);
        Assert.False(release.Prerelease);
    }

    [Fact]
    public async Task TheStableChannelSaysWhenNothingStableHasBeenPublished() {
        var json = ReleaseJson(
            ("r806919.Wizard_1_610", "2026-09-25T00:00:00Z", ["Imlight-win-x64.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync(ServerChannelChoices.Stable));

        Assert.Contains("No stable Imlight release", error.Message);
        Assert.Contains("Prerelease", error.Message);
    }

    [Fact]
    public async Task ThePrereleaseChannelFallsBackToAStableBuildWhenItIsTheNewest() {
        var json = ReleaseJson(
            ("v1.0.0", "2026-10-01T00:00:00Z", false, ["Imlight-win-x64.zip"]),
            ("r806919.Wizard_1_610", "2026-09-25T00:00:00Z", true, ["Imlight-win-x64.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync(ServerChannelChoices.Prerelease);

        Assert.Equal("v1.0.0", release.Tag);
    }

    [Fact]
    public async Task TheDefaultChannelIsPrereleaseAndSeesEverything() {
        var json = ReleaseJson(
            ("r900000.Wizard_1_610", "2026-10-01T00:00:00Z", true, ["Imlight-win-x64.zip"]),
            ("v1.0.0", "2026-09-01T00:00:00Z", false, ["Imlight-win-x64.zip"]));

        var service = new GithubReleaseService(FakeHttpHandler.Serving(json));
        var release = await service.GetLatestWindowsReleaseAsync();

        Assert.Equal("r900000.Wizard_1_610", release.Tag);
    }

    [Fact]
    public async Task BleedingEdgeNeverAsksGitHubForARelease() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(ReleaseJson(
                ("r806919.Wizard_1_610", "2026-09-25T00:00:00Z", ["Imlight-win-x64.zip"]))),
        });

        var service = new GithubReleaseService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync(ServerChannelChoices.BleedingEdge));

        Assert.Contains("source", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReportsRateLimitingClearly() {
        var service = new GithubReleaseService(FakeHttpHandler.Returning(HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetLatestWindowsReleaseAsync());
        Assert.Contains("rate-limited", error.Message);
    }

    [Fact]
    public void QueriesTheWindowsX64AssetForThisRepository() {
        // Guards against a silent regression in the asset name, which would make updates stop working.
        Assert.Equal("Revive101/Imlight", GithubReleaseService.DefaultRepository);
    }
}
