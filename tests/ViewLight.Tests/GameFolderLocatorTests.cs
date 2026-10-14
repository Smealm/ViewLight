using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class GameFolderLocatorTests {

    [Fact]
    public void TheDocumentedRelativePathsAreWhatTheRestOfTheAppUses() {
        Assert.Equal(@"bin\WizardGraphicalClient.exe", GameFolderLocator.RelativeClientExecutable);
        Assert.Equal(@"Data\GameData\root.wad", GameFolderLocator.RelativeRootWad);
    }

    [Fact]
    public void LooksLikeGameFolder_AcceptsAFolderWithTheClientInBin() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", GameFolderLocator.RelativeClientExecutable), "stub");

        Assert.True(GameFolderLocator.LooksLikeGameFolder(gameFolder));
    }

    [Fact]
    public void LooksLikeGameFolder_AcceptsAFolderWithOnlyTheArchive() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("client");
        workspace.WriteFile(Path.Combine("client", GameFolderLocator.RelativeRootWad), [1, 2, 3]);

        Assert.True(GameFolderLocator.LooksLikeGameFolder(gameFolder));
    }

    [Fact]
    public void LooksLikeGameFolder_RejectsEverythingElse() {
        using var workspace = new TempWorkspace();

        Assert.False(GameFolderLocator.LooksLikeGameFolder(null));
        Assert.False(GameFolderLocator.LooksLikeGameFolder(string.Empty));
        Assert.False(GameFolderLocator.LooksLikeGameFolder(@"Z:\definitely\not\here"));
        Assert.False(GameFolderLocator.LooksLikeGameFolder(workspace.CreateDirectory("empty-folder")));
    }

    [Fact]
    public void LooksLikeGameFolder_RejectsADataFolderThatIsNotAGameFolder() {
        using var workspace = new TempWorkspace();

        // Shares the folder name "Wizard101" but has neither marker file.
        var notTheGame = workspace.CreateDirectory(Path.Combine("steamapps", "common", "Wizard101"));
        workspace.WriteFile(Path.Combine("steamapps", "common", "Wizard101", "readme.txt"), "nope");

        Assert.False(GameFolderLocator.LooksLikeGameFolder(notTheGame));
    }

    // ------------------------------------------------------------------ too-deep settings

    [Fact]
    public void NormaliseGameFolder_LiftsABinFolderUpToTheGameFolder() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("Wizard101");
        var binFolder = workspace.CreateDirectory(Path.Combine("Wizard101", "bin"));
        workspace.WriteFile(Path.Combine("Wizard101", GameFolderLocator.RelativeClientExecutable), "stub");

        // Selecting bin\ is the easy mistake: that is where the executable actually is.
        Assert.Equal(gameFolder, GameFolderLocator.NormaliseGameFolder(binFolder));
        Assert.Equal(gameFolder, GameFolderLocator.NormaliseGameFolder(binFolder + "\\"));
    }

    [Fact]
    public void NormaliseGameFolder_LeavesACorrectGameFolderAlone() {
        using var workspace = new TempWorkspace();
        var gameFolder = workspace.CreateDirectory("Wizard101");
        workspace.WriteFile(Path.Combine("Wizard101", GameFolderLocator.RelativeRootWad), [1, 2, 3]);

        Assert.Equal(gameFolder, GameFolderLocator.NormaliseGameFolder(gameFolder));
    }

    [Fact]
    public void NormaliseGameFolder_OnlyCorrectsBinFoldersItCanActuallyFix() {
        using var workspace = new TempWorkspace();

        // A bin folder whose parent is not a game folder must be left as the user set it.
        var orphanBin = workspace.CreateDirectory(Path.Combine("random", "bin"));
        Assert.Equal(orphanBin, GameFolderLocator.NormaliseGameFolder(orphanBin));

        Assert.Equal(string.Empty, GameFolderLocator.NormaliseGameFolder(null));
        Assert.Equal(string.Empty, GameFolderLocator.NormaliseGameFolder("   "));
    }

    [Fact]
    public void NormaliseGameFolder_DoesNotInventAFolderThatIsNotThere() {
        using var workspace = new TempWorkspace();
        var missing = workspace.Path(Path.Combine("Wizard101", "bin"));

        Assert.Equal(missing, GameFolderLocator.NormaliseGameFolder(missing));
    }

    [Fact]
    public void Candidates_NeverThrowsAndReturnsNormalisedPaths() {
        // Touches the real registry on Windows and the conventional paths elsewhere; the point is that
        // detection is best effort and must not throw when Steam is not installed.
        var candidates = GameFolderLocator.Candidates();

        Assert.NotNull(candidates);
        foreach (var candidate in candidates) {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Path));
            Assert.Equal(Path.GetFullPath(candidate.Path), candidate.Path);
            Assert.False(string.IsNullOrWhiteSpace(candidate.Source));
        }
    }

    [Fact]
    public void Candidates_AreDeduplicated() {
        var paths = GameFolderLocator.Candidates()
            .Select(c => c.Path)
            .ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Detect_ReturnsNullWhenNothingIsInstalledAnywhere() {
        // This machine may or may not have Wizard101; if detection succeeds the result must at least
        // be a folder that really looks like an install.
        var detected = GameFolderLocator.Detect();

        if (detected is not null) {
            Assert.True(GameFolderLocator.LooksLikeGameFolder(detected.Path));
        }
    }
}
