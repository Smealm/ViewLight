using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Drives <see cref="PatchServerRunner"/> against real child processes and real sockets. The patch
/// server has to be up before the Imlight server is started, so when it is declared ready is what the
/// wait in Play is built on, and it is tested for real rather than trusted.
/// </summary>
public class PatchServerRunnerTests {

    private const string Host = "127.0.0.1";

    /// <summary>Short settle window so the tests do not sleep for the production 2 seconds.</summary>
    private static readonly TimeSpan TestSettleDelay = TimeSpan.FromSeconds(1);

    private static string SystemExecutable(string name)
        => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>A child process that stays alive for roughly <paramref name="seconds"/> headlessly.</summary>
    private static IReadOnlyList<string> Sleep(int seconds)
        => ["/c", $"ping -n {seconds} 127.0.0.1"];

    private static PatchServerStartOptions OptionsFor(
        TempWorkspace workspace, string folder, int port, IReadOnlyList<string>? arguments = null)
        => new(
            PatchServerFolder: folder,
            ExecutablePath: SystemExecutable("cmd.exe"),
            Host: Host,
            Port: port,
            Arguments: arguments ?? Sleep(40),
            ReadySettleDelay: TestSettleDelay);

    [Fact]
    public async Task WaitsForTheEndpointAndThenHonoursTheSettleWindow() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");
        var port = PortAllocator.FindFreePort()!.Value;

        using var runner = new PatchServerRunner();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Ready += (_, reason) => ready.TrySetResult(reason);

        runner.Start(OptionsFor(workspace, folder, port));
        Assert.Equal(ServerState.WarmingUp, runner.State);

        // -------- nothing is listening yet, so the runner must not claim the patch server is ready.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(ServerState.WarmingUp, runner.State);
        Assert.False(ready.Task.IsCompleted);

        // -------- now the file server binds its endpoint, which is what the client will connect to.
        using var endpoint = new TcpListener(IPAddress.Loopback, port);
        endpoint.Start();

        var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(25)));

        Assert.True(winner == ready.Task, "The runner never noticed the patch server's endpoint opening.");
        Assert.Equal(ServerState.Ready, runner.State);
        Assert.Contains(port.ToString(), await ready.Task);
        Assert.Contains("is answering", await ready.Task);

        await runner.StopAsync();
        Assert.Equal(ServerState.Stopped, runner.State);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task ReportsItsOutputIntoTheLaunchersLog() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");
        var port = PortAllocator.FindFreePort()!.Value;

        using var runner = new PatchServerRunner();
        var lines = new List<LogEntry>();
        runner.Output += (_, entry) => {
            lock (lines) {
                lines.Add(entry);
            }
        };

        runner.Start(OptionsFor(workspace, folder, port));

        // The launcher's own commentary goes to the same place the process's output does, so the Log
        // says which port was chosen and why it is waiting - and all of it is the patch server's, which
        // is what the Log's own box for it filters on.
        lock (lines) {
            Assert.Contains(lines, line => line.Message.Contains($"Waiting for the patch server to answer on {Host}:{port}"));
            Assert.All(lines, line => Assert.Equal(LogSource.Patcher, line.Source));
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        await runner.StopAsync();
    }

    [Fact]
    public void FaultsImmediatelyWhenTheExecutableIsMissing() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        using var runner = new PatchServerRunner();
        runner.Start(new PatchServerStartOptions(
            PatchServerFolder: folder,
            ExecutablePath: PatchServerRunner.ExecutablePathFor(folder),
            Host: Host,
            Port: PortAllocator.FindFreePort()!.Value));

        Assert.Equal(ServerState.Faulted, runner.State);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task ReportsFaultedWhenThePatchServerExitsOnItsOwn() {
        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        using var runner = new PatchServerRunner();
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (_, code) => exitCode.TrySetResult(code);

        runner.Start(OptionsFor(
            workspace, folder, PortAllocator.FindFreePort()!.Value, ["/c", "exit 4"]));

        var winner = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(winner == exitCode.Task, "The runner never noticed the patch server had exited.");
        Assert.Equal(4, await exitCode.Task);
        Assert.Equal(ServerState.Faulted, runner.State);
    }

    [Fact]
    public async Task PutsThePatchServerItStartsInTheLauncherJob() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        using var runner = new PatchServerRunner(job);
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (_, code) => exitCode.TrySetResult(code);

        runner.Start(OptionsFor(workspace, folder, PortAllocator.FindFreePort()!.Value));
        Assert.True(runner.IsRunning, "The patch server did not start.");

        // Closing the job is what Windows does to the launcher's handles when the launcher dies.
        job!.Dispose();

        var winner = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(winner == exitCode.Task,
            "The started patch server was not in the launcher's job, so killing the launcher would strand it.");
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task StopsALeftoverPatchServerFromThisInstallBeforeStartingANewOne() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = workspace.CreateDirectory("patchserver");

        // A real process carrying the name the guard hunts for, started from the path ViewLight manages.
        var executable = PatchServerRunner.ExecutablePathFor(folder);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executable, overwrite: true);

        using var leftover = StartStandIn(executable);

        using var runner = new PatchServerRunner();
        var lines = new List<LogEntry>();
        runner.Output += (_, entry) => {
            lock (lines) {
                lines.Add(entry);
            }
        };

        runner.Start(new PatchServerStartOptions(
            PatchServerFolder: folder,
            ExecutablePath: executable,
            Host: Host,
            Port: PortAllocator.FindFreePort()!.Value,
            Arguments: ["-n", "40", "127.0.0.1"],
            ReadySettleDelay: TestSettleDelay));

        try {
            Assert.True(leftover.WaitForExit(10_000),
                "The leftover patch server was left running, holding the port the new one needs.");

            lock (lines) {
                Assert.Contains(lines, entry => entry.Message.Contains($"Stopped the leftover server process (PID {leftover.Id})"));
            }

            Assert.True(runner.IsRunning, "The new patch server did not start.");
        }
        finally {
            await runner.StopAsync();
        }
    }

    private static Process StartStandIn(string executable) {
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

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stand-in patch server.");
    }
}
