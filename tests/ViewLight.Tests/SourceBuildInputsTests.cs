using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Imlight's two unpublished build inputs, made from the game rather than asked of the user.
/// </summary>
/// <remarks>
/// The game itself is not started here: a test that launches Wizard101 would be a test of this machine.
/// What is pinned down is the part around it - where the files go, which archive entries become message
/// definitions, and what is said when the game, <c>Root.wad</c> or Python is missing.
/// </remarks>
public class SourceBuildInputsTests {

    private const string ManualDump = "{\"version\":2,\"classes\":{\"SomeType\":{\"flags\":0}}}";

    private sealed class NeverCalledRunner : IToolRunner {

        public List<string> Calls { get; } = [];

        public Task<ToolResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string>? onLine = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default) {

            Calls.Add($"{fileName} {string.Join(' ', arguments)}");
            throw new InvalidOperationException("No tool should run when the inputs are already there.");
        }
    }

    private sealed class FailingRunner : IToolRunner {

        public List<string> Calls { get; } = [];

        public Task<ToolResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string>? onLine = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default) {

            Calls.Add($"{fileName} {string.Join(' ', arguments)}");
            return Task.FromResult(new ToolResult(127, string.Empty, "not found"));
        }
    }

    private static void WriteRootWad(string gameFolder, params (string Name, string Content)[] files) {
        var data = Path.Combine(gameFolder, "Data", "GameData");
        Directory.CreateDirectory(data);

        var entries = files
            .Select(file => (file.Name, Encoding.UTF8.GetBytes(file.Content), Compressed: false))
            .ToArray();

        File.WriteAllBytes(Path.Combine(data, "Root.wad"), KiwadBuilder.Build(entries));
    }

    /// <summary>A hand-placed dump is used as it is, so the build works without Python at all.</summary>
    [Fact]
    public async Task AManualTypeDumpAndTheGamesMessagesBecomeTheBuildInputs() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder,
            ("Messages/BaseMessages.xml", "<Base />"),
            ("GameMessages.xml", "<Game />"),
            ("Config/Imlight.ini", "not a message"),
            ("Messages/Notes.txt", "not xml"));

        var checkout = workspace.CreateDirectory("checkout");

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);

        var runner = new NeverCalledRunner();
        var result = await new SourceBuildInputs(runner).PrepareAsync(checkout, gameFolder);

        Assert.True(result.Success, result.Message);
        Assert.Empty(runner.Calls);

        var objectInput = Path.Combine(
            checkout, "submodule", "Imcodec", "src", "Imcodec.ObjectProperty", "GeneratorInput");
        Assert.Equal(ManualDump, File.ReadAllText(Path.Combine(objectInput, "ClientDump.json")));

        var messageInput = Path.Combine(
            checkout, "submodule", "Imcodec", "src", "Imcodec.MessageLayer", "GeneratorInput");

        Assert.Equal(new[] { "BaseMessages.xml", "GameMessages.xml" },
            Directory.GetFiles(messageInput, "*.xml")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal("<Base />", File.ReadAllText(Path.Combine(messageInput, "BaseMessages.xml")));
    }

    /// <summary>Only the game's message definitions are copied, not every XML in the archive.</summary>
    [Fact]
    public async Task LeavesAnOlderClientsMessageFilesOutOfTheCheckout() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder, ("GameMessages.xml", "<Game />"));

        var checkout = workspace.CreateDirectory("checkout");

        var messageInput = Path.Combine(
            checkout, "submodule", "Imcodec", "src", "Imcodec.MessageLayer", "GeneratorInput");
        Directory.CreateDirectory(messageInput);
        File.WriteAllText(Path.Combine(messageInput, "OldMessages.xml"), "<Old />");

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);

        var result = await new SourceBuildInputs(new NeverCalledRunner()).PrepareAsync(checkout, gameFolder);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "GameMessages.xml" },
            Directory.GetFiles(messageInput, "*.xml").Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task WithoutAGameFolderItSaysWhichFolderTheBuildNeeds() {
        using var workspace = new TempWorkspace();

        var result = await new SourceBuildInputs(new NeverCalledRunner())
            .PrepareAsync(workspace.CreateDirectory("checkout"), null);

        Assert.False(result.Success);
        Assert.Contains("Wizard101 install", result.Message);
    }

    [Fact]
    public async Task WithoutRootWadItNamesTheFileRatherThanQuotingTheCompiler() {
        using var workspace = new TempWorkspace();

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);

        var result = await new SourceBuildInputs(new NeverCalledRunner())
            .PrepareAsync(workspace.CreateDirectory("checkout"), workspace.CreateDirectory("game"));

        Assert.False(result.Success);
        Assert.Contains("root.wad", result.Message);
    }

    [Fact]
    public async Task AnArchiveWithoutMessageDefinitionsIsAnIncompleteInstall() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder, ("Config/Imlight.ini", "not a message"));

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);

        var result = await new SourceBuildInputs(new NeverCalledRunner())
            .PrepareAsync(workspace.CreateDirectory("checkout"), gameFolder);

        Assert.False(result.Success);
        Assert.Contains("*Messages*.xml", result.Message);
    }

    /// <summary>No dump and no Python is the one case a user can fix by installing something.</summary>
    [Fact]
    public async Task WithoutPythonItSaysWhatToInstallAndWhereADumpCanGoInstead() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder, ("GameMessages.xml", "<Game />"));

        var result = await new SourceBuildInputs(new FailingRunner())
            .PrepareAsync(workspace.CreateDirectory("checkout"), gameFolder);

        Assert.False(result.Success);
        Assert.Contains("Python 3.12", result.Message);
        Assert.Contains(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), result.Message);
    }

    /// <summary>
    /// A dump this class generated records the client revision, so a game update regenerates it.
    /// </summary>
    [Fact]
    public async Task ADumpFromAnOlderClientRevisionIsNotReused() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder, ("GameMessages.xml", "<Game />"));
        Directory.CreateDirectory(Path.Combine(gameFolder, "bin"));
        File.WriteAllText(Path.Combine(gameFolder, "bin", "revision.dat"), "r806919.Wizard_1_610");

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);
        File.WriteAllText(
            Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json.revision"), "r800000.Wizard_1_600");

        var runner = new FailingRunner();
        var result = await new SourceBuildInputs(runner)
            .PrepareAsync(workspace.CreateDirectory("checkout"), gameFolder);

        Assert.False(result.Success);

        // It went looking for a Python to run wiztype with, rather than building against the old types.
        Assert.Contains(runner.Calls, call => call.StartsWith("py ", StringComparison.Ordinal)
                                              || call.StartsWith("python ", StringComparison.Ordinal)
                                              || call.StartsWith("python3 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADumpFromTheCurrentClientRevisionIsReused() {
        using var workspace = new TempWorkspace();

        var gameFolder = workspace.CreateDirectory("game");
        WriteRootWad(gameFolder, ("GameMessages.xml", "<Game />"));
        Directory.CreateDirectory(Path.Combine(gameFolder, "bin"));
        File.WriteAllText(Path.Combine(gameFolder, "bin", "revision.dat"), "r806919.Wizard_1_610");

        Directory.CreateDirectory(LauncherPaths.SourceDirectory);
        File.WriteAllText(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), ManualDump);
        File.WriteAllText(
            Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json.revision"), "r806919.Wizard_1_610");

        var runner = new NeverCalledRunner();
        var result = await new SourceBuildInputs(runner)
            .PrepareAsync(workspace.CreateDirectory("checkout"), gameFolder);

        Assert.True(result.Success, result.Message);
        Assert.Empty(runner.Calls);
    }
}
