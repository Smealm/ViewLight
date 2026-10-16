using System.Diagnostics;
using System.Net.Sockets;

namespace ViewLight.Services;

public enum ServerState {
    Stopped,
    Starting,
    WarmingUp,
    Ready,
    Stopping,
    Faulted,
}

/// <summary>
/// Everything <see cref="ServerRunner"/> needs to launch the server and know when it is ready.
/// </summary>
/// <param name="AccountServerHost">Host of the account/login server the client will connect to.</param>
/// <param name="AccountServerPort">Port of the account/login server; this is the port that is probed.</param>
/// <param name="ReadySettleDelay">
/// How long to keep waiting after the login port opens, for the account server to finish coming up.
/// Only the port probe waits this out - the server's own readiness line is taken at its word. Defaults
/// to <see cref="ServerRunner.DefaultReadySettleDelay"/>.
/// </param>
public sealed record ServerStartOptions(
    string ServerPath,
    string ExecutablePath,
    string AccountServerHost,
    int AccountServerPort,
    string LogFilePath,
    IReadOnlyList<string>? Arguments = null,
    TimeSpan? ReadySettleDelay = null);

/// <summary>
/// Runs <c>Imlight.Director.exe</c> and reports when it is safe to start the client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a port probe and not a ping.</b> Pinging the server's address proves nothing useful: the
/// machine (or the adapter) answers whether or not Imlight is up. What matters is whether the account
/// server is <i>listening</i>, so the launcher opens a TCP connection to the account server's host and
/// port instead.
/// </para>
/// <para>
/// <b>The two signals are not equally certain, so they are not treated alike.</b> The server's own
/// readiness line - <see cref="ReadyMarker"/> - is the server stating that it has finished starting and
/// is about to serve logins, so it is taken at its word and the client is started the moment it appears:
/// waiting after a direct announcement would only delay every session by the settle window for nothing.
/// The port probe is only a guess by comparison - a socket accepts connections slightly before the
/// account server can serve them - so when that is what fired, the launcher holds for
/// <see cref="DefaultReadySettleDelay"/> before declaring the server ready. Either signal is enough on
/// its own, and the log names whichever actually fired.
/// </para>
/// </remarks>
public sealed class ServerRunner : IDisposable {

    /// <summary>The line Imlight logs once its servers are up.</summary>
    public const string ReadyMarker = "Imlight may now be connected to.";

    /// <summary>
    /// Grace period after the login port opens, before the client is allowed to start. Only the port
    /// probe waits it out: the server's own readiness line does not - see the class remarks.
    /// </summary>
    public static readonly TimeSpan DefaultReadySettleDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for a stopped leftover server to let go of the login port.</summary>
    private static readonly TimeSpan PortReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private ServerState _state = ServerState.Stopped;
    private string? _logFilePath;

    /// <summary>
    /// How big the server's log was before this run started. Imlight appends to a daily file, so a
    /// marker left behind by an earlier run today is still in there - and must not be mistaken for this
    /// server being up. Only what this run writes counts.
    /// </summary>
    private long _logStartOffset;

    /// <summary>
    /// The job the server processes are tied to, so Windows stops them when the launcher ends. Null
    /// when the platform would not give us one, in which case stopping the server is back to being a
    /// graceful-shutdown-only affair.
    /// </summary>
    private readonly ProcessJob? _job;

    /// <summary>
    /// Set while the runner is being disposed. The launcher is going away, so a server that ends then
    /// was stopped deliberately and must not be reported as a crash.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>Creates a runner that ties the servers it starts to a job of its own.</summary>
    public ServerRunner() : this(ProcessJob.Create(out _)) {
    }

    /// <summary>
    /// Creates a runner that ties the servers it starts to <paramref name="job"/>.
    /// </summary>
    /// <param name="job">
    /// The job to assign started servers to, or <c>null</c> to run without the tie. The runner disposes
    /// it, which is also what terminates anything still inside it.
    /// </param>
    public ServerRunner(ProcessJob? job) => _job = job;

    public ServerState State => _state;

    public bool IsRunning {
        get {
            var process = _process;
            if (process is null) {
                return false;
            }

            try {
                return !process.HasExited;
            }
            catch (InvalidOperationException) {
                return false;
            }
        }
    }

    public event EventHandler<ServerState>? StateChanged;

    /// <summary>Raw output from the server process, plus launcher commentary about it.</summary>
    public event EventHandler<LogEntry>? Output;

    /// <summary>Raised when the server is ready to be connected to, with the reason.</summary>
    public event EventHandler<string>? Ready;

    /// <summary>Raised when the server process ends by itself, with its exit code.</summary>
    public event EventHandler<int>? Exited;

    public bool HasReadyBeenSignalled { get; private set; }

    /// <summary>Starts the server and begins watching for readiness.</summary>
    public void Start(ServerStartOptions options) {
        if (IsRunning) {
            throw new InvalidOperationException("The server is already running.");
        }

        if (!File.Exists(options.ExecutablePath)) {
            Log(LogLevel.Error, $"Could not find {options.ExecutablePath}.");
            SetState(ServerState.Faulted);
            return;
        }

        HasReadyBeenSignalled = false;

        // A server started earlier can have outlived the launcher and still hold every port the new one
        // needs. Only this install's own executable is stopped - see ServerProcesses.
        var leftover = ServerProcesses.StopAll(options.ExecutablePath, Log);
        if (leftover.Count > 0) {
            WaitForAccountServerPort(options.AccountServerPort);
        }

        // Read before the server starts writing, so this run's output can be told apart from the
        // previous run's.
        _logFilePath = options.LogFilePath;
        _logStartOffset = LogLength(options.LogFilePath);

        var workingDirectory = ResolveWorkingDirectory(options.ServerPath, options.ExecutablePath);
        if (!IsTheServerFolder(workingDirectory, options.ServerPath)) {
            Log(LogLevel.Warning,
                $"{Path.GetFileName(options.ExecutablePath)} lives in {workingDirectory}, not in the server "
                + $"folder {options.ServerPath}. Running it from its own folder, because that is where it "
                + $"looks for {ImlightConfig.RelativeConfigPath.Replace('/', Path.DirectorySeparatorChar)} "
                + "and its logs.");
        }

        Log(LogLevel.Info, $"Starting {Path.GetFileName(options.ExecutablePath)} in {workingDirectory}");
        SetState(ServerState.Starting);

        var startInfo = new ProcessStartInfo {
            FileName = options.ExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in options.Arguments ?? []) {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        process.Exited += (_, _) => HandleExit(process);

        try {
            if (!process.Start()) {
                Log(LogLevel.Error, "The server process could not be started.");
                SetState(ServerState.Faulted);
                return;
            }
        }
        catch (Exception ex) {
            Log(LogLevel.Error, $"The server process could not be started: {ex.Message}");
            SetState(ServerState.Faulted);
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // As early as possible, so the Director's own children (the game realms) are spawned inside the
        // job too and cannot outlive the launcher either.
        TieToJob(process);

        _process = process;
        _cts = new CancellationTokenSource();

        SetState(ServerState.WarmingUp);
        _monitor = MonitorAsync(options, _cts.Token);
    }

    /// <summary>
    /// Puts the started server in the launcher's job object, so ending the launcher ends the server.
    /// </summary>
    /// <remarks>
    /// Best effort by design: a machine that refuses the assignment still gets a working server, it just
    /// loses the guarantee, and the log says so rather than staying quiet about it.
    /// </remarks>
    private void TieToJob(Process process) {
        if (_job is null) {
            Log(LogLevel.Warning,
                "No process job is available, so the server is not tied to ViewLight: if the launcher is "
                + "killed outright rather than closed, the server can keep running and hold its ports.");
            return;
        }

        if (_job.Assign(process)) {
            Log(LogLevel.Info,
                "The server is tied to ViewLight: if the launcher ends, for any reason, the server it "
                + "started is stopped with it.");
        }
        else {
            Log(LogLevel.Warning,
                $"The server could not be tied to ViewLight's process job ({_job.LastError}) so it is only "
                + "stopped on a normal close, not if the launcher is killed.");
        }
    }

    /// <summary>
    /// The directory the server has to be started in: the one holding <c>Imlight.Director.exe</c>.
    /// </summary>
    /// <remarks>
    /// Imlight resolves its relative paths against the directory it is started in, not against its own
    /// location: the stock <c>LogPath = ./logs/log.txt</c> lands in <c>{server}\logs\</c> (checked on a
    /// real install). Taking the directory from the executable rather than from the configured server
    /// folder makes that true by construction, since the managed install keeps the executable in the
    /// server folder, and still does the right thing if the two are ever pointed apart.
    /// </remarks>
    public static string ResolveWorkingDirectory(string serverPath, string executablePath) {
        var full = Path.IsPathRooted(executablePath)
            ? executablePath
            : Path.Combine(serverPath, executablePath);

        return Path.GetDirectoryName(Path.GetFullPath(full)) ?? serverPath;
    }

    private static bool IsTheServerFolder(string workingDirectory, string serverPath) {
        try {
            return string.Equals(Clean(workingDirectory), Clean(serverPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) {
            return false;
        }

        static string Clean(string path)
            => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Waits for the account server's port to come free once a leftover server has been stopped, so the
    /// new process is not beaten to the bind by a socket on its way out.
    /// </summary>
    private void WaitForAccountServerPort(int port) {
        if (port is < 1 or > 65535) {
            return;
        }

        var deadline = DateTimeOffset.Now.Add(PortReleaseTimeout);
        while (DateTimeOffset.Now < deadline) {
            if (PortAllocator.IsAvailable(port)) {
                return;
            }

            Thread.Sleep(250);
        }

        Log(LogLevel.Warning,
            $"Port {port} is still held after stopping the leftover server process, so the new server may "
            + "not be able to use it.");
    }

    private void HandleLine(string? line) {
        if (string.IsNullOrWhiteSpace(line)) {
            return;
        }

        Log(LogLevel.Server, line);
    }

    private async Task MonitorAsync(ServerStartOptions options, CancellationToken cancellationToken) {
        var settleDelay = options.ReadySettleDelay ?? DefaultReadySettleDelay;
        var host = string.IsNullOrWhiteSpace(options.AccountServerHost) ? "127.0.0.1" : options.AccountServerHost;

        Log(LogLevel.Info, $"Waiting for the account server on {host}:{options.AccountServerPort}…");

        Stopwatch? settling = null;
        var settleAnnounced = false;

        // Which signal started the settle window, so the log says what actually happened.
        var trigger = string.Empty;

        while (!cancellationToken.IsCancellationRequested) {
            if (!IsRunning) {
                return;
            }

            // -------- the server's own readiness line, which is the server stating that it has finished
            // starting. It is taken at its word and the client starts at once: this is a fact about the
            // server, where the port below is only a guess that has to be waited out. Checked first, so
            // a port that opened mid-boot cannot make the announcement wait out a window it does not
            // need.
            if (FileShowsReadyMarker(options.LogFilePath)) {
                SignalReady("the server log reports it is up");
                return;
            }

            // -------- the fallback, and the only signal that waits: is the account server listening on
            // its port? A socket accepts connections slightly before the account server can serve them,
            // so this one gets the settle window.
            if (settling is null && await TcpProbeAsync(host, options.AccountServerPort, cancellationToken)
                    .ConfigureAwait(false)) {
                trigger = $"port {options.AccountServerPort} on {host} is open";
                settling = Stopwatch.StartNew();

                Log(LogLevel.Info,
                    $"{trigger}. Waiting {settleDelay.TotalSeconds:0} seconds for the account server to finish starting…");
            }

            if (settling is not null) {
                if (!settleAnnounced) {
                    settleAnnounced = true;
                    SetState(ServerState.WarmingUp);
                }

                if (settling.Elapsed >= settleDelay) {
                    SignalReady($"{trigger} and the {settleDelay.TotalSeconds:0}s settle window has passed");
                    return;
                }
            }

            try {
                await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return;
            }
        }
    }

    /// <summary>
    /// Looks for the readiness line among what this run has written to the server log.
    /// </summary>
    private bool FileShowsReadyMarker(string logFilePath)
        => ReadLogTail(logFilePath, _logStartOffset)?.Contains(ReadyMarker, StringComparison.Ordinal) ?? false;

    /// <summary>The size of the server log, or 0 when there is not one yet.</summary>
    private static long LogLength(string? logFilePath) {
        try {
            return logFilePath is not null && File.Exists(logFilePath)
                ? new FileInfo(logFilePath).Length
                : 0;
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>
    /// The most recent error lines from the server's own log file. Imlight logs its real diagnosis
    /// there rather than to the console, so this is what turns a crash into an explanation.
    /// </summary>
    private static IReadOnlyList<string> ReadRecentLogErrors(string? logFilePath, long startOffset) {
        const int maxLines = 4;

        if (string.IsNullOrWhiteSpace(logFilePath)) {
            return [];
        }

        var tail = ReadLogTail(logFilePath, startOffset);
        if (tail is null) {
            return [];
        }

        var errors = tail
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Contains("[ERR]", StringComparison.Ordinal)
                           || line.Contains("[FTL]", StringComparison.Ordinal))
            .ToList();

        return errors.Count <= maxLines ? errors : errors[^maxLines..];
    }

    /// <summary>
    /// Reads the tail of a log file from <paramref name="startOffset"/> onwards, or <c>null</c> if it
    /// cannot be read. Nothing written before that offset is returned, so an earlier run's output is
    /// never mistaken for this one's.
    /// </summary>
    private static string? ReadLogTail(string logFilePath, long startOffset = 0, int maxBytes = 16 * 1024) {
        try {
            if (!File.Exists(logFilePath)) {
                return null;
            }

            // Imlight keeps writing to this file, so only the tail is worth reading, and the handle
            // must tolerate the server still holding the file open.
            using var stream = new FileStream(
                logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            stream.Seek(Math.Max(startOffset, stream.Length - maxBytes), SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) {
            return null;
        }
        catch (UnauthorizedAccessException) {
            return null;
        }
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken cancellationToken) {
        if (port is < 1 or > 65535) {
            return false;
        }

        try {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));

            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception) {
            // Refused, unreachable, timed out - all mean "not listening yet".
            return false;
        }
    }

    private void SignalReady(string reason) {
        if (HasReadyBeenSignalled) {
            return;
        }

        HasReadyBeenSignalled = true;
        Log(LogLevel.Success, $"Server is ready — {reason}.");
        SetState(ServerState.Ready);
        Ready?.Invoke(this, reason);
    }

    internal void HandleExit(Process process) {
        var exitCode = 0;
        try {
            // With redirected output, the parameterless wait also drains the async readers. Without it
            // the crash message the server printed on its way out can still be in flight when we
            // announce the exit, which hides the one line the user needs.
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException) {
        }

        if (_disposed || State == ServerState.Stopping) {
            return;
        }

        Log(LogLevel.Error, $"The server stopped unexpectedly (exit code {exitCode}).{DescribeExitCode(exitCode)}");

        // Imlight writes its real diagnosis to its own log file, not to the console, and a crash takes
        // the process down before anyone can read it.
        foreach (var line in ReadRecentLogErrors(_logFilePath, _logStartOffset)) {
            Log(LogLevel.Error, $"Server log: {line}");
        }

        SetState(ServerState.Faulted);
        Exited?.Invoke(this, exitCode);
    }

    /// <summary>
    /// Explains an exit code that has a known meaning, or returns an empty string.
    /// </summary>
    /// <remarks>
    /// <c>0xE0434352</c> is not a real exit code: it is the .NET runtime tearing the process down after
    /// an unhandled exception, so a bare "exit code -532462766" says nothing about the cause. Imlight
    /// exits that way when it cannot load <c>Root.wad</c> - which is what an unreachable patch server
    /// causes - so the note points at the error lines above it.
    /// </remarks>
    public static string DescribeExitCode(int exitCode)
        => unchecked((uint)exitCode) switch {
            DotNetUnhandledExceptionCode =>
                " That code (0xE0434352) is the .NET runtime crashing on an unhandled exception: the"
                + " cause is in the error lines above, and usually in the server log lines that follow.",
            _ => string.Empty,
        };

    /// <summary>The exit code the .NET runtime uses after an unhandled exception.</summary>
    public const uint DotNetUnhandledExceptionCode = 0xE0434352;

    /// <summary>Stops the server and its child processes.</summary>
    public async Task StopAsync() {
        var process = _process;
        if (process is null) {
            SetState(ServerState.Stopped);
            return;
        }

        SetState(ServerState.Stopping);
        _cts?.Cancel();

        try {
            if (!process.HasExited) {
                Log(LogLevel.Info, "Stopping the server…");
                process.Kill(entireProcessTree: true);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    Log(LogLevel.Warning, "The server did not shut down within 15 seconds.");
                }
            }
        }
        catch (Exception ex) {
            Log(LogLevel.Warning, $"Stopping the server reported: {ex.Message}");
        }
        finally {
            process.Dispose();
            _process = null;
            _cts?.Dispose();
            _cts = null;
            HasReadyBeenSignalled = false;
            Log(LogLevel.Info, "Server stopped.");
            SetState(ServerState.Stopped);
        }
    }

    private void SetState(ServerState state) {
        if (_state == state) {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Everything this runner reports - the process's output and the launcher's own narration about it -
    /// is tagged as the server's, because that is where somebody quietening one thing down looks for it.
    /// The two are told apart all the same: only <see cref="LogLevel.Server"/> is a line the process
    /// printed, and everything else was written by this runner about it.
    /// </summary>
    private void Log(LogLevel level, string message)
        => Output?.Invoke(this,
            new LogEntry(DateTimeOffset.Now, level, message, LogSource.Server, Derived: level != LogLevel.Server));

    public void Dispose() {
        _disposed = true;

        try {
            _cts?.Cancel();

            var process = _process;
            if (process is { HasExited: false }) {
                // A deliberate stop rather than a crash. Without saying so, the exit handler reports the
                // kill as "the server stopped unexpectedly" on the way out of a normal close.
                SetState(ServerState.Stopping);
                process.Kill(entireProcessTree: true);
            }

            process?.Dispose();
        }
        catch (Exception) {
            // Best effort during shutdown.
        }
        finally {
            _cts?.Dispose();
            _process = null;

            // Closing the job is the backstop: if the kill above was refused, or a child escaped the
            // tree walk, Windows still terminates everything left in the job as this last handle closes.
            _job?.Dispose();

            SetState(ServerState.Stopped);
        }

        GC.SuppressFinalize(this);
    }
}
