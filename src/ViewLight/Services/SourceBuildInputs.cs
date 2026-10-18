using System.Diagnostics;
using System.Globalization;

namespace ViewLight.Services;

/// <summary>What came of preparing Imlight's build inputs.</summary>
/// <param name="Success">Whether both inputs are on disk and in the checkout.</param>
/// <param name="Message">What to say when it failed, or what was done when it worked.</param>
public sealed record SourceBuildInputsResult(bool Success, string Message);

/// <summary>
/// Prepares the two files Imlight's source build needs and the repository does not publish.
/// </summary>
/// <remarks>
/// A test double of this exists because these steps are the ones a machine can be unable to do - Python,
/// a game install, a running client - and the build decisions around them are worth testing without
/// launching Wizard101.
/// </remarks>
public interface ISourceBuildInputs {

    /// <summary>Where the type dump is kept between builds.</summary>
    string TypeDumpPath { get; }

    Task<SourceBuildInputsResult> PrepareAsync(
        string checkout,
        string? gameFolder,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gets the type dump and the message definitions a build of Imlight's source needs, from the game that
/// is installed on this machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two inputs, two different tools.</b> Imlight's <c>Imcodec</c> submodule generates its object-property
/// classes from a WizWalker-style type dump, and its message layer from the game's own
/// <c>*Messages*.xml</c> definitions. Neither is in the repository: the two <c>GeneratorInput</c> folders
/// ship only an empty <c>!base</c> marker. Without both, a publish stops inside <c>Imcodec</c>, which is
/// the wall every public attempt at building Imlight hits.
/// </para>
/// <para>
/// <b>The dump comes from wiztype.</b> It reads the type registry out of a running Wizard101 client and
/// writes the JSON Imcodec's generator wants. It is a Python package, so the launcher creates a virtual
/// environment of its own under its data folder and installs <c>wiztype</c> into that - nothing is
/// installed into the user's Python or its site-packages. The game is started with no account and closed
/// again when the dump is finished if ViewLight started it; a client the user already had open is left
/// alone.
/// </para>
/// <para>
/// <b>Version 2 is the one that works.</b> wiztype's version 1 dump is the old WizWalker shape - a map of
/// classes with no manifest around it - and Imcodec's generator reports "No class definitions were found
/// in the JSON dump manifest" for it. Version 2 is <c>{"version":2,"classes":{...}}</c>, which is what
/// <c>JsonDumpManifest</c> reads. That was measured, not guessed: v1 fails the build and v2 succeeds.
/// </para>
/// <para>
/// <b>The message definitions are already on disk.</b> They are files inside <c>Root.wad</c>, so the
/// launcher extracts every <c>*Messages*.xml</c> with its own KIWAD reader - the same one that names the
/// item templates - and copies them into the message layer's <c>GeneratorInput</c>. No Imcodec CLI, no
/// second tool, and the files are exactly the ones the client itself uses.
/// </para>
/// </remarks>
public sealed class SourceBuildInputs : ISourceBuildInputs {

    /// <summary>The client process wiztype looks for, without its extension.</summary>
    public const string ClientProcessName = "WizardGraphicalClient";

    /// <summary>The file name the object-property generator reads.</summary>
    public const string TypeDumpFileName = "type-dump.json";

    /// <summary>The oldest Python wiztype supports.</summary>
    private const int MinimumPythonMajor = 3;
    private const int MinimumPythonMinor = 12;

    /// <summary>
    /// A dump smaller than this is not a dump. The real one is around 29 MB; this only has to be well
    /// above an error page or an empty <c>{}</c>.
    /// </summary>
    private const long MinimumTypeDumpBytes = 100_000;

    private const int DumpAttempts = 4;
    private static readonly TimeSpan DumpAttemptDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long to give a started client before the first dump attempt.</summary>
    private static readonly TimeSpan ClientStartDelay = TimeSpan.FromSeconds(8);

    private readonly IToolRunner _runner;

    public SourceBuildInputs(IToolRunner? runner = null) => _runner = runner ?? new ToolRunner();

    /// <summary>The type dump, kept beside the checkout so an update does not remove it.</summary>
    public string TypeDumpPath => Path.Combine(LauncherPaths.SourceDirectory, TypeDumpFileName);

    /// <summary>Which client revision the generated dump came from, so a game update is noticed.</summary>
    private string TypeDumpRevisionPath => TypeDumpPath + ".revision";

    /// <summary>ViewLight's own Python environment, so wiztype is never installed system-wide.</summary>
    private string VenvDirectory => Path.Combine(LauncherPaths.SourceDirectory, "wiztype-venv");

    private string VenvPython => Path.Combine(VenvDirectory, "Scripts", "python.exe");

    /// <summary>Where the extracted message definitions wait between extraction and the copy.</summary>
    private string MessageDirectory => Path.Combine(LauncherPaths.SourceDirectory, "message-xml");

    /// <summary>
    /// Makes sure both inputs exist, then puts them where the checkout's projects read them.
    /// </summary>
    public async Task<SourceBuildInputsResult> PrepareAsync(
        string checkout,
        string? gameFolder,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) {
            return Failed(
                "Imlight's build needs your Wizard101 install: it reads the game's type information and "
                + "message definitions from it. Choose the game folder under Settings and build again.");
        }

        var revision = ReadRevision(gameFolder);

        if (NeedsTypeDump(revision)) {
            progress?.Report(new InstallProgress(0.04, "Types", "Preparing the type dumper…"));

            var dump = await GenerateTypeDumpAsync(gameFolder, revision, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!dump.Success) {
                return dump;
            }
        }

        var messages = await ExtractMessageDefinitionsAsync(gameFolder, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!messages.Success) {
            return messages;
        }

        try {
            CopyIntoCheckout(checkout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return Failed($"The build inputs could not be written into the source checkout: {ex.Message}");
        }

        return new SourceBuildInputsResult(
            true, "Type dump and message definitions are in the checkout.");
    }

    // ------------------------------------------------------------------ the type dump

    /// <summary>Whether the dump has to be made, rather than reused.</summary>
    /// <remarks>
    /// A dump somebody placed by hand has no revision sidecar and is used as it is - that is the escape
    /// hatch for a machine that cannot run wiztype. A dump this class generated records the client
    /// revision it came from, so a game update regenerates it instead of quietly building against types
    /// from a client that has moved on.
    /// </remarks>
    private bool NeedsTypeDump(string? revision) {
        if (!File.Exists(TypeDumpPath)) {
            return true;
        }

        if (!File.Exists(TypeDumpRevisionPath) || revision is null) {
            return false;
        }

        var stored = ReadAllTextOrEmpty(TypeDumpRevisionPath).Trim();

        return stored.Length > 0 && !string.Equals(stored, revision, StringComparison.Ordinal);
    }

    private async Task<SourceBuildInputsResult> GenerateTypeDumpAsync(
        string gameFolder,
        string? revision,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        var python = await FindPythonAsync(cancellationToken).ConfigureAwait(false);

        if (python is null) {
            return Failed(
                "Imlight's build needs a type dump, and making one needs Python "
                + $"{MinimumPythonMajor}.{MinimumPythonMinor} or newer. Install Python from "
                + "https://www.python.org/downloads/ and build again - or put a type dump at "
                + $"{TypeDumpPath} if you already have one.");
        }

        progress?.Report(new InstallProgress(0.05, "Types", "Creating ViewLight's Python environment…"));

        var venv = await EnsureVenvAsync(python, cancellationToken).ConfigureAwait(false);
        if (venv is not null) {
            return Failed(venv);
        }

        var wiztype = await EnsureWiztypeAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!wiztype.Success) {
            return wiztype;
        }

        progress?.Report(new InstallProgress(0.07, "Types", "Looking for Wizard101…"));

        var client = await EnsureClientAsync(gameFolder, progress, cancellationToken).ConfigureAwait(false);
        if (client.Process is null) {
            return Failed(client.Problem ?? "Wizard101 could not be started to read its type information.");
        }

        try {
            var work = Path.Combine(LauncherPaths.StagingDirectory, "wiztype");
            Directory.CreateDirectory(work);

            var output = Path.Combine(work, TypeDumpFileName);
            TryDelete(output);

            ToolResult? last = null;

            for (var attempt = 1; attempt <= DumpAttempts; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new InstallProgress(
                    0.08, "Types", $"Reading Wizard101's types ({attempt} of {DumpAttempts})…"));

                last = await _runner
                    .RunAsync(
                        VenvPython,
                        ["-m", "wiztype", "--version", "2", output],
                        work,
                        line => progress?.Report(new InstallProgress(0.08, "Types", Condense(line))),
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (last.Success && IsUsableTypeDump(output)) {
                    break;
                }

                if (IsClosed(client.Process)) {
                    return Failed(
                        "Wizard101 closed while its type information was being read. Start it once, "
                        + "leave it on the login screen, and build again.");
                }

                if (attempt < DumpAttempts) {
                    await Task.Delay(DumpAttemptDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!IsUsableTypeDump(output)) {
                return Failed(
                    "wiztype could not read Wizard101's type information. Leave the game on its login "
                    + "screen and build again"
                    + (last is null ? "." : $": {Describe(last)}"));
            }

            Directory.CreateDirectory(LauncherPaths.SourceDirectory);
            File.Copy(output, TypeDumpPath, overwrite: true);
            File.WriteAllText(TypeDumpRevisionPath, revision ?? string.Empty);

            progress?.Report(new InstallProgress(
                0.10, "Types", "Type information read from Wizard101."));

            return new SourceBuildInputsResult(true, "Type information read from Wizard101.");
        }
        finally {
            if (client.StartedByUs && client.Process is not null) {
                CloseClient(client.Process, progress);
            }

            // A Process handle is a real handle; the dump is over, so neither the borrowed client nor
            // the one just closed needs this object any more.
            client.Process?.Dispose();
        }
    }

    /// <summary>
    /// The first Python 3.12+ on this machine, as a real executable path.
    /// </summary>
    /// <remarks>
    /// Asked for its own path rather than assumed, because <c>py</c> is a launcher and the executable it
    /// chooses is what a virtual environment must be created with. The answer is two lines - the
    /// executable, then its major and minor version - so a Python that will not even start is skipped
    /// rather than turned into a failure.
    /// </remarks>
    private async Task<string?> FindPythonAsync(CancellationToken cancellationToken) {
        Directory.CreateDirectory(LauncherPaths.SourceDirectory);

        string[][] candidates = [["py", "-3"], ["python"], ["python3"]];

        foreach (var candidate in candidates) {
            var file = candidate[0];
            var prefix = candidate.Skip(1).ToArray();

            var probe = await _runner
                .RunAsync(
                    file,
                    [
                        .. prefix,
                        "-c",
                        "import sys; print(sys.executable); print(f'{sys.version_info[0]}.{sys.version_info[1]}')",
                    ],
                    LauncherPaths.SourceDirectory,
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!probe.Success) {
                continue;
            }

            var lines = probe.Output
                .Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length < 2 || !TryParseMajorMinor(lines[^1], out var major, out var minor)) {
                continue;
            }

            if (major > MinimumPythonMajor
                || (major == MinimumPythonMajor && minor >= MinimumPythonMinor)) {
                return lines[^2];
            }
        }

        return null;
    }

    private static bool TryParseMajorMinor(string text, out int major, out int minor) {
        major = 0;
        minor = 0;

        var parts = text.Split('.');

        return parts.Length >= 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>Creates the private environment, or says why it could not.</summary>
    private async Task<string?> EnsureVenvAsync(string python, CancellationToken cancellationToken) {
        if (File.Exists(VenvPython)) {
            return null;
        }

        // A half-made environment is this launcher's own mess, and `venv` refuses to write into a folder
        // it does not recognise. Starting over is cheaper than diagnosing it.
        TryDeleteDirectory(VenvDirectory);

        var created = await _runner
            .RunAsync(python, ["-m", "venv", VenvDirectory], LauncherPaths.SourceDirectory, null, null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!created.Success || !File.Exists(VenvPython)) {
            return "ViewLight could not create a Python environment for wiztype. " + Describe(created);
        }

        return null;
    }

    /// <summary>Installs wiztype into the private environment when it is not there yet.</summary>
    private async Task<SourceBuildInputsResult> EnsureWiztypeAsync(
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken) {
        var probe = await _runner
            .RunAsync(VenvPython, ["-m", "wiztype", "--help"], LauncherPaths.SourceDirectory, null, null,
                cancellationToken)
            .ConfigureAwait(false);

        if (probe.Success) {
            return new SourceBuildInputsResult(true, string.Empty);
        }

        progress?.Report(new InstallProgress(0.06, "Types", "Installing wiztype (about 10 MB)…"));

        var install = await _runner
            .RunAsync(
                VenvPython,
                ["-m", "pip", "install", "--disable-pip-version-check", "--quiet", "wiztype"],
                LauncherPaths.SourceDirectory,
                line => progress?.Report(new InstallProgress(0.06, "Types", Condense(line))),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!install.Success) {
            return Failed(
                "wiztype could not be installed, so the type dump cannot be made. Check the internet "
                + $"connection and build again. {Describe(install)}");
        }

        var check = await _runner
            .RunAsync(VenvPython, ["-m", "wiztype", "--help"], LauncherPaths.SourceDirectory, null, null,
                cancellationToken)
            .ConfigureAwait(false);

        return check.Success
            ? new SourceBuildInputsResult(true, string.Empty)
            : Failed(
                "wiztype was installed but cannot be started. " + Describe(check)
                + $" A type dump can be put at {TypeDumpPath} instead.");
    }

    // ------------------------------------------------------------------ the client

    /// <summary>The client to dump from, whether it was already running or had to be started.</summary>
    /// <param name="Process">The client process, or <c>null</c> when it could not be started.</param>
    /// <param name="StartedByUs">Whether the launcher owns this process and may close it afterwards.</param>
    /// <param name="Problem">Why there is no process.</param>
    private sealed record ClientAttempt(Process? Process, bool StartedByUs, string? Problem);

    private async Task<ClientAttempt> EnsureClientAsync(
        string gameFolder,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        var existing = Process.GetProcessesByName(ClientProcessName).FirstOrDefault();

        if (existing is not null) {
            return new ClientAttempt(existing, StartedByUs: false, Problem: null);
        }

        var executable = ClientLauncher.FindExecutable(gameFolder);

        if (executable is null) {
            return new ClientAttempt(
                null, false,
                "The game folder does not contain bin\\WizardGraphicalClient.exe, so there is nothing to "
                + "read type information from. Choose the folder holding bin\\ and Data\\ and build again.");
        }

        progress?.Report(new InstallProgress(
            0.07, "Types", "Starting Wizard101 once to read its type information…"));

        Process? started;

        try {
            // The same arguments Play uses, minus the account: a client at its login screen has loaded
            // its registry, and that is all wiztype needs. The server does not have to be running.
            started = Process.Start(ClientLauncher.BuildStartInfo(executable, "127.0.0.1", 12000));
        }
        catch (Exception ex) {
            return new ClientAttempt(null, false, $"Wizard101 could not be started: {ex.Message}");
        }

        if (started is null) {
            return new ClientAttempt(null, false, "Wizard101 could not be started.");
        }

        try {
            await Task.Delay(ClientStartDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            CloseClient(started, progress);
            started.Dispose();
            throw;
        }

        if (IsClosed(started)) {
            started.Dispose();
            return new ClientAttempt(
                null, false,
                "Wizard101 started and closed again before its types could be read. Start it once, leave "
                + "it on the login screen, and build again.");
        }

        return new ClientAttempt(started, StartedByUs: true, Problem: null);
    }

    private static bool IsClosed(Process process) {
        try {
            return process.HasExited;
        }
        catch (InvalidOperationException) {
            return true;
        }
    }

    /// <summary>
    /// Closes a client the launcher started, and only one it started.
    /// </summary>
    /// <remarks>
    /// A game somebody was already playing is never touched - that is the difference between borrowing
    /// the type registry and taking over the machine. The kill is not recursive on purpose: the game
    /// client is the process that was started, and its launcher's children are not the launcher's to end.
    /// </remarks>
    private static void CloseClient(Process process, IProgress<InstallProgress>? progress) {
        try {
            if (IsClosed(process)) {
                return;
            }

            progress?.Report(new InstallProgress(0.10, "Types", "Closing Wizard101…"));
            process.Kill();
        }
        catch (Exception) {
            // A client that has already gone, or that Windows will not let go, costs nothing here.
        }
    }

    // ------------------------------------------------------------------ the message definitions

    /// <summary>Copies every <c>*Messages*.xml</c> out of the client's <c>Root.wad</c>.</summary>
    private async Task<SourceBuildInputsResult> ExtractMessageDefinitionsAsync(
        string gameFolder,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken) {

        progress?.Report(new InstallProgress(0.11, "Types", "Reading the game's message definitions…"));

        return await Task.Run(() => ExtractMessageDefinitions(gameFolder, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private SourceBuildInputsResult ExtractMessageDefinitions(
        string gameFolder, CancellationToken cancellationToken) {

        var rootWad = Path.Combine(gameFolder, GameFolderLocator.RelativeRootWad);

        if (!File.Exists(rootWad)) {
            return Failed(
                $"The game folder does not contain {GameFolderLocator.RelativeRootWad}, which is where "
                + "Imlight's message definitions are. Choose a complete Wizard101 install and build again.");
        }

        try {
            using var archive = KiWadArchive.Open(rootWad);

            var matches = archive.Entries.Where(entry => IsMessageDefinition(entry.Name)).ToList();

            if (matches.Count == 0) {
                return Failed(
                    "Root.wad contains no *Messages*.xml files, so Imlight's message layer has nothing to "
                    + "generate from. The game install looks incomplete.");
            }

            Directory.CreateDirectory(MessageDirectory);

            foreach (var stale in Directory.GetFiles(MessageDirectory, "*.xml")) {
                File.Delete(stale);
            }

            var written = 0;

            foreach (var entry in matches) {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry.Name);

                if (string.IsNullOrWhiteSpace(name)) {
                    continue;
                }

                File.WriteAllBytes(Path.Combine(MessageDirectory, name), archive.ReadEntry(entry));
                written++;
            }

            return new SourceBuildInputsResult(
                true, $"{written} message definition{(written == 1 ? string.Empty : "s")} read from Root.wad.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            return Failed($"The game's Root.wad could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether an archive entry is one of the message definition files.
    /// </summary>
    /// <remarks>
    /// Matched on the file name rather than the whole path, because the archive keeps some of them in a
    /// <c>Messages/</c> folder and some at its root - and the message layer's project only reads
    /// <c>GeneratorInput\*.xml</c>, so they are copied flat.
    /// </remarks>
    private static bool IsMessageDefinition(string entryName) {
        var name = Path.GetFileName(entryName);

        return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
               && name.Contains("Messages", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the checkout

    /// <summary>Puts both inputs where Imcodec's two generators read them.</summary>
    private void CopyIntoCheckout(string checkout) {
        var objectInput = Path.Combine(
            checkout, "submodule", "Imcodec", "src", "Imcodec.ObjectProperty", "GeneratorInput");
        var messageInput = Path.Combine(
            checkout, "submodule", "Imcodec", "src", "Imcodec.MessageLayer", "GeneratorInput");

        Directory.CreateDirectory(objectInput);
        Directory.CreateDirectory(messageInput);

        File.Copy(TypeDumpPath, Path.Combine(objectInput, "ClientDump.json"), overwrite: true);

        // The project reads every XML in that folder, so one left over from an older client would be
        // compiled into the new build. Clearing first makes the set exactly the game's current one.
        foreach (var stale in Directory.GetFiles(messageInput, "*.xml")) {
            File.Delete(stale);
        }

        foreach (var file in Directory.GetFiles(MessageDirectory, "*.xml")) {
            File.Copy(file, Path.Combine(messageInput, Path.GetFileName(file)), overwrite: true);
        }
    }

    // ------------------------------------------------------------------ small things

    private static string? ReadRevision(string gameFolder) {
        var path = Path.Combine(gameFolder, "bin", "revision.dat");

        try {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static string ReadAllTextOrEmpty(string path) {
        try {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return string.Empty;
        }
    }

    /// <summary>Whether a file looks like the JSON dump the generator can read.</summary>
    private static bool IsUsableTypeDump(string path) {
        try {
            var file = new FileInfo(path);

            if (!file.Exists || file.Length < MinimumTypeDumpBytes) {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);

            var buffer = new char[4096];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read);

            return head.Contains("\"classes\"", StringComparison.Ordinal)
                   && head.TrimStart().StartsWith('{');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
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

    private static string Condense(string line) {
        var trimmed = line.Trim();
        trimmed = trimmed[(trimmed.LastIndexOf('\r') + 1)..];

        return trimmed.Length <= 220 ? trimmed : trimmed[..217] + "…";
    }

    private static SourceBuildInputsResult Failed(string message) => new(false, message);

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A stale output file is replaced by wiztype anyway.
        }
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A partial environment is replaced on the next attempt.
        }
    }
}
