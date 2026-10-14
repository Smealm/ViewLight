using System.Diagnostics;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Drives <see cref="ClientSession"/> against real child processes, because what it reports is what the
/// user is told about their game being up.
/// </summary>
public class ClientSessionTests {

    /// <summary>A child process that stays alive for roughly <paramref name="seconds"/> headlessly.</summary>
    private static Process StartChild(int seconds) {
        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"ping -n {seconds} 127.0.0.1");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the child process.");
    }

    [Fact]
    public void ClaimsNothingBeforeAThingIsStarted() {
        using var session = new ClientSession();

        Assert.Equal(ClientState.NotRunning, session.State);
        Assert.False(session.IsRunning);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task AStartedClientIsStartingAndThenRunning() {
        using var session = new ClientSession();
        var states = new List<ClientState>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        session.StateChanged += (_, state) => {
            lock (states) {
                states.Add(state);
            }

            if (state == ClientState.Running) {
                running.TrySetResult();
            }
        };

        using var child = StartChild(30);
        session.Track(child, TimeSpan.FromMilliseconds(300));

        // Reported synchronously, so the screen never shows nothing after Play was pressed.
        Assert.Equal(ClientState.Starting, session.State);
        Assert.True(session.IsRunning, "A client that has just started is not counted as running at all.");
        Assert.Equal(child.Id, session.ProcessId);

        var winner = await Task.WhenAny(running.Task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(winner == running.Task, "A started client never reached the running state.");
        Assert.Equal(ClientState.Running, session.State);

        lock (states) {
            Assert.Equal(new[] { ClientState.Starting, ClientState.Running }, states.ToArray());
        }
    }

    [Fact]
    public async Task StartingHoldsForTheGracePeriod() {
        using var session = new ClientSession();

        using var child = StartChild(30);
        session.Track(child, TimeSpan.FromSeconds(5));

        // Well inside the grace period the client is still starting, not running.
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        Assert.Equal(ClientState.Starting, session.State);
    }

    [Fact]
    public async Task AClientThatExitsIsReportedClosed() {
        using var session = new ClientSession();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        session.StateChanged += (_, state) => {
            if (state == ClientState.Closed) {
                closed.TrySetResult();
            }
        };

        // Exits on its own after about a second.
        using var child = StartChild(1);
        session.Track(child, TimeSpan.FromSeconds(5));

        var winner = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(winner == closed.Task, "A client that exited was never noticed.");
        Assert.Equal(ClientState.Closed, session.State);
        Assert.False(session.IsRunning);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task WatchingCanBeDroppedWithoutTouchingTheGame() {
        using var session = new ClientSession();

        using var child = StartChild(30);
        session.Track(child, TimeSpan.FromSeconds(5));
        session.StopWatching();

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // The game is the user's, not the launcher's: no longer following it must not end it.
        Assert.False(child.HasExited, "Dropping the watch killed the client.");
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task ADisposedSessionReportsNothingFurther() {
        var session = new ClientSession();
        var reported = 0;
        session.StateChanged += (_, _) => Interlocked.Increment(ref reported);

        using var child = StartChild(30);
        session.Track(child, TimeSpan.FromSeconds(5));
        session.Dispose();

        child.Kill(entireProcessTree: true);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Shutting down is not news, so the only report is the one from starting.
        Assert.Equal(1, reported);
    }
}
