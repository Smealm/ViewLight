using System.Diagnostics;

namespace ViewLight.Services;

/// <summary>What the Wizard101 client the launcher started is doing.</summary>
public enum ClientState {

    /// <summary>The launcher has not started a client this session.</summary>
    NotRunning,

    /// <summary>Started, but not yet far enough along to be called up.</summary>
    Starting,

    /// <summary>The client is up and the user is in the game.</summary>
    Running,

    /// <summary>The client the launcher started has exited.</summary>
    Closed,
}

/// <summary>
/// Follows the client the launcher started, so the launcher can say whether the game is running.
/// </summary>
/// <remarks>
/// <para>
/// The client is watched as a process, because that is the only signal the game gives from outside: it
/// reports nothing to the launcher, and <c>WizardClient.log</c> is written by the client for its own
/// reasons. A process that is alive is a client that has not exited, which is all the launcher claims.
/// </para>
/// <para>
/// <b>Starting is short on purpose.</b> A process that has just started is not yet a game with a window,
/// so there is a short grace period in which the launcher shows the client as starting rather than
/// running. The client's own window appearing ends it early when Windows will tell us about it; the
/// grace period ending is the answer when it will not, so the state can never be stuck on "starting".
/// </para>
/// <para>
/// Nothing here touches the client's own lifetime: closing the launcher leaves the game alone.
/// </para>
/// </remarks>
public sealed class ClientSession : IDisposable {

    /// <summary>How long a started client counts as starting before it is called running.</summary>
    public static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _watch;
    private volatile bool _disposed;

    public ClientState State { get; private set; } = ClientState.NotRunning;

    /// <summary>True while a client the launcher started is still alive.</summary>
    public bool IsRunning => State is ClientState.Starting or ClientState.Running;

    /// <summary>The process id of the client being watched, when there is one.</summary>
    public int? ProcessId {
        get {
            lock (_gate) {
                try {
                    return _process is { HasExited: false } process ? process.Id : null;
                }
                catch (InvalidOperationException) {
                    return null;
                }
            }
        }
    }

    public event EventHandler<ClientState>? StateChanged;

    /// <summary>Starts the client, pointed at the given server endpoint, and follows it.</summary>
    /// <param name="login">
    /// The account to sign in as, when the launcher has one it can sign in with. Without it the client
    /// shows its own login screen.
    /// </param>
    /// <param name="steamRequired">
    /// Whether the account is a Steam one, which the client has to be told with <c>-ST</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">The client could not be started.</exception>
    public Process Launch(string executablePath, string gameServerIp, int loginServerPort,
        TimeSpan? startupGrace = null, ClientLogin? login = null, bool steamRequired = false) {
        var process = ClientLauncher.Launch(executablePath, gameServerIp, loginServerPort, login, steamRequired);
        Track(process, startupGrace);
        return process;
    }

    /// <summary>Follows an already-started client process.</summary>
    public void Track(Process process, TimeSpan? startupGrace = null) {
        ArgumentNullException.ThrowIfNull(process);
        StopWatching();

        lock (_gate) {
            _process = process;
        }

        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        SetState(ClientState.Starting);

        var watch = new CancellationTokenSource();
        _watch = watch;
        _ = WatchForStartupAsync(process, startupGrace ?? DefaultStartupGrace, watch.Token);
    }

    /// <summary>Forgets the client being watched. The client itself keeps running.</summary>
    public void StopWatching() {
        CancellationTokenSource? watch;
        Process? process;

        lock (_gate) {
            watch = _watch;
            process = _process;
            _watch = null;
            _process = null;
        }

        watch?.Cancel();
        watch?.Dispose();

        if (process is not null) {
            try {
                process.Exited -= OnProcessExited;
            }
            catch (InvalidOperationException) {
                // Already gone; nothing to unsubscribe from.
            }
        }
    }

    /// <summary>
    /// Waits out the grace period, ending it early if the client puts a window up. Whichever comes
    /// first, the client is running; an exit in the meantime is reported by the exit handler instead.
    /// </summary>
    private async Task WatchForStartupAsync(Process process, TimeSpan grace, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + grace;

        try {
            while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline) {
                if (HasExited(process)) {
                    return;
                }

                if (HasWindow(process)) {
                    break;
                }

                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) {
            return;
        }

        if (token.IsCancellationRequested || HasExited(process)) {
            return;
        }

        SetState(ClientState.Running);
    }

    private void OnProcessExited(object? sender, EventArgs e) {
        if (_disposed) {
            return;
        }

        StopWatching();
        SetState(ClientState.Closed);
    }

    private void SetState(ClientState state) {
        if (State == state) {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static bool HasExited(Process process) {
        try {
            return process.HasExited;
        }
        catch (InvalidOperationException) {
            return true;
        }
    }

    /// <summary>
    /// Whether the client has put a window up. Windows-only information: elsewhere this is always zero,
    /// which is why the grace period is what actually decides.
    /// </summary>
    private static bool HasWindow(Process process) {
        try {
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception) {
            return false;
        }
    }

    public void Dispose() {
        _disposed = true;
        StopWatching();
    }
}
