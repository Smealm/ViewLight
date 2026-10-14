using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Drives <see cref="ServerRunner"/> against real child processes and real sockets. Readiness detection
/// decides when the client gets started, so it is tested for real rather than trusted.
/// </summary>
public class ServerRunnerTests {

    private const string Host = "127.0.0.1";

    /// <summary>Short settle window so the tests do not sleep for the production 10 seconds.</summary>
    private static readonly TimeSpan TestSettleDelay = TimeSpan.FromSeconds(1);

    private static string SystemExecutable(string name)
        => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>A child process that stays alive for roughly <paramref name="seconds"/> headlessly.</summary>
    private static IReadOnlyList<string> Sleep(int seconds)
        => ["/c", $"ping -n {seconds} 127.0.0.1"];

    private static ServerStartOptions OptionsFor(
        TempWorkspace workspace, string serverFolder, int port, IReadOnlyList<string>? arguments = null)
        => new(
            ServerPath: serverFolder,
            ExecutablePath: SystemExecutable("cmd.exe"),
            AccountServerHost: Host,
            AccountServerPort: port,
            LogFilePath: workspace.Path(Path.Combine("server", "logs", "log.txt")),
            Arguments: arguments ?? Sleep(40),
            ReadySettleDelay: TestSettleDelay);

    [Fact]
    public async Task WaitsForThePortToOpenAndThenHonoursTheSettleWindow() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var port = PortAllocator.FindFreePort()!.Value;

        using var runner = new ServerRunner();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Ready += (_, reason) => ready.TrySetResult(reason);

        runner.Start(OptionsFor(workspace, serverPath, port));
        Assert.Equal(ServerState.WarmingUp, runner.State);

        // -------- nothing is listening yet, so the runner must not claim the server is ready.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(ServerState.WarmingUp, runner.State);
        Assert.False(ready.Task.IsCompleted);

        // -------- now the account server starts listening.
        using var accountServer = new TcpListener(IPAddress.Loopback, port);
        accountServer.Start();

        var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(25)));

        Assert.True(winner == ready.Task, "The runner never noticed the account server's port opening.");
        Assert.Equal(ServerState.Ready, runner.State);
        Assert.Contains(port.ToString(), await ready.Task);

        await runner.StopAsync();
        Assert.Equal(ServerState.Stopped, runner.State);
    }

    [Fact]
    public async Task StaysWarmingUpWhileThePortIsClosed() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var port = PortAllocator.FindFreePort()!.Value;

        using var runner = new ServerRunner();
        var readyFired = false;
        runner.Ready += (_, _) => readyFired = true;

        runner.Start(OptionsFor(workspace, serverPath, port));

        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(ServerState.WarmingUp, runner.State);
        Assert.False(readyFired);

        await runner.StopAsync();
    }

    [Fact]
    public async Task DoesNotNeedThePortWhenTheServerLogSaysItIsUp() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var logPath = workspace.Path(Path.Combine("server", "logs", "log.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var port = PortAllocator.FindFreePort()!.Value;
        using var runner = new ServerRunner();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Ready += (_, reason) => ready.TrySetResult(reason);

        runner.Start(OptionsFor(workspace, serverPath, port));

        // Exactly the line Imlight logs as its last act before entering its idle loop.
        File.WriteAllText(logPath,
            "2026-10-13 13:00:00.100 INF Director : Imlight may now be connected to.\r\n");

        var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(25)));

        Assert.True(winner == ready.Task, "The runner never used the log readiness line.");
        Assert.Equal(ServerState.Ready, runner.State);

        // The reason must name the signal that actually fired, not claim the port opened.
        Assert.Contains("server log reports it is up", await ready.Task);

        await runner.StopAsync();
    }

    [Fact]
    public async Task TakesTheServerAtItsWordAndSkipsTheSettleWindow() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var logPath = workspace.Path(Path.Combine("server", "logs", "log.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var port = PortAllocator.FindFreePort()!.Value;
        using var runner = new ServerRunner();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Ready += (_, reason) => ready.TrySetResult(reason);

        // A settle window far longer than this test is prepared to wait: the readiness line is the
        // server saying it is ready, so if it were made to sit through the window the test would time
        // out rather than pass.
        var options = OptionsFor(workspace, serverPath, port) with {
            ReadySettleDelay = TimeSpan.FromMinutes(5),
        };
        runner.Start(options);

        File.WriteAllText(logPath,
            "2026-10-13 13:00:00.100 INF Director : Imlight may now be connected to.\r\n");

        var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(winner == ready.Task, "The readiness line was made to wait out the settle window.");
        Assert.Equal(ServerState.Ready, runner.State);
        Assert.Contains("server log reports it is up", await ready.Task);

        await runner.StopAsync();
    }

    [Fact]
    public async Task ReportsFaultedWhenTheServerProcessExitsOnItsOwn() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        using var runner = new ServerRunner();
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (_, code) => exitCode.TrySetResult(code);

        runner.Start(OptionsFor(
            workspace, serverPath, PortAllocator.FindFreePort()!.Value, ["/c", "exit 3"]));

        var winner = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(winner == exitCode.Task, "The runner never noticed the server had exited.");
        Assert.Equal(3, await exitCode.Task);
        Assert.Equal(ServerState.Faulted, runner.State);
    }

    [Fact]
    public void FaultsImmediatelyWhenTheExecutableIsMissing() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        using var runner = new ServerRunner();
        runner.Start(new ServerStartOptions(
            ServerPath: serverPath,
            ExecutablePath: workspace.Path(Path.Combine("server", ImlightConfig.ServerExecutableName)),
            AccountServerHost: Host,
            AccountServerPort: 12000,
            LogFilePath: workspace.Path(Path.Combine("server", "logs", "log.txt"))));

        Assert.Equal(ServerState.Faulted, runner.State);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task IgnoresAReadyMarkerLeftInTheLogByAnEarlierRun() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var logPath = workspace.Path(Path.Combine("server", "logs", "log.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // Imlight appends to a daily file, so running twice in a day leaves the previous run's
        // readiness line in the log. Taking it for this run's would start the client against a server
        // that has not finished booting.
        File.WriteAllText(logPath,
            "2026-10-13 13:00:00.100 INF Director : Imlight may now be connected to.\r\n");

        var port = PortAllocator.FindFreePort()!.Value;
        using var runner = new ServerRunner();
        var readyFired = false;
        runner.Ready += (_, _) => readyFired = true;

        runner.Start(OptionsFor(workspace, serverPath, port));

        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(ServerState.WarmingUp, runner.State);
        Assert.False(readyFired, "A ready marker from an earlier run was taken as this one being up.");

        await runner.StopAsync();
    }

    // ------------------------------------------------------------------ working directory

    [Fact]
    public void RunsTheServerFromTheFolderHoldingItsExecutable() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");
        var elsewhere = workspace.CreateDirectory(Path.Combine("somewhere-else"));

        var expected = Path.Combine(serverPath, ImlightConfig.ServerExecutableName);

        // The managed install: the executable sits in the server folder, so that is the working
        // directory - and with it ./logs and the embedded database's relative path.
        Assert.Equal(serverPath, ServerRunner.ResolveWorkingDirectory(serverPath, expected));
        Assert.Equal(serverPath, ServerRunner.ResolveWorkingDirectory(serverPath + Path.DirectorySeparatorChar, expected));

        // A bare executable name resolves inside the server folder rather than in whatever directory
        // the launcher happens to run in.
        Assert.Equal(serverPath, ServerRunner.ResolveWorkingDirectory(serverPath, ImlightConfig.ServerExecutableName));

        // Pointed at an executable somewhere else, its own folder wins: that is where it will look for
        // its config and logs.
        var outsideExe = Path.Combine(elsewhere, ImlightConfig.ServerExecutableName);
        Assert.Equal(elsewhere, ServerRunner.ResolveWorkingDirectory(serverPath, outsideExe));
    }

    [Fact]
    public async Task StopsALeftoverServerFromThisInstallBeforeStartingANewOne() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        // A real process carrying the name the guard hunts for, started from the path ViewLight manages.
        var executable = Path.Combine(serverPath, ImlightConfig.ServerExecutableName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executable, overwrite: true);

        using var leftover = StartStandInServer(executable);

        using var runner = new ServerRunner();
        var lines = new List<LogEntry>();
        runner.Output += (_, entry) => {
            lock (lines) {
                lines.Add(entry);
            }
        };

        runner.Start(new ServerStartOptions(
            ServerPath: serverPath,
            ExecutablePath: executable,
            AccountServerHost: Host,
            AccountServerPort: PortAllocator.FindFreePort()!.Value,
            LogFilePath: workspace.Path(Path.Combine("server", "logs", "log.txt")),
            Arguments: ["-n", "40", "127.0.0.1"],
            ReadySettleDelay: TestSettleDelay));

        try {
            Assert.True(leftover.WaitForExit(10_000),
                "The leftover server was left running, holding the ports the new one needs.");

            lock (lines) {
                Assert.Contains(lines, entry => entry.Message.Contains($"Stopped the leftover server process (PID {leftover.Id})"));
                Assert.Contains(lines, entry => entry.Message.Contains($"Starting {ImlightConfig.ServerExecutableName} in {serverPath}"));
            }

            Assert.True(runner.IsRunning, "The new server did not start.");
        }
        finally {
            await runner.StopAsync();
        }
    }

    private static Process StartStandInServer(string executable) {
        var startInfo = new ProcessStartInfo {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        foreach (var argument in new[] { "-n", "60", "127.0.0.1" }) {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stand-in server.");
    }

    // ------------------------------------------------------------------ tie to the launcher

    [Fact]
    public async Task PutsTheServerItStartsInTheLauncherJob() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        using var runner = new ServerRunner(job);
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (_, code) => exitCode.TrySetResult(code);

        runner.Start(OptionsFor(workspace, serverPath, PortAllocator.FindFreePort()!.Value));
        Assert.True(runner.IsRunning, "The server did not start.");

        // Closing the job is what Windows does to the launcher's handles when the launcher dies. The
        // server has to go with it, and the runner has to see that rather than believe it is still up.
        job!.Dispose();

        var winner = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(winner == exitCode.Task,
            "The started server was not in the launcher's job, so killing the launcher would strand it.");
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task SaysSoWhenThereIsNoJobToTieTheServerTo() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        // A machine that refuses the job still gets a working server; it just loses the guarantee, and
        // the log has to say that instead of quietly pretending otherwise.
        using var runner = new ServerRunner(null);
        var lines = new List<LogEntry>();
        runner.Output += (_, entry) => {
            lock (lines) {
                lines.Add(entry);
            }
        };

        runner.Start(OptionsFor(workspace, serverPath, PortAllocator.FindFreePort()!.Value));

        Assert.True(runner.IsRunning, "The server did not start without a job.");

        lock (lines) {
            Assert.Contains(lines, entry => entry.Level == LogLevel.Warning
                                            && entry.Message.Contains("not tied to ViewLight"));
        }

        await runner.StopAsync();
    }

    // ------------------------------------------------------------------ crash reporting

    [Fact]
    public void ExplainsTheDotNetCrashExitCode() {
        var note = ServerRunner.DescribeExitCode(unchecked((int)ServerRunner.DotNetUnhandledExceptionCode));

        Assert.Contains("0xE0434352", note);
        Assert.Contains("unhandled exception", note);

        // Every other code speaks for itself.
        Assert.Equal(string.Empty, ServerRunner.DescribeExitCode(3));
        Assert.Equal(string.Empty, ServerRunner.DescribeExitCode(0));
    }

    [Fact]
    public async Task SurfacesTheServerLogsOwnErrorsWhenTheProcessDies() {
        using var workspace = new TempWorkspace();
        var serverPath = workspace.CreateDirectory("server");

        using var runner = new ServerRunner();
        var lines = new List<LogEntry>();
        runner.Output += (_, entry) => {
            lock (lines) {
                lines.Add(entry);
            }
        };

        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (_, code) => exitCode.TrySetResult(code);

        // The stand-in server stays up for a couple of seconds, the way the real one does.
        runner.Start(OptionsFor(
            workspace, serverPath, PortAllocator.FindFreePort()!.Value,
            ["/c", "ping -n 3 127.0.0.1 >nul & exit 3"]));

        // Imlight logs its real diagnosis here while it runs rather than to the console, and a crash
        // takes the process down before anyone gets the chance to read it. Written after the start,
        // because only this run's lines are the crash's diagnosis - an earlier run's are not.
        workspace.WriteFile(
            Path.Combine("server", "logs", "log.txt"),
            "2026-10-13 14:57:35.088 -04:00 [ERR] Patch server endpoint is not available! Continuing without patch server.\r\n"
            + "2026-10-13 14:57:52.983 -04:00 [INF] Imlight configuration loaded.\r\n");

        var winner = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(winner == exitCode.Task, "The runner never noticed the server had exited.");

        lock (lines) {
            Assert.Contains(lines, l => l.Level == LogLevel.Error && l.Message.Contains("exit code 3"));
            Assert.Contains(lines, l => l.Message.StartsWith("Server log:")
                                        && l.Message.Contains("Patch server endpoint is not available"));

            // Only the errors are worth quoting back; info lines are noise in a failure report.
            Assert.DoesNotContain(lines, l => l.Message.Contains("Imlight configuration loaded"));
        }
    }
}
