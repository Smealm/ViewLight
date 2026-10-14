using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class ClientLauncherTests {

    [Fact]
    public void BuildArguments_MatchesTheDocumentedClientSyntax() {
        // Imlight's README: "-L 127.0.0.1 12000 will work fine."
        Assert.Equal("-L 127.0.0.1 12000", ClientLauncher.BuildArguments("127.0.0.1", 12000));
        Assert.Equal("-L 10.0.0.5 13001", ClientLauncher.BuildArguments("10.0.0.5", 13001));
    }

    [Fact]
    public void FindExecutable_ReturnsNullForAnUnsetOrMissingFolder() {
        Assert.Null(ClientLauncher.FindExecutable(null));
        Assert.Null(ClientLauncher.FindExecutable(string.Empty));
        Assert.Null(ClientLauncher.FindExecutable(@"Z:\definitely\not\here"));
    }

    [Fact]
    public void FindExecutable_UsesTheDocumentedRelativePath() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        Assert.Null(ClientLauncher.FindExecutable(gameFolder));

        // bin\WizardGraphicalClient.exe, relative to the game folder.
        var expected = workspace.WriteFile(
            Path.Combine("client", ClientLauncher.RelativeExecutable), "stub");

        Assert.Equal(expected, ClientLauncher.FindExecutable(gameFolder));
        Assert.Equal(@"bin\WizardGraphicalClient.exe", ClientLauncher.RelativeExecutable);
    }

    [Fact]
    public void FindExecutable_FallsBackToAnotherGraphicalClientNameInsideBin() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        var renamed = workspace.WriteFile(Path.Combine("client", "bin", "WizardGraphicalClient64.exe"), "stub");

        Assert.Equal(renamed, ClientLauncher.FindExecutable(gameFolder));
    }

    // ------------------------------------------------------------------ working directory

    [Fact]
    public void BuildStartInfo_RunsTheClientFromBinNotFromTheGameFolder() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        var executable = workspace.WriteFile(
            Path.Combine("client", ClientLauncher.RelativeExecutable), "stub");

        var startInfo = ClientLauncher.BuildStartInfo(executable, "10.0.0.5", 13001);

        Assert.Equal(executable, startInfo.FileName);

        // The client reads PatchConfig.xml and writes WizardClient.log relative to its working
        // directory, and both live in bin\ - so bin\ it is, not the game folder above it. Started from
        // the game folder it cannot open PatchConfig.xml at all.
        Assert.Equal(workspace.Path(Path.Combine("client", "bin")), startInfo.WorkingDirectory);
        Assert.NotEqual(gameFolder, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["-L", "10.0.0.5", "13001"], startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void WorkingDirectory_IsTheFolderHoldingTheExecutable() {
        using var workspace = new TempWorkspace();
        var executable = workspace.WriteFile(
            Path.Combine("client", ClientLauncher.RelativeExecutable), "stub");

        Assert.Equal(workspace.Path(Path.Combine("client", "bin")), ClientLauncher.WorkingDirectoryFor(executable));
    }

    [Fact]
    public void FindExecutable_WillNotPickUpTheStoreLauncherInTheGameFolderRoot() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");

        // A normal install has this sitting next to Data\; starting it with -L would be wrong.
        workspace.WriteFile(Path.Combine("client", "Wizard101.exe"), "store launcher");

        Assert.Null(ClientLauncher.FindExecutable(gameFolder));
    }

    // ------------------------------------------------------------------ pointing the client elsewhere

    [Fact]
    public void BuildArguments_PointsAtWhateverAddressItIsGiven() {
        // Joining another server is the same argument with a different address, so nothing here is
        // specific to the launcher's own config.
        Assert.Equal("-L 192.168.1.20 12000", ClientLauncher.BuildArguments("192.168.1.20", 12000));
        Assert.Equal("-L login.us.wizard101.com 12000",
            ClientLauncher.BuildArguments("login.us.wizard101.com", 12000));
    }

    [Fact]
    public void BuildArguments_SendsSteamOnlyWhenTheAccountIsASteamOne() {
        // -ST is "Steam required": a standalone account must not be handed it, and a Steam account
        // cannot sign in without it, so it is never guessed at.
        Assert.Equal("-L login.us.wizard101.com 12000",
            ClientLauncher.BuildArguments("login.us.wizard101.com", 12000, steamRequired: false));
        Assert.Equal("-L login.us.wizard101.com 12000 -ST",
            ClientLauncher.BuildArguments("login.us.wizard101.com", 12000, steamRequired: true));
    }

    [Fact]
    public void BuildStartInfo_PassesSteamBeforeTheAccountItBelongsTo() {
        using var workspace = new TempWorkspace();
        var executable = workspace.WriteFile(
            Path.Combine("client", ClientLauncher.RelativeExecutable), "stub");

        var startInfo = ClientLauncher.BuildStartInfo(
            executable, "10.0.0.5", 12000, new ClientLogin(42, "key"), steamRequired: true);

        Assert.Equal(["-L", "10.0.0.5", "12000", "-ST", "-U", "..42", "key"],
            startInfo.ArgumentList.ToArray());
    }
}
