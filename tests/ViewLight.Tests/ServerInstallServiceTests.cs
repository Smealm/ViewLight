using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Exercises the install pipeline end to end against real zip archives and a real filesystem, because
/// the interesting behaviour - what survives an update - only shows up when files are actually written.
/// </summary>
public class ServerInstallServiceTests {

    /// <summary>A trimmed stand-in for the config a release ships.</summary>
    private const string ReleaseConfig =
        "; Imlight configuration\r\n" +
        "[Global Settings]\r\n" +
        "GameRevision = r803238.Wizard_1_610\r\n" +
        "\r\n" +
        "[Character]\r\n" +
        "TutorialDisabled = false\r\n" +
        "; Starter kit template IDs, comma-separated.\r\n" +
        "DefaultItems = 1317127,1317128,87256\r\n" +
        "\r\n" +
        "[Database]\r\n" +
        "AdminAccountUsername = admin\r\n" +
        "AdminAccountPassword = compsci\r\n" +
        "EmbeddedDatabasePort = 8080\r\n";

    private static byte[] BuildReleaseZip(string marker, string config, string rootPrefix = "") {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            Add(zip, $"{rootPrefix}Imlight.Director.exe", $"exe:{marker}");
            Add(zip, $"{rootPrefix}Imcodec.Wad.dll", "dll");
            Add(zip, $"{rootPrefix}Config/Imlight.ini", config);
            Add(zip, $"{rootPrefix}Config/akka.conf", "akka");
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content) {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static ImlightRelease Release(string tag, byte[] zip) => new() {
        Tag = tag,
        Name = tag,
        AssetName = "Imlight-win-x64.zip",
        AssetUrl = $"https://example.test/{tag}/Imlight-win-x64.zip",
        AssetSize = zip.Length,
        Prerelease = true,
        PublishedAt = DateTimeOffset.UtcNow,
    };

    private static string ReadInstalledMarker(string serverPath)
        => File.ReadAllText(ImlightConfig.ExecutablePathFor(serverPath));

    [Fact]
    public async Task InstallsAFreshServer() {
        using var workspace = new TempWorkspace();
        var zip = BuildReleaseZip("v1", ReleaseConfig);
        var service = new ServerInstallService(FakeHttpHandler.Serving(zip));
        var serverPath = workspace.Path("server");

        var result = await service.InstallAsync(Release("r800000.Wizard_1_610", zip), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.SettingsRetained);
        Assert.Null(result.Warning);
        Assert.True(File.Exists(ImlightConfig.ExecutablePathFor(serverPath)));
        Assert.Contains("exe:v1", ReadInstalledMarker(serverPath));
        Assert.True(File.Exists(ImlightConfig.ConfigPathFor(serverPath)));
        Assert.True(File.Exists(Path.Combine(serverPath, "Config", "akka.conf")));
    }

    [Fact]
    public async Task UpdatingKeepsUserSettingsAndRuntimeData() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.Path("server");

        // ---------- 1. install the first build.
        var v1 = BuildReleaseZip("v1", ReleaseConfig);
        var installed = await new ServerInstallService(FakeHttpHandler.Serving(v1))
            .InstallAsync(Release("r800000.Wizard_1_610", v1), serverPath);
        Assert.True(installed.Success, installed.Message);

        // ---------- 2. the user configures the server, and the server writes runtime data.
        var config = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(config);
        config.AdminUsername = "proprietor";
        config.AdminPassword = "s3cret-pass";
        config.TutorialDisabled = true;
        config.DefaultItems = "87256,126983";
        config.Ini.SetCommentAbove(
            ImlightConfig.SectionCharacter, "DefaultItems", ["Starter kit template IDs."]);
        config.Save(serverPath);

        workspace.WriteFile(Path.Combine("server", "logs", "log.txt"), "previous session");
        workspace.WriteFile(Path.Combine("server", "spiraldb-cache", "zone.json"), "{}");
        workspace.WriteFile(Path.Combine("server", "cache", "Root.wad"), "cached wad");
        workspace.WriteFile(Path.Combine("server", "ImlightEmbeddedDatabase", "playerdata"), "accounts");

        // ---------- 3. a newer build arrives with different defaults and a brand new setting.
        var v2Config = ReleaseConfig
            .Replace("DefaultItems = 1317127,1317128,87256", "DefaultItems = 1317127,1317128,87256,284071")
            .Replace("AdminAccountPassword = compsci",
                "AdminAccountPassword = compsci\r\nBrandNewSetting = fromV2");
        var v2 = BuildReleaseZip("v2", v2Config);

        var result = await new ServerInstallService(FakeHttpHandler.Serving(v2))
            .InstallAsync(Release("r810000.Wizard_1_610", v2), serverPath);

        // ---------- binaries were replaced.
        Assert.True(result.Success, result.Message);
        Assert.Contains("exe:v2", ReadInstalledMarker(serverPath));

        // ---------- the user's settings won.
        var updated = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(updated);
        Assert.Equal("proprietor", updated.AdminUsername);
        Assert.Equal("s3cret-pass", updated.AdminPassword);
        Assert.True(updated.TutorialDisabled);
        Assert.Equal([87256u, 126983u], updated.DefaultItemsIds);
        Assert.True(result.SettingsRetained >= 5);

        // ---------- the new release's own default still lands where the user had no opinion.
        Assert.Equal("fromV2", updated.Ini.Get("Database", "BrandNewSetting"));

        // ---------- runtime state the server owns was left alone.
        Assert.True(File.Exists(workspace.Path(Path.Combine("server", "logs", "log.txt"))));
        Assert.True(File.Exists(workspace.Path(Path.Combine("server", "spiraldb-cache", "zone.json"))));
        Assert.True(File.Exists(workspace.Path(Path.Combine("server", "cache", "Root.wad"))));
        Assert.True(File.Exists(workspace.Path(Path.Combine("server", "ImlightEmbeddedDatabase", "playerdata"))));

        // ---------- and the previous config was kept as a fallback.
        Assert.True(File.Exists(ImlightConfig.ConfigPathFor(serverPath) + ".bak"));
        Assert.Contains("s3cret-pass", File.ReadAllText(ImlightConfig.ConfigPathFor(serverPath) + ".bak"));
    }

    [Fact]
    public async Task UnwrapsArchivesThatNestEverythingInOneFolder() {
        using var workspace = new TempWorkspace();
        // Some earlier releases wrapped the build in a top-level folder rather than being flat.
        var zip = BuildReleaseZip("v1", ReleaseConfig, rootPrefix: "Imlight-win-x64/");
        var serverPath = workspace.Path("server");

        var result = await new ServerInstallService(FakeHttpHandler.Serving(zip))
            .InstallAsync(Release("r800000.Wizard_1_610", zip), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(ImlightConfig.ExecutablePathFor(serverPath)));
        Assert.True(File.Exists(ImlightConfig.ConfigPathFor(serverPath)));
    }

    [Fact]
    public async Task DoesNotDownloadAgainWhenTheArchiveIsAlreadyCached() {
        using var workspace = new TempWorkspace();
        var zip = BuildReleaseZip("v1", ReleaseConfig);
        var release = Release("r800000.Wizard_1_610", zip);

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(zip),
        });
        var service = new ServerInstallService(new HttpClient(handler));

        Assert.True((await service.InstallAsync(release, workspace.Path("server"))).Success);
        var requestsAfterFirstInstall = handler.Requests.Count;
        Assert.Equal(1, requestsAfterFirstInstall);

        Assert.True((await service.InstallAsync(release, workspace.Path("server"))).Success);
        Assert.Equal(requestsAfterFirstInstall, handler.Requests.Count);
    }

    [Fact]
    public void HasCachedArchive_RequiresACompleteFile() {
        using var workspace = new TempWorkspace();
        var zip = BuildReleaseZip("v1", ReleaseConfig);
        var service = new ServerInstallService(FakeHttpHandler.Serving(zip));
        var release = Release("r800000.Wizard_1_610", zip);

        Assert.False(service.HasCachedArchive(release));

        var path = service.GetCachedArchivePath(release);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, zip);
        Assert.True(service.HasCachedArchive(release));

        // A truncated file - e.g. an interrupted download - must not be trusted.
        File.WriteAllBytes(path, zip[..(zip.Length / 2)]);
        Assert.False(service.HasCachedArchive(release));
    }

    [Fact]
    public async Task RefusesAnArchiveThatEscapesTheInstallFolder() {
        using var workspace = new TempWorkspace();

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            Add(zip, "Imlight.Director.exe", "exe");
            Add(zip, "../escaped.txt", "this must never be written");
        }

        var bytes = stream.ToArray();
        var serverPath = workspace.Path("server");

        var result = await new ServerInstallService(FakeHttpHandler.Serving(bytes))
            .InstallAsync(Release("r800000.Wizard_1_610", bytes), serverPath);

        Assert.False(result.Success);
        Assert.Contains("outside the extraction folder", result.Message);
        Assert.False(File.Exists(workspace.Path("escaped.txt")));
    }

    [Fact]
    public async Task WarnsWhenTheArchiveHasNoServerExecutable() {
        using var workspace = new TempWorkspace();

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            Add(zip, "README.txt", "no binaries here");
        }

        var bytes = stream.ToArray();
        var serverPath = workspace.Path("server");

        var result = await new ServerInstallService(FakeHttpHandler.Serving(bytes))
            .InstallAsync(Release("r800000.Wizard_1_610", bytes), serverPath);

        Assert.True(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains(ImlightConfig.ServerExecutableName, result.Warning);
    }

    [Fact]
    public async Task ReportsAnHttpFailureInsteadOfThrowing() {
        using var workspace = new TempWorkspace();
        var service = new ServerInstallService(FakeHttpHandler.Returning(HttpStatusCode.NotFound));
        var release = Release("r800000.Wizard_1_610", [1, 2, 3]);

        var result = await service.InstallAsync(release, workspace.Path("server"));

        Assert.False(result.Success);
        Assert.Contains("Install failed", result.Message);
    }

    // ------------------------------------------------------------------ embedded RavenDB port

    [Fact]
    public async Task MovesTheEmbeddedDatabaseOffTheStockPortOnInstall() {
        using var workspace = new TempWorkspace();
        var zip = BuildReleaseZip("v1", ReleaseConfig);
        var serverPath = workspace.Path("server");

        var result = await new ServerInstallService(FakeHttpHandler.Serving(zip))
            .InstallAsync(Release("r800000.Wizard_1_610", zip), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.EmbeddedDatabasePort);

        // 8080 is heavily contested and Imlight dies on boot when something else holds it.
        var installed = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(installed);
        Assert.NotEqual(ImlightConfig.DefaultEmbeddedDatabasePort, installed.EmbeddedDatabasePort);
        Assert.InRange(installed.EmbeddedDatabasePort, PortAllocator.MinimumPort, PortAllocator.MaximumPort);
        Assert.True(PortAllocator.IsAvailable(installed.EmbeddedDatabasePort));
    }

    [Fact]
    public async Task KeepsAUserChosenEmbeddedDatabasePortWhenItIsFree() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.Path("server");

        var v1 = BuildReleaseZip("v1", ReleaseConfig);
        Assert.True((await new ServerInstallService(FakeHttpHandler.Serving(v1))
            .InstallAsync(Release("r800000.Wizard_1_610", v1), serverPath)).Success);

        var chosen = PortAllocator.FindFreePort()!.Value;
        var config = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(config);
        config.EmbeddedDatabasePort = chosen;
        config.Save(serverPath);

        var v2 = BuildReleaseZip("v2", ReleaseConfig);
        var result = await new ServerInstallService(FakeHttpHandler.Serving(v2))
            .InstallAsync(Release("r810000.Wizard_1_610", v2), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.EmbeddedDatabasePort);
        Assert.Equal(chosen, ImlightConfig.TryLoad(serverPath)!.EmbeddedDatabasePort);
    }

    [Fact]
    public async Task ReplacesAnEmbeddedDatabasePortThatIsAlreadyTaken() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.Path("server");

        var v1 = BuildReleaseZip("v1", ReleaseConfig);
        Assert.True((await new ServerInstallService(FakeHttpHandler.Serving(v1))
            .InstallAsync(Release("r800000.Wizard_1_610", v1), serverPath)).Success);

        // Something else gets to a port first; Imlight must not be left pointed at it.
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var config = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(config);
        config.EmbeddedDatabasePort = taken;
        config.Save(serverPath);

        var v2 = BuildReleaseZip("v2", ReleaseConfig);
        var result = await new ServerInstallService(FakeHttpHandler.Serving(v2))
            .InstallAsync(Release("r810000.Wizard_1_610", v2), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.EmbeddedDatabasePort);
        Assert.NotEqual(taken, ImlightConfig.TryLoad(serverPath)!.EmbeddedDatabasePort);
    }

    // ------------------------------------------------------------------ patch server

    [Fact]
    public async Task PointsTheInstalledServerAtAPatchServerThatWorks() {
        using var workspace = new TempWorkspace();
        var zip = BuildReleaseZip("v1", ReleaseConfig);
        var serverPath = workspace.Path("server");

        var result = await new ServerInstallService(FakeHttpHandler.Serving(zip))
            .InstallAsync(Release("r800000.Wizard_1_610", zip), serverPath);

        Assert.True(result.Success, result.Message);

        // The release config does not mention a patch server at all, and Imlight kills the whole
        // process while loading Root.wad when it cannot reach one.
        Assert.Equal(ImlightConfig.PublicPatchServerUrl, result.PatchServerUrl);

        var installed = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(installed);
        Assert.Equal(ImlightConfig.PublicPatchServerUrl, installed.PatchServerUrl);
    }

    [Fact]
    public async Task KeepsAPatchServerTheUserConfiguredAcrossAnUpdate() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.Path("server");

        var v1 = BuildReleaseZip("v1", ReleaseConfig);
        Assert.True((await new ServerInstallService(FakeHttpHandler.Serving(v1))
            .InstallAsync(Release("r800000.Wizard_1_610", v1), serverPath)).Success);

        var config = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(config);
        config.PatchServerUrl = "https://my-aurorium.test/";
        config.Save(serverPath);

        var v2 = BuildReleaseZip("v2", ReleaseConfig);
        var result = await new ServerInstallService(FakeHttpHandler.Serving(v2))
            .InstallAsync(Release("r810000.Wizard_1_610", v2), serverPath);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.PatchServerUrl);

        var updated = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(updated);
        Assert.Equal("https://my-aurorium.test", updated.PatchServerUrl);
    }

    [Fact]
    public async Task CopesWithAnArchiveThatShipsNoConfigFile() {
        using var workspace = new TempWorkspace();

        // No Imlight.ini to rewrite: the port step must be skipped rather than throw.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            Add(zip, "Imlight.Director.exe", "exe");
        }

        var bytes = stream.ToArray();
        var result = await new ServerInstallService(FakeHttpHandler.Serving(bytes))
            .InstallAsync(Release("r800000.Wizard_1_610", bytes), workspace.Path("server"));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.EmbeddedDatabasePort);
        Assert.Null(result.PatchServerUrl);
    }
}
