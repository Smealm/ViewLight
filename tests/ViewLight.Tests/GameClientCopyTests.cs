using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Copying the game into a portable folder is offered as a convenience, and the Steam caveat attached to
/// it is the part users trip over, so both are tested here.
/// </summary>
public class GameClientCopyTests {

    private static void MakeGame(TempWorkspace workspace, string folder, bool steam = false) {
        workspace.WriteFile(Path.Combine(folder, GameFolderLocator.RelativeClientExecutable), "stub");
        workspace.WriteFile(Path.Combine(folder, "Data", "GameData", "root.wad"), "wad");
        workspace.WriteFile(Path.Combine(folder, "Bin", "extra.dat"), "extra");

        if (steam) {
            workspace.WriteFile(Path.Combine(folder, "Bin", "steam_api.dll"), "steam");
        }
    }

    [Fact]
    public async Task CopiesTheWholeFolderAndSaysHowMuchItCopied() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("install");
        MakeGame(workspace, "install");

        var result = await new GameClientCopyService()
            .CopyAsync(source, workspace.Path(Path.Combine("stick", "game")));

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(result.Destination, GameFolderLocator.RelativeClientExecutable)));
        Assert.True(File.Exists(Path.Combine(result.Destination, "Bin", "extra.dat")));
        Assert.True(File.Exists(Path.Combine(result.Destination, "Data", "GameData", "root.wad")));

        // The copy is byte-identical, not just present.
        Assert.Equal(
            File.ReadAllText(Path.Combine(source, "Bin", "extra.dat")),
            File.ReadAllText(Path.Combine(result.Destination, "Bin", "extra.dat")));
    }

    [Fact]
    public async Task RefusesAFolderThatIsNotAWizard101Install() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("not-a-game");

        var result = await new GameClientCopyService()
            .CopyAsync(source, workspace.Path(Path.Combine("stick", "game")));

        Assert.False(result.Success);
        Assert.Contains("does not look like a Wizard101 install", result.Message);
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public async Task SaysTheGameIsAlreadyInPlaceRatherThanCopyingItOverItself() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("install");
        MakeGame(workspace, "install");

        var result = await new GameClientCopyService().CopyAsync(source, source);

        // Already there is a success - there is just nothing to do - and nothing is deleted either.
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesCopied);
        Assert.Contains("already in the launcher's folder", result.Message);
        Assert.True(File.Exists(Path.Combine(source, GameFolderLocator.RelativeClientExecutable)));
    }

    [Fact]
    public async Task RefusesWhenOneFolderIsInsideTheOther() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("install");
        MakeGame(workspace, "install");

        var result = await new GameClientCopyService()
            .CopyAsync(source, Path.Combine(source, "game"));

        Assert.False(result.Success);
        Assert.Contains("inside the other", result.Message);
    }

    [Fact]
    public async Task ACancelledCopyDoesNotClaimSuccess() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("install");
        MakeGame(workspace, "install");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new GameClientCopyService()
            .CopyAsync(source, workspace.Path(Path.Combine("stick", "game")),
                cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the Steam caveat

    [Fact]
    public async Task WarnsAboutWhatASteamCopyMightNeedAndWhatWouldNot() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("steam-install");
        MakeGame(workspace, "steam-install", steam: true);

        var result = await new GameClientCopyService()
            .CopyAsync(source, workspace.Path(Path.Combine("stick", "game")));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Warning);
        Assert.Contains("Steam", result.Warning);

        // It is a caveat rather than a refusal, and it is spelled out as one: the build plays without the
        // Steam client today, so a warning that said it would not start would be wrong about the folder in
        // front of the user - and about the one fact worth knowing, which is that KingsIsle can change it.
        Assert.Contains("steam_api.dll", result.Warning);
        Assert.Contains("may require", result.Warning, StringComparison.OrdinalIgnoreCase);

        // The point of the warning is that a standalone copy would not have the problem at all.
        Assert.Contains("standalone", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaysNothingExtraAboutAStandaloneClient() {
        using var workspace = new TempWorkspace();
        var source = workspace.CreateDirectory("install");
        MakeGame(workspace, "install");

        var result = await new GameClientCopyService()
            .CopyAsync(source, workspace.Path(Path.Combine("stick", "game")));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void RecognisesASteamInstall() {
        using var workspace = new TempWorkspace();

        workspace.CreateDirectory("steam-game/bin");
        workspace.WriteFile("steam-game/bin/steam_api.dll", "steam");
        Assert.True(GameFolderLocator.IsSteamInstall(workspace.Path("steam-game")));

        workspace.CreateDirectory("plain-game/bin");
        Assert.False(GameFolderLocator.IsSteamInstall(workspace.Path("plain-game")));
    }

    [Fact]
    public void SaysNothingAboutSteamForAFolderThatIsNotThere() {
        using var workspace = new TempWorkspace();

        Assert.False(GameFolderLocator.IsSteamInstall(workspace.Path("nothing-here")));
        Assert.False(GameFolderLocator.IsSteamInstall(null));
        Assert.False(GameFolderLocator.IsSteamInstall(""));
    }
}
