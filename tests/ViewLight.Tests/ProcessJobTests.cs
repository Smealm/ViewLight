using System.Diagnostics;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The job object is what guarantees no server outlives the launcher, so it is driven against real
/// processes rather than trusted. Every test here is skipped off Windows, where the feature is
/// meaningless and <see cref="ProcessJob.Create"/> deliberately returns nothing.
/// </summary>
public class ProcessJobTests {

    [Fact]
    public void CreatesAJobOnWindows() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var job = ProcessJob.Create(out var error);

        Assert.NotNull(job);
        Assert.Null(error);
    }

    [Fact]
    public void ClosingTheJobKillsTheProcessesInIt() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        using var child = StartPing(Path.Combine(Environment.SystemDirectory, "ping.exe"), workspace.Root, 60);
        Assert.True(job!.Assign(child), "The process could not be put in the job.");
        Assert.False(child.HasExited);

        // This is what the kernel does to the launcher's handles when the launcher dies.
        job.Dispose();

        Assert.True(child.WaitForExit(10_000),
            "Closing the job left its process running, so a killed launcher would strand a server.");
    }

    [Fact]
    public void ClosingTheJobAlsoKillsProcessesSpawnedByItsMembers() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();

        // Imlight's Director spawns the game realms as child processes, and those inherit the job. A
        // uniquely named copy of ping stands in for one, so it can be found in the process list.
        const string grandchildName = "viewlight-job-grandchild";
        var grandchild = Path.Combine(workspace.Root, grandchildName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), grandchild);

        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = workspace.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The pause gives the assignment below time to land before the grandchild exists, which is the
        // same ordering the runner relies on when it ties the Director immediately after starting it.
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"ping -n 2 127.0.0.1 >nul & {grandchildName}.exe -n 60 127.0.0.1");

        using var cmd = Process.Start(startInfo);
        Assert.NotNull(cmd);
        Assert.True(job!.Assign(cmd!), "The parent process could not be put in the job.");

        Assert.True(
            WaitFor(() => CountProcesses(grandchildName) > 0, TimeSpan.FromSeconds(15)),
            "The child process never started, so there was nothing to test.");

        job.Dispose();

        Assert.True(
            WaitFor(() => CountProcesses(grandchildName) == 0, TimeSpan.FromSeconds(15)),
            "A process spawned by a job member outlived the job, which would leave a game realm behind.");
    }

    [Fact]
    public void ClosingTheJobTwiceIsHarmless() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        job!.Dispose();
        job.Dispose();
    }

    [Fact]
    public void RefusesTheAssignmentOnceItIsClosed() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var job = ProcessJob.Create(out _);
        Assert.NotNull(job);

        using var child = StartPing(Path.Combine(Environment.SystemDirectory, "ping.exe"), workspace.Root, 30);

        job!.Dispose();

        Assert.False(job.Assign(child));
        Assert.NotNull(job.LastError);
    }

    private static Process StartPing(string executable, string workingDirectory, int seconds) {
        var startInfo = new ProcessStartInfo {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(seconds.ToString());
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        return process!;
    }

    private static int CountProcesses(string name) {
        var processes = Process.GetProcessesByName(name);

        try {
            return processes.Length;
        }
        finally {
            foreach (var process in processes) {
                process.Dispose();
            }
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout) {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline) {
            if (condition()) {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }
}
