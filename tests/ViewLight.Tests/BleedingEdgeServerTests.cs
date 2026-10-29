using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The bleeding-edge channel's decisions, without git or dotnet actually running.
/// </summary>
/// <remarks>
/// What is worth pinning down here is not that <c>git clone</c> works - it does - but the rules the
/// launcher puts around it: an uncommitted edit is never merged over, a fast-forward is preferred, a
/// conflict leaves the tree alone, and a build that stops in Imcodec says what is missing instead of
/// quoting the compiler.
/// </remarks>
public class BleedingEdgeServerTests {

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string ShortCommit = "abc1234";

    /// <summary>The tip of the default branch on GitHub - which is not the commit the checkout is on.</summary>
    private const string LatestCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string LatestShortCommit = "89abcde";

    private const string DefaultBranch = "quality-assurance";

    /// <summary>One branch that only exists on GitHub, so a build of it has to move the checkout.</summary>
    private const string OtherBranch = "feat/plugins";

    /// <summary>A checkout on disk, and a runner that answers as git and dotnet would.</summary>
    private sealed class ScriptedRunner : IToolRunner {

        private readonly Func<string, IReadOnlyList<string>, ToolResult> _respond;

        public ScriptedRunner(Func<string, IReadOnlyList<string>, ToolResult> respond) => _respond = respond;

        public List<string> Calls { get; } = [];

        public List<string> WorkingDirectories { get; } = [];

        public Task<ToolResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string>? onLine = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default) {

            Calls.Add($"{fileName} {string.Join(' ', arguments)}");
            WorkingDirectories.Add(workingDirectory);
            return Task.FromResult(_respond(fileName, arguments));
        }
    }

    private static string CreateCheckout() {
        var directory = Path.Combine(LauncherPaths.SourceDirectory, BleedingEdgeServer.CheckoutFolderName);
        Directory.CreateDirectory(Path.Combine(directory, ".git"));

        var project = Path.Combine(directory, "src", "Imlight.Director");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "Imlight.Director.csproj"),
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        return directory;
    }

    private static ToolResult Answer(IReadOnlyList<string> arguments, string? status = null) {
        var first = arguments.Count > 0 ? arguments[0] : string.Empty;

        if (first == "--version") {
            return new ToolResult(0, "git version 2.52.0", string.Empty);
        }

        if (first == "rev-parse" && arguments.Count > 1 && arguments[1] == "HEAD") {
            return new ToolResult(0, Commit, string.Empty);
        }

        if (first == "rev-parse" && arguments.Count > 2 && arguments[1] == "--short") {
            return new ToolResult(0, ShortCommit, string.Empty);
        }

        // The upstream and the checked-out branch are different questions - the first is where the branch
        // came from, the second is where the tree is - and the answer they used to share is the bug this
        // set of tests is about.
        if (first == "rev-parse" && arguments.Count > 2 && arguments[1] == "--abbrev-ref") {
            return new ToolResult(0, arguments[2] == "HEAD" ? DefaultBranch : $"origin/{DefaultBranch}",
                string.Empty);
        }

        // GitHub's answer, with the default branch named in the symref line and the tips below it. The tip
        // of the default branch is deliberately ahead of the commit the checkout is on.
        if (first == "ls-remote") {
            return new ToolResult(0,
                $"ref: refs/heads/{DefaultBranch}\tHEAD\n"
                + $"{LatestCommit}\tHEAD\n"
                + $"{Commit}\trefs/heads/{OtherBranch}\n"
                + $"{LatestCommit}\trefs/heads/{DefaultBranch}\n",
                string.Empty);
        }

        if (first == "rev-list") {
            return new ToolResult(0, "2", string.Empty);
        }

        if (first == "log") {
            return arguments.Contains("--format=%cI")
                ? new ToolResult(0, "2026-10-17T10:00:00+00:00", string.Empty)
                : new ToolResult(0, "fix: some server type fields", string.Empty);
        }

        if (first == "status") {
            return new ToolResult(0, status ?? string.Empty, string.Empty);
        }

        if (first == "--list-sdks") {
            return new ToolResult(0, "10.0.302 [C:\\Program Files\\dotnet\\sdk]", string.Empty);
        }

        return new ToolResult(0, string.Empty, string.Empty);
    }

    /// <summary>One of the projects' own output folders, as the checkout lays it out.</summary>
    private static string BuildOutput(string relative)
        => Path.Combine(
            LauncherPaths.SourceDirectory,
            BleedingEdgeServer.CheckoutFolderName,
            relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The Director output folder - the runnable tree a build leaves behind.</summary>
    private static string DirectorOutput => BuildOutput("src/Imlight.Director/bin/Release/net10.0/win-x64");

    /// <summary>
    /// Writes the build output the fake dotnet would have produced: the runnable tree in Director's RID
    /// folder, and the two libraries beside it in their own folders.
    /// </summary>
    private static void BuildInto(string marker = "edge") {
        Directory.CreateDirectory(Path.Combine(DirectorOutput, "Config"));
        File.WriteAllText(Path.Combine(DirectorOutput, "Imlight.Director.exe"), $"exe:{marker}");
        File.WriteAllText(Path.Combine(DirectorOutput, "Imlight.Director.dll"), $"director:{marker}");
        File.WriteAllText(Path.Combine(DirectorOutput, "Imlight.CoreLib.dll"), $"corelib:{marker}");
        File.WriteAllText(Path.Combine(DirectorOutput, "Imlight.Common.dll"), $"common:{marker}");
        File.WriteAllText(
            Path.Combine(DirectorOutput, "Config", "Imlight.ini"),
            "[Global Settings]\r\nGameRevision = r803238.Wizard_1_610\r\n\r\n[Database]\r\nAdminAccountUsername = admin\r\nAdminAccountPassword = compsci\r\n");
        File.WriteAllText(Path.Combine(DirectorOutput, "Config", "akka.conf"), "akka");

        // Each library also has a folder of its own. A file only that folder holds is what proves the
        // deploy reads all three rather than the runnable tree alone.
        var corelib = BuildOutput("src/Imlight.CoreLib/bin/Release/net10.0");
        Directory.CreateDirectory(Path.Combine(corelib, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(corelib, "Imlight.CoreLib.dll"), $"corelib:{marker}");
        File.WriteAllText(Path.Combine(corelib, "runtimes", "win-x64", "native", "corelib-only.dll"), "native");

        var common = BuildOutput("src/Imlight.Common/bin/Release/net10.0");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "Imlight.Common.dll"), $"common:{marker}");
    }

    // ------------------------------------------------------------------ what the card says

    [Fact]
    public async Task AFreshMachineIsNotAnErrorJustNothingToBuildYet() {
        using var workspace = new TempWorkspace();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync();

        Assert.False(state.IsCloned);
        Assert.Null(state.Problem);
        Assert.Contains(runner.Calls, call => call == "git --version");

        // The branch list and the newest commit arrive before the first clone, because they are asked of
        // GitHub rather than of a checkout - which is what lets this channel be chosen and aimed at a
        // branch on a machine that has never built anything.
        Assert.Contains(DefaultBranch, state.Branches!);
        Assert.Equal(LatestCommit, state.Latest?.Commit);

        // git has to be started somewhere that exists: a probe aimed at the checkout folder before
        // there is a checkout fails with "the directory name is invalid" and used to look exactly like
        // git not being installed at all.
        Assert.All(runner.WorkingDirectories, directory => Assert.True(Directory.Exists(directory), directory));
    }

    [Fact]
    public async Task AMachineWithoutGitSaysSoBeforeAnythingIsPressed() {
        using var workspace = new TempWorkspace();
        var runner = new ScriptedRunner((_, _) => new ToolResult(127, string.Empty, "git not found"));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync();

        Assert.False(state.IsCloned);
        Assert.Contains("needs git", state.Problem);
    }

    [Fact]
    public async Task WithACheckoutItSaysHowFarBehindGitHubItIs() {
        using var workspace = new TempWorkspace();
        CreateCheckout();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync();

        Assert.True(state.IsCloned);
        Assert.Equal(Commit, state.Commit);
        Assert.Equal(2, state.Behind);
        Assert.Equal(LatestShortCommit, state.Latest!.ShortCommit);
        Assert.Contains("2 commits behind", state.Summary);
        Assert.Contains("ls-remote", string.Join(' ', runner.Calls));
        Assert.Contains(runner.Calls, call => call.Contains("fetch --prune"));
    }

    [Fact]
    public async Task TheNewestCommitIsGitHubsAnswerNotWhateverTheCheckoutHasFetched() {
        using var workspace = new TempWorkspace();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync();

        // This is the bug the branch picker was added for: the card named the commit the checkout happened
        // to be on as "the latest source", so a fix that had landed on GitHub since the last successful
        // fetch never appeared at all.
        Assert.Equal(LatestCommit, state.Latest!.Commit);
        Assert.Contains(LatestShortCommit, state.Summary);
        Assert.DoesNotContain(ShortCommit, state.Summary);
    }

    [Fact]
    public async Task TheBranchesAndTheDefaultAreTheOnesGitHubHas() {
        using var workspace = new TempWorkspace();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync();

        Assert.Equal([OtherBranch, DefaultBranch], state.Branches);
        Assert.Equal(DefaultBranch, state.DefaultBranch);
    }

    [Fact]
    public async Task TheBranchThatWasAskedForIsTheOneThatIsRead() {
        using var workspace = new TempWorkspace();
        CreateCheckout();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync(OtherBranch);

        Assert.Equal(OtherBranch, state.Latest!.Branch);
        Assert.Equal(Commit, state.Latest.Commit);

        // Behind is counted against the commit GitHub named for that branch, not against whatever the
        // checkout happens to track.
        Assert.Contains(runner.Calls, call => call == $"git rev-list --count HEAD..{Commit}");
    }

    [Fact]
    public async Task ABranchGitHubNoLongerHasIsNamedAndTheDefaultIsWhatIsShown() {
        using var workspace = new TempWorkspace();
        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync("fix/client-desync");

        Assert.NotNull(state.Problem);
        Assert.Contains("fix/client-desync", state.Problem);
        Assert.Equal(DefaultBranch, state.Latest!.Branch);
    }

    [Fact]
    public async Task ACheckoutOnAnotherBranchIsNotTheNewestSourceOfTheOneThatWasChosen() {
        using var workspace = new TempWorkspace();
        CreateCheckout();
        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 2 && arguments[0] == "rev-parse" && arguments[1] == "--abbrev-ref"
                && arguments[2] == "HEAD"
                ? new ToolResult(0, OtherBranch, string.Empty)
                : Answer(arguments));

        var state = await new BleedingEdgeServer(runner: runner).CheckAsync(DefaultBranch);

        Assert.False(state.IsOnBranch);
        Assert.False(state.IsUpToDate);
        Assert.Contains($"the checkout is on {OtherBranch}", state.Summary);
    }

    // ------------------------------------------------------------------ updating the checkout

    /// <summary>Stands in for the wiztype and Root.wad work, which needs a game install.</summary>
    private sealed class FakeInputs : ISourceBuildInputs {

        public FakeInputs(bool success = true, string message = "inputs ready") {
            Success = success;
            Message = message;
        }

        private bool Success { get; }

        private string Message { get; }

        public string TypeDumpPath => Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json");

        public int Calls { get; private set; }

        public string? LastGameFolder { get; private set; }

        public Task<SourceBuildInputsResult> PrepareAsync(
            string checkout,
            string? gameFolder,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default) {

            Calls++;
            LastGameFolder = gameFolder;
            return Task.FromResult(new SourceBuildInputsResult(Success, Message));
        }
    }

    private static BleedingEdgeServer Server(ScriptedRunner runner, FakeInputs? inputs = null)
        => new(runner: runner, inputs: inputs ?? new FakeInputs());

    private static string ServerFolder(TempWorkspace workspace) => workspace.Path("server");

    [Fact]
    public async Task NeverMergesOverUncommittedEdits() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments, " M src/a.cs"));

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(runner.Calls, call => call.Contains(" merge "));
        Assert.Contains("uncommitted changes", result.Warning);
    }

    [Fact]
    public async Task BuildsTheBranchThatWasChosenRatherThanTheOneTheCheckoutTracks() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments));

        var result = await Server(runner).InstallAsync(ServerFolder(workspace), branch: OtherBranch);

        Assert.True(result.Success, result.Message);

        // The tree is moved onto the branch first, then fast-forwarded to that branch's tip - by name, so a
        // local branch that tracks something else cannot send the build back where it was.
        Assert.Contains(runner.Calls, call => call == $"git checkout -B {OtherBranch} --track origin/{OtherBranch}");
        Assert.Contains(runner.Calls, call => call.EndsWith($"merge --ff-only origin/{OtherBranch}"));
    }

    [Fact]
    public async Task FastForwardsAHeadThatIsSimplyBehind() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments));

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.True(result.Success, result.Message);
        Assert.Contains(runner.Calls, call => call.Contains("merge --ff-only"));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("merge --no-edit"));
        Assert.Contains(runner.Calls, call => call.Contains("submodule update --init --recursive"));
    }

    [Fact]
    public async Task MergesLocalCommitsWhenTheHistoryHasDiverged() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Count > 0 && arguments[0] == "build") {
                return Build(arguments);
            }

            if (arguments.Contains("--ff-only")) {
                return new ToolResult(1, string.Empty, "not possible to fast-forward");
            }

            return Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.True(result.Success, result.Message);
        Assert.Contains(runner.Calls, call => call.Contains("merge --no-edit"));
    }

    [Fact]
    public async Task AConflictedMergeIsAbortedAndLeavesTheTreeAlone() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Contains("merge") && arguments.Contains("--ff-only")) {
                return new ToolResult(1, string.Empty, "not possible to fast-forward");
            }

            if (arguments.Contains("--no-edit")) {
                return new ToolResult(1, string.Empty, "CONFLICT (content): Merge conflict in src/a.cs");
            }

            return Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.False(result.Success);
        Assert.Contains("resolve it", result.Message);
        Assert.Contains(runner.Calls, call => call.EndsWith("merge --abort"));
        Assert.DoesNotContain(runner.Calls, call => call.Contains(" build "));
    }

    // ------------------------------------------------------------------ building

    private static ToolResult Build(IReadOnlyList<string> arguments) {
        BuildInto();
        return new ToolResult(0, "Imlight.Director -> built", string.Empty);
    }

    [Fact]
    public async Task ClonesThenBuildsAndInstallsTheResult() {
        using var workspace = new TempWorkspace();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Count > 0 && arguments[0] == "clone") {
                CreateCheckout();
                return new ToolResult(0, string.Empty, "Cloning into 'imlight'...");
            }

            return arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.True(result.Success, result.Message);
        Assert.Equal("edge-" + ShortCommit, result.Tag);
        Assert.Equal(Commit, result.Commit);
        // The default branch is named at clone time, so choosing nothing and choosing the default are the
        // same build without a checkout in between.
        Assert.Contains(runner.Calls, call => call.Contains("clone --recurse-submodules")
                                             && call.Contains($"--branch {DefaultBranch}"));

        // The checkout's own output folders, built the way the checkout builds itself - no staging folder
        // and no publish output path of the launcher's choosing.
        Assert.Contains(runner.Calls, call => call.Contains("build src/Imlight.Director/Imlight.Director.csproj")
                                                 && call.Contains("--self-contained true")
                                                 && call.Contains("-r win-x64"));

        // The server folder is where the built files must end up - the executable and both libraries.
        Assert.Equal("exe:edge", File.ReadAllText(Path.Combine(ServerFolder(workspace), "Imlight.Director.exe")));
        Assert.Equal("director:edge", File.ReadAllText(Path.Combine(ServerFolder(workspace), "Imlight.Director.dll")));
        Assert.Equal("corelib:edge", File.ReadAllText(Path.Combine(ServerFolder(workspace), "Imlight.CoreLib.dll")));
        Assert.Equal("common:edge", File.ReadAllText(Path.Combine(ServerFolder(workspace), "Imlight.Common.dll")));
        Assert.Equal("akka", File.ReadAllText(Path.Combine(ServerFolder(workspace), "Config", "akka.conf")));

        // A file that only CoreLib's own folder holds was deployed too: all three build folders are the
        // source of the server, not just the runnable tree.
        Assert.Equal(
            "native",
            File.ReadAllText(Path.Combine(ServerFolder(workspace), "runtimes", "win-x64", "native", "corelib-only.dll")));
    }

    [Fact]
    public async Task ASecondBuildOfTheSameSourceRewritesNothing() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments));

        Assert.True((await Server(runner).InstallAsync(ServerFolder(workspace))).Success);

        var executable = Path.Combine(ServerFolder(workspace), "Imlight.Director.exe");
        var assembly = Path.Combine(ServerFolder(workspace), "Imlight.CoreLib.dll");
        var executableWritten = File.GetLastWriteTimeUtc(executable);
        var assemblyWritten = File.GetLastWriteTimeUtc(assembly);

        var second = await Server(runner).InstallAsync(ServerFolder(workspace));

        // The build put its output where it always does, and the server folder already holds exactly that,
        // so nothing of the build was written again. (The config is the one file an install always owns:
        // it is merged and repaired on every deploy, so it is not claimed to be untouched.)
        Assert.True(second.Success, second.Message);
        Assert.True(second.FilesUnchanged > 0);
        Assert.Contains("already current", second.Message);
        Assert.Equal(executableWritten, File.GetLastWriteTimeUtc(executable));
        Assert.Equal(assemblyWritten, File.GetLastWriteTimeUtc(assembly));
        Assert.Equal("exe:edge", File.ReadAllText(executable));
    }

    [Fact]
    public async Task BuildsWhatIsCompiledOverAServerWhoseSettingsAreKept() {
        using var workspace = new TempWorkspace();
        CreateCheckout();
        workspace.WriteFile(
            Path.Combine("server", "Config", "Imlight.ini"),
            "[Database]\r\nAdminAccountUsername = keeper\r\nAdminAccountPassword = s3cret\r\n");

        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments));

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.True(result.Success, result.Message);
        Assert.True(result.SettingsRetained > 0);

        var installed = ImlightConfig.TryLoad(ServerFolder(workspace));
        Assert.NotNull(installed);
        Assert.Equal("keeper", installed.AdminUsername);
        Assert.Equal("s3cret", installed.AdminPassword);
    }

    [Fact]
    public async Task DoesNotInstallABuildOutputThatHasNoManagedAssembly() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Count > 0 && arguments[0] == "build") {
                Directory.CreateDirectory(DirectorOutput);
                File.WriteAllText(Path.Combine(DirectorOutput, "Imlight.Director.exe"), "edge build");
                return new ToolResult(0, "built", string.Empty);
            }

            return Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.False(result.Success);
        Assert.Contains("Imlight.Director.dll", result.Message);
        Assert.False(Directory.Exists(ServerFolder(workspace)));
    }

    [Fact]
    public async Task SaysWhereTheTypeDumpGoesWhenTheBuildStopsInImcodec() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Count > 0 && arguments[0] == "build") {
                return new ToolResult(1, string.Empty,
                    "C:\\src\\Imcodec.ObjectProperty\\TypeCache\\ServerGeneratedTypeRegistry.cs(65,44): "
                    + "error CS0246: The type or namespace name 'RequirementList' could not be found "
                    + "[C:\\src\\Imcodec.ObjectProperty\\Imcodec.ObjectProperty.csproj]");
            }

            return Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.False(result.Success);
        Assert.Contains("type dump", result.Message);
        Assert.Contains(Path.Combine(LauncherPaths.SourceDirectory, "type-dump.json"), result.Message);
        Assert.False(Directory.Exists(ServerFolder(workspace)));
    }

    [Fact]
    public async Task DoesNotStartTheBuildWhenTheBuildInputsCannotBeMade() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var inputs = new FakeInputs(
            success: false,
            message: "making one needs Python 3.12 or newer. Install Python from https://www.python.org/downloads/");

        var runner = new ScriptedRunner((_, arguments) => Answer(arguments));

        var result = await Server(runner, inputs).InstallAsync(ServerFolder(workspace), workspace.Path("client"));

        Assert.False(result.Success);
        Assert.Contains("Python 3.12", result.Message);
        Assert.DoesNotContain(runner.Calls, call => call.Contains(" build "));
        Assert.False(Directory.Exists(ServerFolder(workspace)));
    }

    [Fact]
    public async Task PassesTheGameFolderToWhateverMakesTheBuildInputs() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var inputs = new FakeInputs();
        var runner = new ScriptedRunner((_, arguments) =>
            arguments.Count > 0 && arguments[0] == "build" ? Build(arguments) : Answer(arguments));

        var result = await Server(runner, inputs).InstallAsync(
            ServerFolder(workspace), workspace.Path("client"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, inputs.Calls);
        Assert.Equal(workspace.Path("client"), inputs.LastGameFolder);
    }

    [Fact]
    public async Task WithoutTheSdkItSaysSoRatherThanFailingInsideTheBuild() {
        using var workspace = new TempWorkspace();
        CreateCheckout();

        var runner = new ScriptedRunner((_, arguments) => {
            if (arguments.Count > 0 && arguments[0] == "--list-sdks") {
                return new ToolResult(0, "8.0.404 [C:\\Program Files\\dotnet\\sdk]", string.Empty);
            }

            return Answer(arguments);
        });

        var result = await Server(runner).InstallAsync(ServerFolder(workspace));

        Assert.False(result.Success);
        Assert.Contains(".NET 10 SDK", result.Message);
        Assert.DoesNotContain(runner.Calls, call => call.Contains(" build "));
    }
}
