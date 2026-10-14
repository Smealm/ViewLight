using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class ImlightConfigTests {

    private const string Sample =
        "[Global Settings]\r\n" +
        "GameRevision = r803238.Wizard_1_610\r\n" +
        "; comment\r\n" +
        "EnforceRevision = false\r\n" +
        "\r\n" +
        "[Login Server]\r\n" +
        "LoginServerPort = 12000\r\n" +
        "\r\n" +
        "[Game Server]\r\n" +
        "GameServerPort = 12333\r\n" +
        "GameServerIP = 127.0.0.1\r\n" +
        "GameServerCount = 3\r\n" +
        "RealmNames = Centaur,Pegasus,Phoenix\r\n" +
        "\r\n" +
        "[Character]\r\n" +
        "StartingLevel = 1\r\n" +
        "TutorialDisabled = false\r\n" +
        "DefaultItems = 1317127,1317128,87256\r\n" +
        "StartingZone = WizardCity/Interiors/WC_Headmistress_House\r\n" +
        "BaseGoldPouch = 1000000\r\n" +
        "\r\n" +
        "[Logging]\r\n" +
        "LogPath = ./logs/log.txt\r\n" +
        "LogLevel = DEBUG\r\n" +
        "\r\n" +
        "[Database]\r\n" +
        "SpiralDBAutoFetch = true\r\n" +
        "AdminAccountUsername = admin\r\n" +
        "AdminAccountPassword = compsci\r\n";

    private static ImlightConfig Load(TempWorkspace workspace) {
        workspace.WriteFile(Path.Combine("server", ImlightConfig.RelativeConfigPath), Sample);
        var config = ImlightConfig.TryLoad(workspace.Path("server"));
        Assert.NotNull(config);
        return config;
    }

    [Fact]
    public void TryLoad_ReturnsNullWhenNothingIsInstalled() {
        using var workspace = new TempWorkspace();

        Assert.Null(ImlightConfig.TryLoad(workspace.Path("nothing-here")));
    }

    [Fact]
    public void ReadsEveryValueTheLauncherUses() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        Assert.Equal("admin", config.AdminUsername);
        Assert.Equal("compsci", config.AdminPassword);
        Assert.False(config.TutorialDisabled);
        Assert.Equal(1, config.StartingLevel);
        Assert.Equal("WizardCity/Interiors/WC_Headmistress_House", config.StartingZone);
        Assert.Equal(1_000_000, config.BaseGoldPouch);
        Assert.Equal("127.0.0.1", config.GameServerIp);
        Assert.Equal(12000, config.LoginServerPort);
        Assert.Equal(12333, config.GameServerPort);
        Assert.Equal(3, config.GameServerCount);
        Assert.Equal("Centaur,Pegasus,Phoenix", config.RealmNames);
        Assert.True(config.SpiralDbAutoFetch);
        Assert.Equal("DEBUG", config.LogLevel);
        Assert.Equal("r803238.Wizard_1_610", config.GameRevision);
    }

    [Fact]
    public void DefaultItemsIds_ParsesAndDeduplicates() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        Assert.Equal([1317127u, 1317128u, 87256u], config.DefaultItemsIds);
    }

    [Fact]
    public void BuildsTheClientLaunchArgumentsFromTheIni() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        Assert.Equal("-L 127.0.0.1 12000", config.BuildClientLaunchArguments());

        config.GameServerIp = "10.0.0.5";
        config.LoginServerPort = 13000;

        Assert.Equal("-L 10.0.0.5 13000", config.BuildClientLaunchArguments());
    }

    [Fact]
    public void SaveRoundTripsWhileKeepingComments() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        config.TutorialDisabled = true;
        config.AdminPassword = "s3cret";
        config.DefaultItems = "87256,126983";
        config.Save(workspace.Path("server"));

        var reloaded = ImlightConfig.TryLoad(workspace.Path("server"));
        Assert.NotNull(reloaded);
        Assert.True(reloaded.TutorialDisabled);
        Assert.Equal("s3cret", reloaded.AdminPassword);
        Assert.Equal([87256u, 126983u], reloaded.DefaultItemsIds);

        var text = File.ReadAllText(ImlightConfig.ConfigPathFor(workspace.Path("server")));
        Assert.Contains("; comment", text);
        Assert.Contains("RealmNames = Centaur,Pegasus,Phoenix", text);
    }

    [Fact]
    public void ResolveLogFilePath_MakesRelativePathsAbsolute() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        var expected = Path.GetFullPath(Path.Combine(workspace.Path("server"), "logs", "log.txt"));
        Assert.Equal(expected, config.ResolveLogFilePath(workspace.Path("server")));
    }

    [Fact]
    public void ResolveLogFilePath_FindsTheDailyFileSerilogActuallyWrites() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);
        var serverPath = workspace.Path("server");

        // Serilog rolls the file daily, so `./logs/log.txt` on disk is `./logs/log<date>.txt` and the
        // configured name never exists. Resolving to the configured name would leave the launcher
        // watching a file that is not there - no readiness line, and no crash errors to quote back.
        var rolled = workspace.WriteFile(
            Path.Combine("server", "logs", $"log{DateTime.Now:yyyyMMdd}.txt"), "[ERR] something");

        Assert.Equal(rolled, config.ResolveLogFilePath(serverPath));
    }

    [Fact]
    public void ResolveLogFilePath_WatchesTodaysFileOnTheFirstRunOfADay() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);
        var serverPath = workspace.Path("server");

        // Only yesterday's daily file is on disk, and the server has not started today yet. That file
        // is not the one this run writes to, so it must not be the one watched - its readiness line
        // would be read as evidence of a server that is up, and it would never gain another line.
        workspace.WriteFile(Path.Combine("server", "logs", "log20200101.txt"), "old");

        Assert.Equal(
            Path.Combine(serverPath, "logs", $"log{DateTime.Now:yyyyMMdd}.txt"),
            config.ResolveLogFilePath(serverPath));
    }

    [Fact]
    public void ResolveLogFilePath_PrefersTheMostRecentlyWrittenLogWhenItIsNotADailyName() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);
        var serverPath = workspace.Path("server");

        // A config using another rolling interval, or files kept by hand: the newest is the live one.
        var older = workspace.WriteFile(Path.Combine("server", "logs", "log-2020.txt"), "old");
        var newer = workspace.WriteFile(Path.Combine("server", "logs", "log-2021.txt"), "new");

        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        Assert.Equal(newer, config.ResolveLogFilePath(serverPath));

        // The exact name in the config wins when it is really there.
        var exact = workspace.WriteFile(Path.Combine("server", "logs", "log.txt"), "exact");
        Assert.Equal(exact, config.ResolveLogFilePath(serverPath));
    }

    [Fact]
    public void ResolveLogFilePath_FallsBackToTheConfiguredNameWhenThereIsNoLogAtAll() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.Path("server"), "logs", "log.txt")),
            config.ResolveLogFilePath(workspace.Path("server")));
    }

    // ------------------------------------------------------------------ starter kit hard limits

    [Fact]
    public void ValidateStarterKit_RejectsAnythingBelowTwoItems() {
        // Below two the client bricks the character, so this is a hard lock and not a warning.
        Assert.Equal(ImlightConfig.TooFewStarterItemsMessage, ImlightConfig.ValidateStarterKit(0));
        Assert.Equal(ImlightConfig.TooFewStarterItemsMessage, ImlightConfig.ValidateStarterKit(1));
        Assert.Equal("Can't be less than two.", ImlightConfig.ValidateStarterKit(1));
    }

    [Fact]
    public void ValidateStarterKit_AcceptsTheSmallestAndLargestValidKits() {
        Assert.Null(ImlightConfig.ValidateStarterKit(ImlightConfig.MinDefaultItems));
        Assert.Null(ImlightConfig.ValidateStarterKit(ImlightConfig.MaxDefaultItems));
    }

    [Fact]
    public void ValidateStarterKit_RejectsMoreThanRavenDbCanBatch() {
        var error = ImlightConfig.ValidateStarterKit(ImlightConfig.MaxDefaultItems + 1);

        Assert.NotNull(error);
        Assert.Contains("16", error);
    }

    [Fact]
    public void TheLimitsAreTheOnesTheClientActuallyHas() {
        Assert.Equal(2, ImlightConfig.MinDefaultItems);
        Assert.Equal(16, ImlightConfig.MaxDefaultItems);
    }

    // ------------------------------------------------------------------ patch server

    [Fact]
    public void PatchServerUrl_IsReadFromTheConfig() {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("server", ImlightConfig.RelativeConfigPath),
            "[Patch Server]\r\n" +
            "PatchServerInternalUrl = https://patcher.phill030.de\r\n");

        var config = ImlightConfig.TryLoad(workspace.Path("server"));

        Assert.NotNull(config);
        Assert.Equal("https://patcher.phill030.de", config.PatchServerUrl);
    }

    [Fact]
    public void PatchServerUrl_LosesAnyTrailingSlashImlightWouldDoubleUp() {
        // Imlight requests "{url}/V_{revision}", so a stored trailing slash becomes "//V_…".
        using var workspace = new TempWorkspace();
        var serverPath = workspace.Path("server");
        workspace.WriteFile(Path.Combine("server", ImlightConfig.RelativeConfigPath),
            "[Patch Server]\r\n" +
            "PatchServerInternalUrl = https://patcher.phill030.de/\r\n");

        var config = ImlightConfig.TryLoad(serverPath);
        Assert.NotNull(config);
        Assert.Equal("https://patcher.phill030.de", config.PatchServerUrl);

        config.PatchServerUrl = "https://other.example.test///";
        config.Save(serverPath);

        Assert.Contains("PatchServerInternalUrl = https://other.example.test\r\n",
            File.ReadAllText(ImlightConfig.ConfigPathFor(serverPath)));
    }

    [Fact]
    public void UnusablePatchServerUrls_AreTheBlankAndStockOnes() {
        Assert.True(ImlightConfig.IsUnusablePatchServerUrl(null));
        Assert.True(ImlightConfig.IsUnusablePatchServerUrl("   "));
        Assert.True(ImlightConfig.IsUnusablePatchServerUrl(ImlightConfig.StockPatchServerUrl));
        Assert.True(ImlightConfig.IsUnusablePatchServerUrl("HTTP://127.0.0.1:12369/"));

        // A patch server the user chose is a fine address to leave alone, whatever it is.
        Assert.False(ImlightConfig.IsUnusablePatchServerUrl(ImlightConfig.PublicPatchServerUrl));
        Assert.False(ImlightConfig.IsUnusablePatchServerUrl("http://127.0.0.1:12370"));
    }

    [Fact]
    public void BooleanValuesAreWrittenInTheSpellingImlightParses() {
        using var workspace = new TempWorkspace();
        var config = Load(workspace);

        config.TutorialDisabled = true;
        config.EnforceRevision = true;
        config.SpiralDbAutoFetch = false;
        config.Save(workspace.Path("server"));

        var text = File.ReadAllText(ImlightConfig.ConfigPathFor(workspace.Path("server")));
        Assert.Contains("TutorialDisabled = true", text);
        Assert.Contains("EnforceRevision = true", text);
        Assert.Contains("SpiralDBAutoFetch = false", text);
    }
}
