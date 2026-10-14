using System.Diagnostics;
using System.Net.Sockets;

namespace ViewLight.Services;

/// <summary>
/// Everything <see cref="PatchServerRunner"/> needs to start Aurorium and know when it answers.
/// </summary>
/// <param name="PatchServerFolder">The folder the patch server is installed in, and runs from.</param>
/// <param name="ExecutablePath">The <c>aurorium.exe</c> to start.</param>
/// <param name="Host">The interface the endpoint is bound to, as its config says.</param>
/// <param name="Port">The port the endpoint is bound to; this is what is probed.</param>
/// <param name="ReadySettleDelay">
/// How long to keep waiting after the endpoint accepts a connection, for the HTTP server behind it to
/// be able to answer. Defaults to <see cref="PatchServerRunner.DefaultReadySettleDelay"/>.
/// </param>
public sealed record PatchServerStartOptions(
    string PatchServerFolder,
    string ExecutablePath,
    string Host,
    int Port,
    IReadOnlyList<string>? Arguments = null,
    TimeSpan? ReadySettleDelay = null);

/// <summary>
/// Runs <c>aurorium.exe</c>, the local patch server Imlight loads <c>Root.wad</c> from, and reports
/// when it is answering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is started first.</b> Imlight asks the patch server for <c>Root.wad</c> and the world data
/// during its own boot, so a patch server that is not up yet is a patch server Imlight has to fall back
/// from its cache for. Starting it first, and waiting for it to answer, is what makes the local one the
/// one Imlight actually uses.
/// </para>
/// <para>
/// <b>Why the endpoint rather than a log line.</b> Aurorium logs through <c>tracing</c> and prints
/// nothing that reliably means "the file server is bound" - a message that changes with a log level or
/// a build. What does mean it is a TCP connection to the address it was told to serve on being
/// accepted, which is exactly what the client will do. The socket accepts a moment before the HTTP
/// server can answer, so a short settle window follows it.
/// </para>
/// <para>
/// <b>One process, no children.</b> Unlike the Director there is nothing to stop in a tree, but the
/// process is still put in the launcher's job so it cannot outlive ViewLight even if the launcher is
/// killed outright.
/// </para>
/// </remarks>
public sealed class PatchServerRunner : IDisposable {

    /// <summary>The executable Aurorium publishes for Windows.</summary>
    public const string ExecutableName = "aurorium.exe";

    /// <summary>
    /// Grace period after the endpoint opens, before the launcher calls the patch server ready.
    /// </summary>
    public static readonly TimeSpan DefaultReadySettleDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PortReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessJob? _job;

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private ServerState _state = ServerState.Stopped;
    private string _host = PatchServerConfig.Host;
    private int _port = PatchServerConfig.DefaultPort;

    /// <summary>Set while the runner is being disposed, so a stop it caused is not reported as a crash.</summary>
    private volatile bool _disposed;

    public PatchServerRunner() : this(ProcessJob.Create(out _)) {
    }

    public PatchServerRunner(ProcessJob? job) => _job = job;

    /// <summary>
    /// The states are the server's own, borrowed rather than duplicated: a patch server starts, warms
    /// up, runs, stops and breaks in the same five ways, and Home draws its row with the same rules.
    /// </summary>
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

    /// <summary>The endpoint it is serving on, once it has been started.</summary>
    public string Endpoint => PatchServerConfig.EndpointUrl(_port);

    public bool HasReadyBeenSignalled { get; private set; }

    public event EventHandler<ServerState>? StateChanged;

    /// <summary>Raw output from the patch server process, plus launcher commentary about it.</summary>
    public event EventHandler<LogEntry>? Output;

    /// <summary>Raised when the patch server is answering, with the reason.</summary>
    public event EventHandler<string>? Ready;

    /// <summary>Raised when the patch server process ends by itself, with its exit code.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>Where the executable for a folder is expected to be.</summary>
    public static string ExecutablePathFor(string patchServerFolder)
        => Path.Combine(patchServerFolder, ExecutableName);

    /// <summary>Starts the patch server and begins watching its endpoint.</summary>
    public void Start(PatchServerStartOptions options) {
        if (IsRunning) {
            throw new InvalidOperationException("The patch server is already running.");
        }

        if (!File.Exists(options.ExecutablePath)) {
            Log(LogLevel.Error, $"Could not find {options.ExecutablePath}.");
            SetState(ServerState.Faulted);
            return;
        }

        HasReadyBeenSignalled = false;
        _host = string.IsNullOrWhiteSpace(options.Host) ? PatchServerConfig.Host : options.Host;
        _port = options.Port;

        // A patch server started earlier can have outlived the launcher and still hold its endpoint.
        var leftover = ServerProcesses.StopAll(options.ExecutablePath, ExecutableName, Log);
        if (leftover.Count > 0) {
            WaitForEndpointRelease();
        }

        var workingDirectory = ServerRunner.ResolveWorkingDirectory(options.PatchServerFolder, options.ExecutablePath);

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
                Log(LogLevel.Error, "The patch server process could not be started.");
                SetState(ServerState.Faulted);
                return;
            }
        }
        catch (Exception ex) {
            Log(LogLevel.Error, $"The patch server process could not be started: {ex.Message}");
            SetState(ServerState.Faulted);
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        TieToJob(process);

        _process = process;
        _cts = new CancellationTokenSource();

        SetState(ServerState.WarmingUp);
        _monitor = MonitorAsync(options, _cts.Token);
    }

    private void TieToJob(Process process) {
        if (_job is null) {
            Log(LogLevel.Warning,
                "No process job is available, so the patch server is not tied to ViewLight: if the "
                + "launcher is killed outright rather than closed, it can keep running and hold its port.");
            return;
        }

        if (_job.Assign(process)) {
            Log(LogLevel.Info,
                "The patch server is tied to ViewLight: if the launcher ends, for any reason, it is "
                + "stopped with it.");
        }
        else {
            Log(LogLevel.Warning,
                $"The patch server could not be tied to ViewLight's process job ({_job.LastError}) so it is "
                + "only stopped on a normal close, not if the launcher is killed.");
        }
    }

    /// <summary>
    /// Waits for a stopped leftover patch server to let go of the endpoint, so the new process is not
    /// beaten to the bind by a socket on its way out.
    /// </summary>
    private void WaitForEndpointRelease() {
        var deadline = DateTimeOffset.Now.Add(PortReleaseTimeout);
        while (DateTimeOffset.Now < deadline) {
            if (PortAllocator.IsAvailable(_port)) {
                return;
            }

            Thread.Sleep(250);
        }

        Log(LogLevel.Warning,
            $"Port {_port} is still held after stopping the leftover patch server process, so the new "
            + "one may not be able to use it.");
    }

    private void HandleLine(string? line) {
        if (string.IsNullOrWhiteSpace(line)) {
            return;
        }

        Log(LogLevel.Server, line);
    }

    private async Task MonitorAsync(PatchServerStartOptions options, CancellationToken cancellationToken) {
        var settleDelay = options.ReadySettleDelay ?? DefaultReadySettleDelay;

        Log(LogLevel.Info, $"Waiting for the patch server to answer on {_host}:{_port}…");

        Stopwatch? settling = null;
        var settleAnnounced = false;

        while (!cancellationToken.IsCancellationRequested) {
            if (!IsRunning) {
                return;
            }

            if (settling is null
                && await TcpProbeAsync(_host, _port, cancellationToken).ConfigureAwait(false)) {
                settling = Stopwatch.StartNew();

                Log(LogLevel.Info,
                    $"Port {_port} on {_host} is open. Waiting {settleDelay.TotalSeconds:0.#} seconds for "
                    + "the patch server to be able to answer…");
            }

            if (settling is not null) {
                if (!settleAnnounced) {
                    settleAnnounced = true;
                    SetState(ServerState.WarmingUp);
                }

                if (settling.Elapsed >= settleDelay) {
                    SignalReady($"its endpoint on {_host}:{_port} is answering");
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
            // Refused, unreachable, timed out - all mean "not serving yet".
            return false;
        }
    }

    private void SignalReady(string reason) {
        if (HasReadyBeenSignalled) {
            return;
        }

        HasReadyBeenSignalled = true;
        Log(LogLevel.Success, $"Patch server is ready — {reason}.");
        SetState(ServerState.Ready);
        Ready?.Invoke(this, reason);
    }

    internal void HandleExit(Process process) {
        var exitCode = 0;
        try {
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException) {
        }

        if (_disposed || State == ServerState.Stopping) {
            return;
        }

        Log(LogLevel.Error, $"The patch server stopped unexpectedly (exit code {exitCode}).");
        SetState(ServerState.Faulted);
        Exited?.Invoke(this, exitCode);
    }

    /// <summary>Stops the patch server.</summary>
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
                Log(LogLevel.Info, "Stopping the patch server…");
                process.Kill(entireProcessTree: true);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    Log(LogLevel.Warning, "The patch server did not shut down within 15 seconds.");
                }
            }
        }
        catch (Exception ex) {
            Log(LogLevel.Warning, $"Stopping the patch server reported: {ex.Message}");
        }
        finally {
            process.Dispose();
            _process = null;
            _cts?.Dispose();
            _cts = null;
            HasReadyBeenSignalled = false;
            Log(LogLevel.Info, "Patch server stopped.");
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

    /// <summary>Reported as the patch server's, the same way the server's runner reports its own.</summary>
    private void Log(LogLevel level, string message)
        => Output?.Invoke(this, new LogEntry(DateTimeOffset.Now, level, message, LogSource.Patcher));

    public void Dispose() {
        _disposed = true;

        try {
            _cts?.Cancel();

            var process = _process;
            if (process is { HasExited: false }) {
                // A deliberate stop rather than a crash, so the exit handler does not call it one.
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

            // Closing the job is the backstop: anything left in it goes with this handle.
            _job?.Dispose();

            SetState(ServerState.Stopped);
        }

        GC.SuppressFinalize(this);
    }
}
