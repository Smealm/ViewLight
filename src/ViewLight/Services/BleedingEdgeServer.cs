using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ViewLight.Services;

/// <summary>The result of running one tool - git, or dotnet - and the last of what it said.</summary>
/// <param name="ExitCode">The process's exit code; 0 is success.</param>
/// <param name="Output">The last few hundred lines of standard output.</param>
/// <param name="Error">The last few hundred lines of standard error.</param>
public sealed record ToolResult(int ExitCode, string Output, string Error) {

    public bool Success => ExitCode == 0;

    /// <summary>Everything the tool said, for a sentence that has to quote it.</summary>
    public string Combined => string.Join(Environment.NewLine,
        new[] { Output, Error }.Where(part => part.Length > 0));
}

/// <summary>Runs a command-line tool and hands back what it said.</summary>
/// <remarks>
/// An interface because the decisions worth testing - when to merge, when not to, and what a failed
/// build means - are about the sequence of git commands rather than about git itself. The default
/// implementation, <see cref="ToolRunner"/>, is the only one a launcher on a real machine uses.
/// </remarks>
public interface IToolRunner {

    Task<ToolResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs a real process, forwarding its lines and keeping the tail of them.</summary>
/// <remarks>
/// Both streams are read asynchronously and a line callback sees both, because git writes its progress
/// to standard error and its answers to standard output - a caller that only watched stdout would see a
/// clone sit silently for a minute. The captured tail is bounded: a restore or a publish can print
/// thousands of lines, and only the end of it is what says what went wrong.
/// </remarks>
public sealed class ToolRunner : IToolRunner {

    /// <summary>How many lines of each stream to keep. Enough for a stack of errors, not a build log.</summary>
    private const int KeptLines = 400;

    public async Task<ToolResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default) {

        var start = new ProcessStartInfo(fileName) {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null) {
            foreach (var (key, value) in environment) {
                start.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        var output = new LineBuffer(KeptLines);
        var error = new LineBuffer(KeptLines);

        process.OutputDataReceived += (_, e) => Accept(e.Data, output, onLine);
        process.ErrorDataReceived += (_, e) => Accept(e.Data, error, onLine);

        try {
            if (!process.Start()) {
                return new ToolResult(127, string.Empty, $"{fileName} could not be started.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
            // The usual one is a tool that is not installed at all: the caller says what to do about it,
            // and this is the sentence it has to work with.
            return new ToolResult(127, string.Empty, $"{fileName} could not be started ({ex.Message}).");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            TryKill(process);
            throw;
        }

        // The asynchronous readers deliver their last lines just after the process exits; this waits for
        // them, so a failure message is not lost by a race with disposal.
        process.WaitForExit();

        return new ToolResult(process.ExitCode, output.Text, error.Text);
    }

    private static void Accept(string? line, LineBuffer buffer, Action<string>? onLine) {
        if (line is null) {
            return;
        }

        buffer.Add(line);

        if (line.Trim().Length > 0) {
            onLine?.Invoke(line);
        }
    }

    private static void TryKill(Process process) {
        try {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception) {
            // It may have finished between the cancel and the kill: nothing to do either way.
        }
    }

    /// <summary>The last however-many lines of one stream, joined back up.</summary>
    private sealed class LineBuffer(int capacity) {

        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public void Add(string line) {
            lock (_gate) {
                _lines.Enqueue(line);

                while (_lines.Count > capacity) {
                    _lines.Dequeue();
                }
            }
        }

        public string Text {
            get {
                lock (_gate) {
                    return string.Join(Environment.NewLine, _lines);
                }
            }
        }
    }
}

/// <summary>Where the bleeding-edge checkout is, and what the last look at it found.</summary>
/// <param name="IsCloned">Whether there is a checkout at all.</param>
/// <param name="Commit">The full commit the checkout is on.</param>
/// <param name="ShortCommit">The same commit, abbreviated the way the tag uses it.</param>
/// <param name="Subject">The commit subject, which is what makes a hash mean something.</param>
/// <param name="CommittedAt">When that commit was made, when git says.</param>
/// <param name="Behind">How many commits GitHub has that the checkout does not.</param>
/// <param name="HasLocalChanges">Whether the working tree has edits that were never committed.</param>
/// <param name="Problem">What went wrong asking, when something did.</param>
public sealed record EdgeSourceState(
    bool IsCloned,
    string Commit,
    string ShortCommit,
    string Subject,
    DateTimeOffset? CommittedAt,
    int Behind,
    bool HasLocalChanges,
    string? Problem) {

    public static EdgeSourceState NotCloned() => new(false, string.Empty, string.Empty, string.Empty, null, 0, false, null);

    /// <summary>Whether the checkout is the newest source GitHub has.</summary>
    public bool IsUpToDate => IsCloned && Behind == 0 && Problem is null;

    /// <summary>What the checkout is, in one line, for a card that has room for one.</summary>
    public string Summary {
        get {
            if (!IsCloned) {
                return "Not cloned yet.";
            }

            var head = $"{ShortCommit} · {Subject}";

            if (Problem is { Length: > 0 }) {
                return $"{head} (could not check for newer source)";
            }

            if (Behind > 0) {
                return $"{head} · {Behind} newer commit{(Behind == 1 ? string.Empty : "s")} on GitHub";
            }

            return HasLocalChanges ? $"{head} · local changes" : head;
        }
    }
}

/// <summary>The result of building Imlight from source and installing it.</summary>
/// <remarks>
/// Deliberately the same shape as an <see cref="InstallResult"/> plus the commit, because everything the
/// caller does afterwards - recording the build, reading the config again, restarting the databases - is
/// the same either way, and a second kind of result would only be a second set of branches.
/// </remarks>
public sealed record EdgeInstallResult(
    bool Success,
    string Tag,
    string Commit,
    string Message,
    string? Warning = null,
    int FilesWritten = 0,
    int SettingsRetained = 0,
    int? EmbeddedDatabasePort = null,
    string? PatchServerUrl = null,
    int FilesUnchanged = 0);

/// <summary>
/// The bleeding-edge channel: Imlight's current source, cloned, built on this machine and installed.
/// </summary>
/// <remarks>
/// <para>
/// This is not a release and is not pretending to be one. Every other install path downloads a build
/// somebody else made; this one is the only thing that can run a fix or a feature that has been committed
/// and not yet built - and the only one that can fail on a machine which cannot build .NET projects.
/// </para>
/// <para>
/// <b>The update process is git's, not a download's.</b> The checkout lives under the launcher's own data
/// folder, so the second build is a <c>git fetch</c> and a merge rather than another clone. A working
/// tree with uncommitted edits is never merged into: it is built exactly as it is, with a warning, because
/// throwing away somebody's edits to fetch a commit is the one thing an updater must not do. Local commits
/// are merged with GitHub's normally, and a conflict aborts the merge and leaves the tree as it was.
/// </para>
/// <para>
/// <b>What is built is not self-contained before it starts.</b> Imlight's <c>Imcodec</c> submodule
/// generates its game types from a WizWalker type dump, and its messages from the game's own XML
/// definitions; neither is published in the repository, and without them the build stops inside
/// <c>Imcodec</c>. <see cref="SourceBuildInputs"/> makes both from the installed game - wiztype for the
/// dump, and the launcher's KIWAD reader for the messages - and this class refuses to start the publish
/// when it cannot, so the failure is one sentence about Python or the game folder instead of a hundred
/// <c>CS0246</c> errors. A dump placed at <see cref="TypeDumpPath"/> by hand is still honoured.
/// </para>
/// </remarks>
public sealed class BleedingEdgeServer {

    public const string RepositoryUrl = "https://github.com/Revive101/Imlight";

    /// <summary>The folder the checkout is cloned into, inside <see cref="LauncherPaths.SourceDirectory"/>.</summary>
    public const string CheckoutFolderName = "imlight";

    /// <summary>The prefix every tag this channel writes carries, so it is never mistaken for a release.</summary>
    public const string TagPrefix = "edge-";

    /// <summary>The project in the checkout that becomes the server.</summary>
    private const string DirectorProject = "src/Imlight.Director/Imlight.Director.csproj";

    /// <summary>
    /// Where the three projects put what they build when Director is built self-contained for win-x64.
    /// </summary>
    /// <remarks>
    /// Director's own RID folder is the runnable tree - the host, the managed assemblies, the runtime and
    /// every package - and the two libraries are beside it in their own framework folders. The deploy reads
    /// these directly; nothing is copied into a staging folder first.
    /// </remarks>
    private const string DirectorOutputRelative = "src/Imlight.Director/bin/Release/net10.0/win-x64";
    private const string CoreLibOutputRelative = "src/Imlight.CoreLib/bin/Release/net10.0";
    private const string CommonOutputRelative = "src/Imlight.Common/bin/Release/net10.0";

    private static readonly IReadOnlyDictionary<string, string> s_gitEnvironment =
        new Dictionary<string, string> {
            // A public repository needs no credentials; this only stops a wrong remote from waiting on a
            // prompt nobody can see behind a launcher window.
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };

    private static readonly IReadOnlyDictionary<string, string> s_dotnetEnvironment =
        new Dictionary<string, string> {
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

    private readonly ServerInstallService _install;
    private readonly IToolRunner _runner;
    private readonly ISourceBuildInputs _inputs;

    public BleedingEdgeServer(
        ServerInstallService? install = null,
        IToolRunner? runner = null,
        ISourceBuildInputs? inputs = null) {
        _install = install ?? new ServerInstallService();
        _runner = runner ?? new ToolRunner();
        _inputs = inputs ?? new SourceBuildInputs(_runner);
    }

    /// <summary>The checkout's own folder.</summary>
    public string SourceDirectory => Path.Combine(LauncherPaths.SourceDirectory, CheckoutFolderName);

    /// <summary>
    /// The type dump the build needs: made from the game when it is missing, and kept between builds.
    /// </summary>
    /// <remarks>
    /// Beside the checkout rather than inside it, because the checkout is git's and this file is not:
    /// outside it, an update cannot even notice the file, and a fresh clone does not take it away.
    /// </remarks>
    public string TypeDumpPath => _inputs.TypeDumpPath;

    /// <summary>Whether there is a checkout to read.</summary>
    public bool IsCloned => Directory.Exists(Path.Combine(SourceDirectory, ".git"));

    /// <summary>How a commit is named as an installed tag.</summary>
    public static string TagFor(string shortCommit) => TagPrefix + shortCommit;

    /// <summary>Whether a tag names a build from source rather than a published release.</summary>
    public static bool IsEdgeTag(string? tag) => tag is { Length: > 0 } && tag.StartsWith(TagPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Looks at the checkout and asks GitHub whether it is behind - without changing anything.
    /// </summary>
    /// <exception cref="OperationCanceledException">The check was cancelled.</exception>
    public async Task<EdgeSourceState> CheckAsync(CancellationToken cancellationToken = default) {
        if (!IsCloned) {
            Directory.CreateDirectory(LauncherPaths.SourceDirectory);

            // There is nothing to read yet, but there is a question worth answering before the button is
            // pressed: whether this machine has git at all. A launcher that says so on the card is better
            // than one that spends a button press finding out.
            var git = await GitAsync(["--version"], null, cancellationToken, LauncherPaths.SourceDirectory)
                .ConfigureAwait(false);

            return git.Success
                ? EdgeSourceState.NotCloned()
                : EdgeSourceState.NotCloned() with {
                    Problem = "A build from source needs git, and it was not found on this machine. "
                              + Describe(git),
                };
        }

        var local = await ReadLocalStateAsync(cancellationToken).ConfigureAwait(false);

        if (local.Problem is not null) {
            return local;
        }

        var fetched = await GitAsync(["fetch", "--prune", "--quiet", "origin"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!fetched.Success) {
            return local with {
                Problem = "Git could not reach GitHub: " + Describe(fetched),
            };
        }

        return local with { Behind = await CountBehindAsync(cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// Clones the source if it is not there, brings it up to date if it is, builds it, and installs it.
    /// </summary>
    /// <param name="serverPath">The server folder to write over, keeping its settings.</param>
    /// <param name="gameFolder">
    /// The Wizard101 install to read the build's type dump and message definitions from. When it is not
    /// given, the inputs are expected at <see cref="TypeDumpPath"/> already.
    /// </param>
    /// <param name="progress">Where to report progress.</param>
    /// <param name="beforeWriting">
    /// Called once, immediately before the first file is written - after the clone, the build and the disk
    /// space have all been spent, so a build that fails costs nothing that was running.
    /// </param>
    /// <param name="cancellationToken">Cancels the clone, the build, or the write.</param>
    public async Task<EdgeInstallResult> InstallAsync(
        string serverPath,
        string? gameFolder = null,
        IProgress<InstallProgress>? progress = null,
        Func<Task>? beforeWriting = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(serverPath)) {
            return Failed(string.Empty, string.Empty, "No server folder is configured.");
        }

        var warning = default(string);
        var tag = string.Empty;
        var commit = string.Empty;

        try {
            Directory.CreateDirectory(LauncherPaths.SourceDirectory);

            if (!IsCloned) {
                progress?.Report(new InstallProgress(0.02, "Source", "Cloning Imlight from GitHub…"));

                var clone = await _runner.RunAsync(
                        "git",
                        ["clone", "--recurse-submodules", RepositoryUrl, CheckoutFolderName],
                        LauncherPaths.SourceDirectory,
                        line => progress?.Report(new InstallProgress(0.04, "Source", Condense(line))),
                        s_gitEnvironment,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!clone.Success) {
                    return Failed(string.Empty, string.Empty,
                        "The source could not be cloned. " + Describe(clone)
                        + " Git must be installed and able to reach GitHub.");
                }
            }
            else {
                warning = await SyncAsync(progress, cancellationToken).ConfigureAwait(false);
            }

            var state = await ReadLocalStateAsync(cancellationToken).ConfigureAwait(false);

            if (state.Commit.Length == 0) {
                return Failed(string.Empty, string.Empty,
                    state.Problem ?? "The source checkout could not be read, so there was nothing to build.");
            }

            tag = TagFor(state.ShortCommit);
            commit = state.Commit;

            var dotnet = DotnetHost.Find() ?? "dotnet";
            var sdk = await CheckSdkAsync(dotnet, cancellationToken).ConfigureAwait(false);

            if (sdk is not null) {
                return Failed(tag, commit, sdk);
            }

            // The build is not self-contained, and the two files it is missing both come from the game.
            // Done before the build folder is created, so an impossible build leaves no half-made folder.
            var inputs = await _inputs
                .PrepareAsync(SourceDirectory, gameFolder, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!inputs.Success) {
                return Failed(tag, commit, "The build did not start. " + inputs.Message);
            }

            progress?.Report(new InstallProgress(0.14, "Build", $"Building {state.ShortCommit}…"));

            // Built the way the checkout builds itself, with the output left where the projects put it:
            // Director self-contained under its own bin\Release\net10.0\win-x64, and CoreLib and Common
            // beside them in their own net10.0 folders. Nothing is copied to a staging folder first - the
            // deploy reads these three directly and writes only the files the server does not already have.
            var build = await _runner.RunAsync(
                    dotnet,
                    [
                        "build", DirectorProject,
                        "-c", "Release",
                        "-r", "win-x64",
                        "--self-contained", "true",
                        "--nologo",
                    ],
                    SourceDirectory,
                    line => {
                        if (IsWorthShowing(line)) {
                            progress?.Report(new InstallProgress(0.30, "Build", Condense(line)));
                        }
                    },
                    s_dotnetEnvironment,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!build.Success) {
                return Failed(tag, commit, DescribeBuildFailure(build), warning);
            }

            var directorOutput = OutputDirectory(DirectorOutputRelative);

            // The three projects' own output folders, in deploy order: Director first because it is the
            // runnable tree, then the two libraries that are part of it. A folder that is not there is
            // skipped rather than guessed at.
            var roots = new[] {
                directorOutput,
                OutputDirectory(CoreLibOutputRelative),
                OutputDirectory(CommonOutputRelative),
            }.Where(Directory.Exists).ToList();

            // The names that make the three folders a server. Checked before anything is written, so a
            // build that reported success while producing a partial tree fails here, in one sentence,
            // instead of being deployed as a server that cannot start.
            var missing = new[] {
                ImlightConfig.ServerExecutableName,
                "Imlight.Director.dll",
                "Imlight.CoreLib.dll",
                "Imlight.Common.dll",
            }.FirstOrDefault(file => !File.Exists(Path.Combine(directorOutput, file)));

            if (missing is not null) {
                return Failed(tag, commit,
                    $"The build finished, but {missing} is not in {directorOutput}, so there was nothing to "
                    + "install. Check the build's own output in the Log.", warning);
            }

            progress?.Report(new InstallProgress(0.86, "Deploy",
                $"Built {state.ShortCommit}; deploying from {directorOutput}"));

            var install = await _install
                .InstallFromFoldersAsync(roots, tag, serverPath, progress, beforeWriting, cancellationToken)
                .ConfigureAwait(false);

            return new EdgeInstallResult(
                install.Success, tag, commit, install.Message, install.Warning ?? warning,
                install.FilesWritten, install.SettingsRetained,
                install.EmbeddedDatabasePort, install.PatchServerUrl, install.FilesUnchanged);
        }
        catch (OperationCanceledException) {
            return Failed(tag, commit, "Build cancelled.");
        }
        catch (Exception ex) {
            return Failed(tag, commit, $"Build failed: {ex.Message}", warning);
        }
    }

    /// <summary>
    /// One of the projects' own output folders, under this build's checkout.
    /// </summary>
    private string OutputDirectory(string relative)
        => Path.Combine(SourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------------ git

    /// <summary>
    /// Brings the checkout up to date, and says what it did not do.
    /// </summary>
    /// <returns>
    /// A warning for the Log when the update was skipped on purpose, or <c>null</c> when it ran.
    /// </returns>
    /// <remarks>
    /// Uncommitted changes are the one thing this will not go around. They are somebody's work, they are
    /// not recoverable from git, and merging over them is how an updater loses a fix that was being tried
    /// out - so the build uses the tree as it is and says so. Everything else is a normal fetch and merge.
    /// </remarks>
    private async Task<string?> SyncAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken) {
        var status = await GitAsync(["status", "--porcelain"], null, cancellationToken).ConfigureAwait(false);

        if (!status.Success || status.Output.Trim().Length > 0) {
            return "The source checkout has uncommitted changes, so it was built as it is rather than updated.";
        }

        progress?.Report(new InstallProgress(0.06, "Source", "Checking for newer source…"));

        var fetch = await GitAsync(["fetch", "--prune", "--quiet", "origin"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!fetch.Success) {
            return $"Git could not check for newer source ({Describe(fetch)}), so the build used what was already checked out.";
        }

        var behind = await CountBehindAsync(cancellationToken).ConfigureAwait(false);

        if (behind <= 0) {
            progress?.Report(new InstallProgress(0.10, "Source", "The checkout is already the newest source."));
            return null;
        }

        progress?.Report(new InstallProgress(
            0.10, "Source", $"Bringing in {behind} new commit{(behind == 1 ? string.Empty : "s")}…"));

        // Fast-forward first, because that is the ordinary case and it keeps the local branch exactly the
        // shape GitHub has. A local commit of the user's own is the case the second merge is for.
        var merge = await GitAsync(Identity("merge", "--ff-only", "@{u}"), null, cancellationToken)
            .ConfigureAwait(false);

        if (!merge.Success) {
            merge = await GitAsync(Identity("merge", "--no-edit", "@{u}"), null, cancellationToken)
                .ConfigureAwait(false);

            if (!merge.Success) {
                await GitAsync(["merge", "--abort"], null, cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    "GitHub's source could not be merged into the local commits without a conflict. "
                    + $"The checkout was left as it was; resolve it in {SourceDirectory} and build again. "
                    + Describe(merge));
            }
        }

        // The submodule pointer moves with the source, and the build reads the submodule rather than a
        // package - so an update that did not update it would compile yesterday's Imcodec.
        await GitAsync(["submodule", "update", "--init", "--recursive", "--quiet"], null, cancellationToken)
            .ConfigureAwait(false);

        return null;
    }

    /// <summary>The arguments for a merge, with an identity that does not need one configured.</summary>
    /// <remarks>
    /// A merge commit wants an author, and a machine that has never used git has none - which would turn
    /// "there is a newer commit" into a failure about user.email. ViewLight signs the merge it made.
    /// </remarks>
    private static IReadOnlyList<string> Identity(params string[] arguments)
        => ["-c", "user.name=ViewLight", "-c", "user.email=viewlight@localhost", .. arguments];

    private async Task<int> CountBehindAsync(CancellationToken cancellationToken) {
        var upstream = await GitAsync(["rev-parse", "--abbrev-ref", "@{u}"], null, cancellationToken)
            .ConfigureAwait(false);

        var reference = upstream.Success ? "@{u}" : "origin/HEAD";

        var count = await GitAsync(["rev-list", "--count", $"HEAD..{reference}"], null, cancellationToken)
            .ConfigureAwait(false);

        return count.Success && int.TryParse(count.Output.Trim(), out var behind) ? behind : 0;
    }

    /// <summary>Reads what the checkout is, without asking GitHub anything.</summary>
    private async Task<EdgeSourceState> ReadLocalStateAsync(CancellationToken cancellationToken) {
        var commit = await GitAsync(["rev-parse", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        var shortCommit = await GitAsync(["rev-parse", "--short", "HEAD"], null, cancellationToken).ConfigureAwait(false);

        if (!commit.Success || !shortCommit.Success) {
            return EdgeSourceState.NotCloned() with {
                Problem = "Git could not read the source checkout. " + Describe(commit.Success ? shortCommit : commit),
            };
        }

        var subject = await GitAsync(["log", "-1", "--format=%s"], null, cancellationToken).ConfigureAwait(false);
        var date = await GitAsync(["log", "-1", "--format=%cI"], null, cancellationToken).ConfigureAwait(false);
        var status = await GitAsync(["status", "--porcelain"], null, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? committedAt = null;
        if (date.Success
            && DateTimeOffset.TryParse(date.Output.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)) {
            committedAt = parsed;
        }

        return new EdgeSourceState(
            IsCloned: true,
            Commit: commit.Output.Trim(),
            ShortCommit: shortCommit.Output.Trim(),
            Subject: subject.Success ? subject.Output.Trim() : string.Empty,
            CommittedAt: committedAt,
            Behind: 0,
            HasLocalChanges: status.Success && status.Output.Trim().Length > 0,
            Problem: null);
    }

    private Task<ToolResult> GitAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
        => _runner.RunAsync("git", arguments, workingDirectory ?? SourceDirectory, onLine, s_gitEnvironment,
            cancellationToken);

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Why the machine cannot build the project, or <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    /// Asked before the publish rather than after it, because the answer is a sentence somebody can act
    /// on - "the SDK, not just the runtime" - and the publish's own answer would be a wall of text about
    /// a target that does not exist. The major version is read from the project rather than written down
    /// here, so a project that moves to a newer framework is not silently held back by this check.
    /// </remarks>
    private async Task<string?> CheckSdkAsync(string dotnet, CancellationToken cancellationToken) {
        var sdks = await _runner
            .RunAsync(dotnet, ["--list-sdks"], SourceDirectory, null, s_dotnetEnvironment, cancellationToken)
            .ConfigureAwait(false);

        if (!sdks.Success) {
            return "Building Imlight needs the .NET SDK, and no dotnet host was found. Install the "
                   + $".NET {RequiredMajorVersion()} SDK (the launcher ships only the runtime) and try again.";
        }

        var wanted = RequiredMajorVersion();

        var hasIt = sdks.Output
            .Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(words => words.Length > 0
                          && int.TryParse(words[0].Split('.')[0], out var major)
                          && major >= wanted);

        return hasIt
            ? null
            : $"Building Imlight needs the .NET {wanted} SDK, and this machine has {DescribeSdks(sdks.Output)}. "
              + "Install the SDK (the launcher ships only the runtime) and try again.";
    }

    /// <summary>The .NET major version the Director project targets, read from the project itself.</summary>
    private int RequiredMajorVersion() {
        try {
            var project = Path.Combine(SourceDirectory, DirectorProject.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(project);
            var match = Regex.Match(text, @"<TargetFramework>\s*net(\d+)\.");

            if (match.Success && int.TryParse(match.Groups[1].Value, out var major)) {
                return major;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            // An unreadable project is the publish's to report; this is only the version check.
        }

        return 10;
    }

    private static string DescribeSdks(string listing) {
        var sdks = listing
            .Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(version => version is { Length: > 0 })
            .ToArray();

        return sdks.Length == 0 ? "none" : string.Join(", ", sdks);
    }

    /// <summary>
    /// The lines worth one line on the progress bar, rather than every restore message.
    /// </summary>
    /// <remarks>
    /// Compiler errors are deliberately not among them: a failed build emits hundreds in a burst, and
    /// forwarding each one turns the status line into a blur that ends on an arbitrary error. The end of
    /// the build is where they are summarised - <see cref="DescribeBuildFailure"/> picks the ones that
    /// name the cause - so the progress bar stays about which project is being built.
    /// </remarks>
    private static bool IsWorthShowing(string line) {
        var trimmed = line.Trim();

        return trimmed.StartsWith("Determining projects", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains(" -> ", StringComparison.Ordinal);
    }

    /// <summary>
    /// What a failed build should say, in a sentence a person can use.
    /// </summary>
    /// <remarks>
    /// The <c>CS0246</c>/<c>CS0115</c> pair inside <c>Imcodec.ObjectProperty</c> is the signature of the
    /// one failure every public attempt hits: the generated game types are missing because their input is
    /// not in the repository. Naming that, and where to put the file, is worth more than the compiler's
    /// own hundred lines.
    /// </remarks>
    private string DescribeBuildFailure(ToolResult publish) {
        var lines = publish.Combined.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var errors = lines
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();

        var text = errors.Length > 0 ? string.Join(" ", errors) : Describe(publish);

        var missingGeneratedTypes =
            lines.Any(line => line.Contains("CS0246", StringComparison.Ordinal)
                              || line.Contains("CS0115", StringComparison.Ordinal))
            && lines.Any(line => line.Contains("Imcodec.ObjectProperty", StringComparison.Ordinal));

        if (!missingGeneratedTypes) {
            return "The source did not build. " + Condense(text);
        }

        return "The source did not build, because Imcodec generates its game types from a WizWalker type "
               + "dump that Imlight does not publish. If the dump ViewLight made is stale, delete "
               + $"{TypeDumpPath} (and its .revision file) and build again; a dump you made yourself can "
               + $"be put at that path instead. " + Condense(text);
    }

    private static string Describe(ToolResult result) {
        var text = result.Combined.Trim();

        if (text.Length == 0) {
            return $"It exited with code {result.ExitCode} and said nothing.";
        }

        var line = text.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? text;

        return Condense(line);
    }

    /// <summary>One line, short enough for a status bar, that still says what happened.</summary>
    private static string Condense(string line) {
        var trimmed = line.Trim();

        // git's own progress uses carriage returns and a spinner; none of that belongs in a label.
        trimmed = trimmed[(trimmed.LastIndexOf('\r') + 1)..];

        return trimmed.Length <= 220 ? trimmed : trimmed[..217] + "…";
    }

    private static EdgeInstallResult Failed(string tag, string commit, string message, string? warning = null)
        => new(false, tag, commit, message, warning);

    private static void TryDelete(string folder) {
        try {
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Staging leftovers are harmless; the next build clears them again.
        }
    }
}
