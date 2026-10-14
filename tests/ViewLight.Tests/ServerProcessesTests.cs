using System.Diagnostics;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Covers the guard against starting a second Director on top of one that never shut down. The kill
/// path is exercised with real processes: a copy of a system executable renamed to
/// <c>Imlight.Director.exe</c>, which is the process name the guard looks for.
/// </summary>
public class ServerProcessesTests {

    private const string ExecutableName = ImlightConfig.ServerExecutableName;

    [Fact]
    public void IsSameExecutable_MatchesTheSameFileWrittenDifferently() {
        var path = Path.Combine("C:", "Servers", "ViewLight", ExecutableName);

        Assert.True(ServerProcesses.IsSameExecutable(path, path));
        Assert.True(ServerProcesses.IsSameExecutable(path, path.ToUpperInvariant()));
        Assert.True(ServerProcesses.IsSameExecutable(path, Path.Combine("C:", "Servers", ".", "ViewLight", ExecutableName)));
        Assert.True(ServerProcesses.IsSameExecutable(path, Path.Combine("C:", "Servers", "Other", "..", "ViewLight", ExecutableName)));

        // A different install is a different server, and must never be treated as this one's.
        Assert.False(ServerProcesses.IsSameExecutable(path, Path.Combine("D:", "Servers", "ViewLight", ExecutableName)));
        Assert.False(ServerProcesses.IsSameExecutable(path, Path.Combine("C:", "Servers", "Retail", ExecutableName)));

        // Nothing to compare against.
        Assert.False(ServerProcesses.IsSameExecutable(path, null));
        Assert.False(ServerProcesses.IsSameExecutable(path, string.Empty));
        Assert.False(ServerProcesses.IsSameExecutable(null, path));
    }

    [Fact]
    public void BelongingTo_KeepsOnlyThisInstallsProcesses() {
        var managed = Path.Combine("C:", "Servers", "ViewLight", ExecutableName);

        var candidates = new RunningServerProcess[] {
            new(1, managed),
            new(2, managed.ToUpperInvariant()),
            new(3, Path.Combine("D:", "Somewhere", ExecutableName)),
            // A process whose image path could not be read: never claimed, so never killed.
            new(4, null),
        };

        var ours = ServerProcesses.BelongingTo(managed, candidates);

        Assert.Equal(new[] { 1, 2 }, ours.Select(process => process.Id));
    }

    [Fact]
    public void ProcessName_IsTheDirectorWithoutItsExtension() {
        Assert.Equal("Imlight.Director", ServerProcesses.ProcessName);
    }

    [Fact]
    public void StopsTheLeftoverFromThisInstallAndLeavesAnotherInstallAlone() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        using var workspace = new TempWorkspace();
        var mine = workspace.CreateDirectory(Path.Combine("mine"));
        var theirs = workspace.CreateDirectory(Path.Combine("theirs"));

        var myExecutable = CopyProcessImage(Path.Combine(mine, ExecutableName));
        var theirExecutable = CopyProcessImage(Path.Combine(theirs, ExecutableName));

        using var leftover = StartLongRunningProcess(myExecutable);
        using var otherServer = StartLongRunningProcess(theirExecutable);

        try {
            var lines = new List<LogEntry>();
            var stopped = ServerProcesses.StopAll(myExecutable, (level, message) => {
                lock (lines) {
                    lines.Add(new LogEntry(DateTimeOffset.Now, level, message));
                }
            });

            Assert.Equal(new[] { leftover.Id }, stopped);
            Assert.True(leftover.WaitForExit(10_000), "The leftover server was not stopped.");

            // The other install's server is none of our business and is still running.
            Assert.False(otherServer.HasExited, "A server from another install was stopped.");

            lock (lines) {
                Assert.Contains(lines, entry => entry.Level == LogLevel.Warning
                                                && entry.Message.Contains($"(PID {leftover.Id})")
                                                && entry.Message.Contains("Stopping it first"));

                Assert.Contains(lines, entry => entry.Message.Contains($"Stopped the leftover server process (PID {leftover.Id})"));

                // The one that was left alone must not be mentioned at all.
                Assert.DoesNotContain(lines, entry => entry.Message.Contains($"PID {otherServer.Id}"));
            }
        }
        finally {
            Kill(leftover);
            Kill(otherServer);
        }
    }

    [Fact]
    public void ReportsNothingWhenNoServerFromThisInstallIsRunning() {
        using var workspace = new TempWorkspace();
        var executable = Path.Combine(workspace.CreateDirectory("server"), ExecutableName);

        var lines = new List<LogEntry>();
        var stopped = ServerProcesses.StopAll(executable, (level, message) => lines.Add(new LogEntry(DateTimeOffset.Now, level, message)));

        Assert.Empty(stopped);
        Assert.Empty(lines);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A copy of a harmless system executable renamed to <see cref="ExecutableName"/>, so the guard
    /// finds a real process carrying the name it hunts for.
    /// </summary>
    private static string CopyProcessImage(string destination) {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), destination, overwrite: true);
        return destination;
    }

    private static void Kill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception) {
            // Best effort: the test is over either way.
        }
    }

    private static Process StartLongRunningProcess(string executable) {
        var startInfo = new ProcessStartInfo {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stand-in server.");

        // ping takes a moment to bind its name to a pid.
        var deadline = DateTimeOffset.Now.AddSeconds(10);
        while (DateTimeOffset.Now < deadline && !IsRunningWithExecutable(process.Id, executable)) {
            Thread.Sleep(50);
        }

        return process;
    }

    private static bool IsRunningWithExecutable(int id, string executable) {
        try {
            using var process = Process.GetProcessById(id);
            return !process.HasExited
                   && ServerProcesses.IsSameExecutable(executable, process.MainModule?.FileName);
        }
        catch (Exception) {
            return false;
        }
    }
}
