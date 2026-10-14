using System.Net;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Installing the patch server is one download and one config file, and the two things that matter are
/// that the executable lands where the launcher will look for it and that nothing is re-downloaded on a
/// second attempt.
/// </summary>
public class PatchServerInstallServiceTests {

    private static PatchServerRelease Release() => new() {
        Tag = "v4.0.2",
        Name = "4.0.2",
        AssetName = "aurorium.exe",
        AssetUrl = "https://example.test/v4.0.2/aurorium.exe",
        AssetSize = 4,
    };

    [Fact]
    public async Task WritesTheExecutableAndItsConfigIntoTheFolder() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        var service = new PatchServerInstallService(
            FakeHttpHandler.Serving([1, 2, 3, 4]));

        var result = await service.InstallAsync(Release(), folder, 12400);

        Assert.True(result.Success, result.Message);

        var installed = PatchServerRunner.ExecutablePathFor(folder);
        Assert.True(File.Exists(installed));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(installed));

        // The config the launcher wrote is the one the patch server will read, and it names the port the
        // view model is about to point Imlight at.
        Assert.Equal(12400, PatchServerConfig.TryReadPort(folder));
    }

    [Fact]
    public async Task ReusesTheCachedDownloadOnASecondAttempt() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent([9, 9, 9, 9]),
        });

        var service = new PatchServerInstallService(new HttpClient(handler));
        var release = Release();

        Assert.True((await service.InstallAsync(release, folder, 12369)).Success);
        Assert.True(service.HasCachedArchive(release));

        // A second install - an update retry, or a folder that was cleared - must not download again.
        Assert.True((await service.InstallAsync(release, folder, 12369)).Success);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefusesToInstallWhenThereIsNoFolder() {
        using var workspace = new TempWorkspace();

        var service = new PatchServerInstallService(FakeHttpHandler.Serving([1, 2, 3, 4]));
        var result = await service.InstallAsync(Release(), string.Empty, 12369);

        Assert.False(result.Success);
        Assert.Contains("folder", result.Message);
    }

    [Fact]
    public async Task ExplainsItselfWhenTheDownloadIsRefused() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        var service = new PatchServerInstallService(FakeHttpHandler.Returning(HttpStatusCode.NotFound));
        var result = await service.InstallAsync(Release(), folder, 12369);

        Assert.False(result.Success);
        Assert.Contains("404", result.Message);
        Assert.False(File.Exists(PatchServerRunner.ExecutablePathFor(folder)));
    }

    [Fact]
    public void DoesNotTakeAPartialDownloadForAGoodOne() {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(LauncherPaths.DownloadsDirectory);

        var service = new PatchServerInstallService(FakeHttpHandler.Serving([1, 2, 3, 4]));
        var release = Release();

        // A file that is not the size GitHub reported is an interrupted download, not a cached one.
        File.WriteAllBytes(service.GetCachedArchivePath(release), [1, 2]);
        Assert.False(service.HasCachedArchive(release));

        File.WriteAllBytes(service.GetCachedArchivePath(release), [1, 2, 3, 4]);
        Assert.True(service.HasCachedArchive(release));
    }
}
