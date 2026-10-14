using System.Diagnostics;

namespace ViewLight.Services;

/// <summary>An Imlight server process that is already running on this machine.</summary>
/// <param name="Id">Its process id.</param>
/// <param name="ExecutablePath">
/// The executable it was started from, or <c>null</c> when that cannot be read (another user's
/// process, or one that ended between being listed and being inspected).
/// </param>
public sealed record RunningServerProcess(int Id, string? ExecutablePath);

/// <summary>
/// Finds and stops Imlight servers that were started from <b>this install</b> and are still running.
/// </summary>
/// <remarks>
/// A server the launcher starts can outlive the launcher: if ViewLight is killed, or crashes, the
/// Director keeps running and keeps every port it bound - 12000, the game realms, the patch server and
/// the embedded database. The next Play then starts a second Director that fails to bind any of them,
/// which looks like the server simply being broken.
/// <para>
/// Only processes started from the managed install's own executable are touched. An Imlight someone
/// runs from somewhere else on the machine is their server, not this launcher's, and killing it would
/// be an unwelcome surprise.
/// </para>
/// </remarks>
public static class ServerProcesses {

    /// <summary>The process name Imlight's Director runs under, without the extension.</summary>
    public static string ProcessName => Path.GetFileNameWithoutExtension(ImlightConfig.ServerExecutableName);

    /// <summary>How long to wait for a stopped process to actually exit.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Is this process the very executable the launcher starts? Comparison is case-insensitive on the
    /// full path, because Windows paths are, and it is normalized so <c>.</c> and <c>..</c> segments
    /// cannot hide a match.
    /// </summary>
    public static bool IsSameExecutable(string? managedExecutablePath, string? candidatePath) {
        if (string.IsNullOrWhiteSpace(managedExecutablePath) || string.IsNullOrWhiteSpace(candidatePath)) {
            return false;
        }

        return string.Equals(
            NormalizePath(managedExecutablePath), NormalizePath(candidatePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The processes, out of <paramref name="candidates"/>, that belong to this install.</summary>
    public static IReadOnlyList<RunningServerProcess> BelongingTo(
        string managedExecutablePath, IEnumerable<RunningServerProcess> candidates)
        => candidates.Where(candidate => IsSameExecutable(managedExecutablePath, candidate.ExecutablePath)).ToList();

    /// <summary>Every running process carrying Imlight's process name, with the executable it runs from.</summary>
    public static IReadOnlyList<RunningServerProcess> Running() {
        var found = new List<RunningServerProcess>();

        Process[] processes;
        try {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception) {
            // Enumeration is best effort: a locked-down machine can refuse it, and that must not stop
            // the launcher from starting its server.
            return found;
        }

        foreach (var process in processes) {
            using (process) {
                found.Add(new RunningServerProcess(process.Id, TryReadExecutablePath(process)));
            }
        }

        return found;
    }

    /// <summary>
    /// Stops every running server that was started from <paramref name="managedExecutablePath"/> and
    /// reports each one through <paramref name="log"/>.
    /// </summary>
    /// <returns>The process ids that were stopped.</returns>
    public static IReadOnlyList<int> StopAll(string managedExecutablePath, Action<LogLevel, string> log) {
        var stopped = new List<int>();

        foreach (var leftover in BelongingTo(managedExecutablePath, Running())) {
            try {
                using var process = Process.GetProcessById(leftover.Id);

                log(LogLevel.Warning,
                    $"Another {ImlightConfig.ServerExecutableName} from this server folder is still running "
                    + $"(PID {leftover.Id}); it would hold the ports the new one needs. Stopping it first…");

                process.Kill(entireProcessTree: true);

                if (process.WaitForExit((int)StopTimeout.TotalMilliseconds)) {
                    log(LogLevel.Info, $"Stopped the leftover server process (PID {leftover.Id}).");
                }
                else {
                    log(LogLevel.Warning,
                        $"The leftover server process (PID {leftover.Id}) did not stop within "
                        + $"{StopTimeout.TotalSeconds:0} seconds.");
                }

                stopped.Add(leftover.Id);
            }
            catch (Exception ex) {
                // Already gone, or not ours to kill. Either way the start carries on and the log says
                // what happened.
                log(LogLevel.Warning,
                    $"Could not stop the leftover server process (PID {leftover.Id}): {ex.Message}");
            }
        }

        return stopped;
    }

    private static string? TryReadExecutablePath(Process process) {
        try {
            // Reading another process's image path needs the same access as opening it, so this can be
            // refused. Returning null keeps the process out of BelongingTo, which is the safe answer:
            // a process we cannot identify is never killed.
            return process.MainModule?.FileName;
        }
        catch (Exception) {
            return null;
        }
    }

    private static string NormalizePath(string path) {
        try {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception) {
            // An unparseable path is compared as written rather than crashing the start.
            return path.Trim();
        }
    }
}
