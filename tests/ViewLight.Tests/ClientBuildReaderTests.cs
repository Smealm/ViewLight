using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The client's build is what tells the user which two revisions they are holding, so both places it is
/// written down are read for real - including the fallback, because it is only needed on installs where
/// the first source is missing.
/// </summary>
public class ClientBuildReaderTests {

    [Fact]
    public void ReadsTheRevisionTheClientWrote() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", "bin", "revision.dat"), "r806919.Wizard_1_610\n");

        Assert.Equal("r806919.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void IgnoresATrailingNewlineAndAByteOrderMark() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", "bin", "revision.dat"), "\uFEFFr803238.Wizard_1_610\r\n");

        Assert.Equal("r803238.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void FallsBackToTheLineTheClientLogs() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", "bin", "WizardClient.log"),
            "10/13/26 18:02:15 [STAT] CORE_SEER       Wizard Graphical Client version 'W.1.610.21';"
            + " revision 'r806919.Wizard_1_610'. Data version 'r806919.Wizard_1_610'.\n");

        Assert.Equal("r806919.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void TheRevisionFileWinsOverTheLog() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        // A half-patched install can leave a log from the previous build behind. The revision file is the
        // client's own current statement of what it is, so it is the one that counts.
        workspace.WriteFile(Path.Combine("client", "bin", "revision.dat"), "r806919.Wizard_1_610\n");
        workspace.WriteFile(Path.Combine("client", "bin", "WizardClient.log"),
            "[STAT] CORE_SEER       Wizard Graphical Client version 'W.1.590.1'; revision 'r800000.Wizard_1_590'.\n");

        Assert.Equal("r806919.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void DoesNotMistakeTheOtherRevisionLinesInTheLog() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        // A real log mentions revisions in several other shapes first, including the patch URL, which
        // carries the same string. Only the "revision 'x'" line is the client stating its own.
        workspace.WriteFile(Path.Combine("client", "bin", "WizardClient.log"), """
            10/13/26 18:02:40 [DBGM] CORE_SEER       SYSTEM INFO: Revision: 512.00
            10/13/26 18:02:48 [STAT] CORE_SEER          <Value name="Revision">512.00</Value>
            10/13/26 18:02:49 [STAT] CORE_PATCH_SYS  UrlPrefix=http://versionak.us.wizard101.com/WizPatcher/V_r806919.Wizard_1_610/LatestBuild
            10/13/26 18:03:02 [STAT] CORE_SEER       Wizard Graphical Client version 'W.1.610.21'; revision 'r803238.Wizard_1_610'.
            """);

        Assert.Equal("r803238.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void ReportsNothingRatherThanGuessing() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        Assert.Null(ClientBuildReader.Read(gameFolder));
        Assert.Null(ClientBuildReader.Read(null));
        Assert.Null(ClientBuildReader.Read(string.Empty));
        Assert.Null(ClientBuildReader.Read(workspace.Path("no-such-folder")));

        // A log with nothing to say about the client's own build is not a build.
        workspace.WriteFile(Path.Combine("client", "bin", "WizardClient.log"), "Revision: 512.00\n");
        Assert.Null(ClientBuildReader.Read(gameFolder));

        // Nor is an empty revision file, which an interrupted patch can leave.
        workspace.WriteFile(Path.Combine("client", "bin", "revision.dat"), "\n");
        Assert.Null(ClientBuildReader.Read(gameFolder));
    }

    [Fact]
    public void OnlyTheFirstLineOfTheRevisionFileCounts() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", "bin", "revision.dat"),
            "r806919.Wizard_1_610\nsomething else entirely\n");

        Assert.Equal("r806919.Wizard_1_610", ClientBuildReader.Read(gameFolder));
    }

    [Theory]
    [InlineData("r806919.Wizard_1_610", "r806919.Wizard_1_610", true)]
    [InlineData("r806919.wizard_1_610", "r806919.Wizard_1_610", true)]
    [InlineData(" r806919.Wizard_1_610 ", "r806919.Wizard_1_610", true)]
    [InlineData("r806919.Wizard_1_610", "r803238.Wizard_1_610", false)]
    [InlineData(null, "r806919.Wizard_1_610", false)]
    [InlineData("r806919.Wizard_1_610", null, false)]
    [InlineData("", "r806919.Wizard_1_610", false)]
    public void TwoBuildsMatchOnlyWhenBothAreKnownAndTheSame(string? left, string? right, bool expected)
        => Assert.Equal(expected, ClientBuildReader.AreSame(left, right));
}
