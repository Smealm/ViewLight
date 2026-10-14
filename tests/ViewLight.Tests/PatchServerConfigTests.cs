using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The patch server's own config decides which port it serves on, and that port is the address Imlight
/// is then pointed at - so what the launcher writes into it, and when it leaves it alone, is pinned
/// down here.
/// </summary>
public class PatchServerConfigTests {

    [Fact]
    public void WritesTheEndpointImlightIsPointedAt() {
        var toml = PatchServerConfig.Build(12369);

        Assert.Contains("[server]", toml);
        Assert.Contains("endpoint = \"127.0.0.1:12369\"", toml);

        // The rest of the shape is Aurorium's own, with the one setting the launcher has a reason to
        // differ about: how long it waits before retrying an asset it could not download.
        Assert.Contains("[fetcher]", toml);
        Assert.Contains("save_directory = \"data\"", toml);
        Assert.Contains("[patch]", toml);
        Assert.Contains($"host = \"{PatchServerConfig.PublicPatchHost}\"", toml);

        Assert.Equal(12369, PatchServerConfig.ParsePort(toml));
    }

    [Fact]
    public void TheFetchSettingsAreTheOnesAFirstFetchFinishesUnder() {
        var toml = PatchServerConfig.Build(12369);

        // Aurorium ships eight hours between attempts and two downloads at a time. The interval is also
        // its retry cadence, so eight hours is a single failed asset holding a revision back for most of
        // a day - which is what "the patch server is up and fetching nothing" looks like.
        Assert.Contains($"fetch_interval = {PatchServerConfig.FetchIntervalSeconds}", toml);
        Assert.Contains($"concurrent_downloads = {PatchServerConfig.ConcurrentDownloads}", toml);
        Assert.Contains($"file_logging = {PatchServerConfig.FileLogging.ToString().ToLowerInvariant()}", toml);
        Assert.DoesNotContain("fetch_interval = 28800", toml);

        Assert.True(PatchServerConfig.FetchIntervalSeconds < 28800);
    }

    [Fact]
    public void TheWrittenConfigRoundTripsThroughAFolder() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        Assert.Null(PatchServerConfig.TryReadPort(folder));

        Assert.True(PatchServerConfig.EnsureConfig(folder, 12400));
        Assert.Equal(12400, PatchServerConfig.TryReadPort(folder));
        Assert.True(File.Exists(PatchServerConfig.PathFor(folder)));

        // The same port again is not a reason to rewrite the file: a config someone has grown by hand
        // must survive the launcher being opened.
        File.AppendAllText(PatchServerConfig.PathFor(folder), "# a hand-written note\r\n");
        Assert.False(PatchServerConfig.EnsureConfig(folder, 12400));
        Assert.Contains("a hand-written note", File.ReadAllText(PatchServerConfig.PathFor(folder)));

        // A different port is.
        Assert.True(PatchServerConfig.EnsureConfig(folder, 12500));
        Assert.Equal(12500, PatchServerConfig.TryReadPort(folder));
        Assert.DoesNotContain("a hand-written note", File.ReadAllText(PatchServerConfig.PathFor(folder)));
    }

    [Fact]
    public void AFolderTheLauncherAlreadySetUpIsNotWrittenAgain() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        Assert.True(PatchServerConfig.EnsureConfig(folder, 12400));

        // Untouched and already current: nothing to do, and no write on every launch.
        Assert.False(PatchServerConfig.EnsureConfig(folder, 12400));
        Assert.False(PatchServerConfig.IsALauncherTemplate(folder, 12400, out var isCurrent) && !isCurrent);
        Assert.True(isCurrent);
    }

    [Fact]
    public void AnOldCopyOfTheLaunchersOwnTemplateIsBroughtUpToDate() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        // What this launcher wrote before the fetch settings changed: recognised as ours, so a machine set
        // up back then is not left waiting eight hours between attempts for the rest of its life.
        File.WriteAllText(PatchServerConfig.PathFor(folder), PatchServerConfig.LegacyBuild(12400));

        Assert.True(PatchServerConfig.IsALauncherTemplate(folder, 12400, out var isCurrent));
        Assert.False(isCurrent);

        Assert.True(PatchServerConfig.EnsureConfig(folder, 12400));
        Assert.Equal(PatchServerConfig.Build(12400), File.ReadAllText(PatchServerConfig.PathFor(folder)));

        // And once it is current, that is the end of it.
        Assert.False(PatchServerConfig.EnsureConfig(folder, 12400));
    }

    [Fact]
    public void AConfigSomebodyEditedIsNotOneOfOurs() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        // One line changed by hand - a different upstream, a longer interval, a bigger queue - and the
        // file stops being the launcher's to update. The endpoint still is.
        File.WriteAllText(
            PatchServerConfig.PathFor(folder),
            PatchServerConfig.LegacyBuild(12400).Replace("fetch_interval = 28800", "fetch_interval = 60"));

        Assert.False(PatchServerConfig.IsALauncherTemplate(folder, 12400, out var isCurrent));
        Assert.False(isCurrent);
        Assert.False(PatchServerConfig.EnsureConfig(folder, 12400));
        Assert.Contains("fetch_interval = 60", File.ReadAllText(PatchServerConfig.PathFor(folder)));

        // A different endpoint is written over, hand-edited or not: it is the one value Imlight is pointed
        // at, and a patch server listening elsewhere is one nothing can reach.
        Assert.True(PatchServerConfig.EnsureConfig(folder, 12500));
        Assert.Equal(12500, PatchServerConfig.TryReadPort(folder));
        Assert.DoesNotContain("fetch_interval = 60", File.ReadAllText(PatchServerConfig.PathFor(folder)));
    }

    [Theory]
    // Written without quotes, the way a hand-edited TOML file can be.
    [InlineData("[server]\nendpoint = 127.0.0.1:13000\n", 13000)]
    // A comment inside the section, and other sections around it.
    [InlineData("[debug]\nlevel = \"info\"\n\n[server]\n# where it serves\nendpoint = \"127.0.0.1:13001\"\n\n[fetcher]\n", 13001)]
    [InlineData("[fetcher]\nsave_directory = \"data\"\n", null)]
    [InlineData("[server]\nendpoint = \"127.0.0.1\"\n", null)]
    [InlineData("[server]\nendpoint = \"127.0.0.1:99999\"\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadsTheEndpointOutOfWhateverTheFileSays(string? toml, int? expected)
        => Assert.Equal(expected, PatchServerConfig.ParsePort(toml));

    [Fact]
    public void EndpointIsTheAddressImlightIsGiven() {
        Assert.Equal("http://127.0.0.1:12369", PatchServerConfig.EndpointUrl(12369));
    }

    [Fact]
    public void TheStoredPortDefaultsToTheOneImlightsOwnConfigPointsAt() {
        // Imlight's stock PatchServerInternalUrl is http://127.0.0.1:12369, so a default install lands
        // exactly where Imlight already looks. The two constants live in different layers, so this is
        // what keeps them in step.
        Assert.Equal(PatchServerConfig.DefaultPort, new LauncherSettings().PatchServerPort);
    }

    [Fact]
    public void TheStockAddressIsTheOneTheServersOwnRepairLeavesAlone() {
        // The whole point of the default port: a patch server running there does not look like the
        // broken default Imlight ships, because ServerConfigRepair asks whether something answers.
        var stock = PatchServerConfig.EndpointUrl(PatchServerConfig.DefaultPort);

        Assert.Equal(ImlightConfig.StockPatchServerUrl, stock);
        Assert.True(ImlightConfig.IsUnusablePatchServerUrl(stock));
        Assert.False(ServerConfigRepair.ShouldReplacePatchServer(stock, _ => true));
    }
}
