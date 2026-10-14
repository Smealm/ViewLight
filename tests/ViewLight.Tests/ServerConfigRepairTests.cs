using System.Net;
using System.Net.Sockets;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Covers the repairs that stand between a stock <c>Imlight.ini</c> and a server that boots at all.
/// </summary>
/// <remarks>
/// The patch server half of this exists because of a real crash: with the stock localhost address in
/// place and nothing behind it, Imlight throws while loading <c>Root.wad</c>, nothing catches it, and
/// the .NET runtime tears the process down - surfacing to the user as nothing but exit code
/// <c>-532462766</c>. So these tests are as much documentation of the failure as they are checks.
/// </remarks>
public class ServerConfigRepairTests {

    /// <summary>The stock config, minus everything these repairs do not touch.</summary>
    private const string StockConfig =
        "; Imlight configuration\r\n" +
        "[Global Settings]\r\n" +
        "GameRevision = r803238.Wizard_1_610\r\n" +
        "\r\n" +
        "[Patch Server]\r\n" +
        "; The URL to the patch server.\r\n" +
        "PatchServerInternalUrl = http://127.0.0.1:12369\r\n" +
        "PatchServerPort = 12500\r\n" +
        "\r\n" +
        "[Database]\r\n" +
        "AdminAccountUsername = admin\r\n" +
        "; The port used by the embedded database.\r\n" +
        "EmbeddedDatabasePort = 8080\r\n";

    // ------------------------------------------------------------------ the decision itself

    [Fact]
    public void ReplacesABlankOrStockPatchServerWhenNothingAnswersThere() {
        Assert.True(ServerConfigRepair.ShouldReplacePatchServer(null, _ => true));
        Assert.True(ServerConfigRepair.ShouldReplacePatchServer(string.Empty, _ => true));
        Assert.True(ServerConfigRepair.ShouldReplacePatchServer("   ", _ => true));
        Assert.True(ServerConfigRepair.ShouldReplacePatchServer(ImlightConfig.StockPatchServerUrl, _ => false));
    }

    [Fact]
    public void KeepsASelfHostedPatchServerRunningOnTheStockAddress() {
        // The stock address is where an advanced user's own Aurorium patch server would listen, so a
        // live endpoint there is a working setup rather than the broken default.
        Assert.False(ServerConfigRepair.ShouldReplacePatchServer(ImlightConfig.StockPatchServerUrl, _ => true));
    }

    [Fact]
    public void NeverTouchesAPatchServerTheUserChose() {
        var probed = false;

        // A chosen address is left alone even when it is silent - it may just be down right now.
        Assert.False(ServerConfigRepair.ShouldReplacePatchServer(
            "https://patcher.example.test", _ => { probed = true; return false; }));

        Assert.False(probed);
    }

    [Fact]
    public void TreatsATrailingSlashAsTheSameAddress() {
        // Imlight builds "{url}/V_{revision}", so "…/12369/" must not read as a different server.
        Assert.True(ServerConfigRepair.ShouldReplacePatchServer(
            ImlightConfig.StockPatchServerUrl + "/", _ => false));
    }

    // ------------------------------------------------------------------ the live probe

    [Fact]
    public void DetectsAListeningLoopbackEndpointForReal() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listening = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(ServerConfigRepair.IsEndpointListening($"http://127.0.0.1:{listening}"));

        var free = PortAllocator.FindFreePort()!.Value;
        Assert.False(ServerConfigRepair.IsEndpointListening($"http://127.0.0.1:{free}"));
    }

    [Fact]
    public void DoesNotProbeHostsThatAreNotOnThisMachine() {
        // Whether a remote patch server is up is Imlight's business, not something the launcher infers
        // by connecting to it, and it must never be mistaken for a self-hosted one.
        Assert.False(ServerConfigRepair.IsEndpointListening(ImlightConfig.PublicPatchServerUrl));
    }

    // ------------------------------------------------------------------ writing the config

    [Fact]
    public void PointsABlankPatchServerAtThePublicOne() {
        var ini = IniFile.Parse("[Patch Server]\r\nPatchServerName = Imlight.Patch\r\n");

        var assigned = ServerConfigRepair.EnsureUsablePatchServer(ini);

        Assert.Equal(ImlightConfig.PublicPatchServerUrl, assigned);
        Assert.Equal(
            ImlightConfig.PublicPatchServerUrl,
            ini.Get(ImlightConfig.SectionPatch, "PatchServerInternalUrl"));
    }

    [Fact]
    public void LeavesAChosenPatchServerOutOfIt() {
        var ini = IniFile.Parse("[Patch Server]\r\nPatchServerInternalUrl = https://my-aurorium.test\r\n");

        Assert.Null(ServerConfigRepair.EnsureUsablePatchServer(ini));
        Assert.Equal("https://my-aurorium.test", ini.Get(ImlightConfig.SectionPatch, "PatchServerInternalUrl"));
    }

    [Fact]
    public void ApplyWritesThePublicPatchServerIntoAStockConfigOnDisk() {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile(
            Path.Combine("server", ImlightConfig.RelativeConfigPath), StockConfig);

        var repair = ServerConfigRepair.Apply(path);

        Assert.True(repair.ChangedAnything);
        Assert.Equal(ImlightConfig.PublicPatchServerUrl, repair.PatchServerUrl);
        Assert.NotNull(repair.EmbeddedDatabasePort);

        var text = File.ReadAllText(path);
        Assert.Contains($"PatchServerInternalUrl = {ImlightConfig.PublicPatchServerUrl}", text);

        // The file is documentation as much as configuration, so the parts nobody asked to change stay.
        Assert.Contains("; The URL to the patch server.", text);
        Assert.Contains("; The port used by the embedded database.", text);
        Assert.Contains("AdminAccountUsername = admin", text);
        Assert.Contains("PatchServerPort = 12500", text);

        var reloaded = ImlightConfig.TryLoad(workspace.Path("server"));
        Assert.NotNull(reloaded);
        Assert.Equal(ImlightConfig.PublicPatchServerUrl, reloaded.PatchServerUrl);
    }

    [Fact]
    public void ApplyChangesNothingWhenTheConfigIsAlreadyUsable() {
        var free = PortAllocator.FindFreePort()!.Value;
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile(
            Path.Combine("server", ImlightConfig.RelativeConfigPath),
            StockConfig
                .Replace(ImlightConfig.StockPatchServerUrl, "https://my-aurorium.test")
                .Replace("EmbeddedDatabasePort = 8080", $"EmbeddedDatabasePort = {free}"));

        var repair = ServerConfigRepair.Apply(path);

        Assert.False(repair.ChangedAnything);
    }

    [Fact]
    public void ApplyIsANoOpWhenThereIsNoConfigYet() {
        using var workspace = new TempWorkspace();

        var repair = ServerConfigRepair.Apply(
            workspace.Path(Path.Combine("server", ImlightConfig.RelativeConfigPath)));

        Assert.False(repair.ChangedAnything);
    }
}
