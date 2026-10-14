using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewLight.Services;

/// <summary>
/// Everything needed to run the RavenDB Imlight ships, without running Imlight.
/// </summary>
/// <param name="ServerAssemblyPath">The <c>Raven.Server.dll</c> that is started.</param>
/// <param name="ServerDirectory">The folder it is started from, which is where its own paths resolve.</param>
/// <param name="DataDirectory">The folder the documents live in - the same one the server would open.</param>
/// <param name="SettingsFilePath">The settings file written for this run, in the launcher's own folder.</param>
/// <param name="Port">The port the config points Imlight at, so both answer on the same address.</param>
/// <param name="StartupTimeout">How long to wait for it to answer before giving up.</param>
public sealed record EmbeddedRavenSetup(
    string ServerAssemblyPath,
    string ServerDirectory,
    string DataDirectory,
    string SettingsFilePath,
    string LogsDirectory,
    int Port,
    TimeSpan StartupTimeout) {

    /// <summary>
    /// Works out how to reach this server's database, or why it cannot be reached without the server.
    /// </summary>
    /// <remarks>
    /// Nothing here is guessed at: the path the database's documents are in is resolved exactly the way
    /// Raven's own embedded server resolves it, and the assembly that would open them has to be there. A
    /// wrong answer here is worse than no answer - it either starts an empty second database beside the
    /// real one, or writes accounts into a place the server will never read.
    /// </remarks>
    public static EmbeddedRavenSetup? For(
        ImlightConfig config, string serverPath, string dataRoot, out string? problem) {

        problem = null;

        if (string.IsNullOrWhiteSpace(serverPath)) {
            problem = "No server folder is set, so there is no database to read.";
            return null;
        }

        var serverDirectory = config.ResolveRavenServerDirectory(serverPath);
        var assembly = ImlightConfig.RavenServerAssemblyFor(serverDirectory);

        if (!File.Exists(assembly)) {
            problem = $"The server's own RavenDB was not found at {assembly}.";
            return null;
        }

        return new EmbeddedRavenSetup(
            assembly,
            serverDirectory,
            config.ResolveEmbeddedDatabaseDirectory(serverPath),
            SettingsPathFor(dataRoot),
            Path.Combine(dataRoot, "raven", "logs"),
            config.EmbeddedDatabasePort,
            TimeSpan.FromSeconds(Math.Clamp(config.EmbeddedDatabaseTimeoutSeconds, 10, 300)));
    }

    /// <summary>Where the settings file written for a launcher-managed database lives.</summary>
    public static string SettingsPathFor(string dataRoot)
        => Path.Combine(dataRoot, "raven", "embedded-settings.json");
}

/// <summary>
/// Runs the server's own RavenDB in the background, so the launcher can read and write the player
/// database while Imlight itself is not running.
/// </summary>
/// <remarks>
/// <para>
/// This is the server's database, started with the server's own binaries: same assembly, same data
/// directory, same port. Nothing about the storage is re-implemented, which is the whole point - the
/// documents stay Imlight's, and an Imlight update replaces the database along with everything else.
/// </para>
/// <para>
/// <b>Only one process can have the data directory open.</b> RavenDB holds it under an exclusive lock, so
/// the launcher only ever starts its own database when Imlight is not running, and stops it again before
/// the server or the game is started. That is also <i>how</i> the two take turns: whoever holds the lock
/// owns it, and the launcher gives it up early rather than being evicted.
/// </para>
/// <para>
/// The database is stopped the way Imlight's own embedded manager stops it - the process is ended -
/// because RavenDB's storage engine is transactional and recovers from that by design. There is no route
/// to ask this build to shut down: its console command wants a real keyboard, and it serves no shutdown
/// endpoint (checked against the shipped 7.2.4 build).
/// </para>
/// </remarks>
public sealed class EmbeddedRaven : IDisposable {

    /// <summary>How long a probe of the database's port may take before it counts as not answering.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient s_http = new() { Timeout = ProbeTimeout };

    private readonly object _gate = new();
    private Process? _process;
    private ProcessJob? _job;

    /// <summary>The port the launcher's own database was started on, once it is running.</summary>
    public int Port { get; private set; }

    /// <summary>The data directory it was started against, once it is running.</summary>
    public string? DataDirectory { get; private set; }

    public bool IsRunning {
        get {
            lock (_gate) {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Why the database is not running, when the launcher tried and could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// The settings the server's RavenDB is started with.
    /// </summary>
    /// <remarks>
    /// The four that matter are the ones Imlight sets too: the port, the data directory,
    /// <c>Setup.Mode=None</c> so a headless server does not sit waiting for a browser to finish setting it
    /// up, and <c>Databases.MaxIdleTimeInSec=-1</c> which is what keeps a database the launcher is not
    /// querying every second from being unloaded. The EULA is accepted on the launcher's behalf because
    /// Imlight's own embedded server has already been run by this user, and a prompt here would be a
    /// window nobody can see.
    /// </remarks>
    public static string BuildSettings(int port, string dataDirectory, string logsDirectory) {
        var settings = new JsonObject {
            ["ServerUrl"] = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            ["DataDir"] = dataDirectory,
            ["LogsPath"] = logsDirectory,
            ["Setup.Mode"] = "None",
            ["License.EulaAccepted"] = true,

            // An unsecured server is only reachable from this machine, which is the only place it is
            // meant to be reachable from: it is bound to loopback.
            ["Security.UnsecuredAccessAllowed"] = "Local",
            ["Databases.MaxIdleTimeInSec"] = -1,
        };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Starts the database and returns <c>null</c>, or returns why it could not be started.
    /// </summary>
    /// <remarks>
    /// The call returns as soon as the process exists; <see cref="WaitUntilAnsweringAsync"/> is what says
    /// whether it actually came up. That matters because a RavenDB start is tens of seconds of work, and
    /// the launcher has to stay responsive while it happens.
    /// </remarks>
    public async Task<string?> StartAsync(EmbeddedRavenSetup setup) {
        await StopAsync().ConfigureAwait(true);

        Problem = null;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(setup.SettingsFilePath)!);
            Directory.CreateDirectory(setup.LogsDirectory);
            File.WriteAllText(setup.SettingsFilePath, BuildSettings(setup.Port, setup.DataDirectory, setup.LogsDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Problem = $"The database's settings could not be written ({ex.Message}).";
        }

        var host = FindDotnetHost();
        if (host is null) {
            return Problem =
                "No .NET host was found to run the server's RavenDB with, so the player database can only "
                + "be read while the server itself is running.";
        }

        var start = new ProcessStartInfo(host) {
            WorkingDirectory = setup.ServerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The shape Imlight's own embedded package launches it with: a settings file, and non-interactive
        // so nothing ever waits for a console that is not there.
        start.ArgumentList.Add(setup.ServerAssemblyPath);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(setup.SettingsFilePath);
        start.ArgumentList.Add("-n");

        try {
            var process = Process.Start(start);
            if (process is null) {
                return Problem = "The database process could not be started.";
            }

            // Read and discarded: a database that fills an unread pipe would stall, and its own log file
            // is where anything worth keeping goes.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_gate) {
                _process = process;
                Port = setup.Port;
                DataDirectory = setup.DataDirectory;

                // Tied to the launcher for the same reason the server is: a launcher that is killed
                // outright must not leave a database holding the lock the server needs.
                _job = ProcessJob.Create(out _);
                _job?.Assign(process);
            }

            return null;
        }
        catch (Exception ex) {
            return Problem = $"The database could not be started: {ex.Message}";
        }
    }

    /// <summary>Waits for the port to answer, which is what says the database is really up.</summary>
    public async Task<bool> WaitUntilAnsweringAsync(EmbeddedRavenSetup setup, CancellationToken token = default) {
        var deadline = DateTimeOffset.UtcNow + setup.StartupTimeout;

        while (DateTimeOffset.UtcNow < deadline) {
            if (token.IsCancellationRequested) {
                return false;
            }

            if (await AnswersAsync(setup.Port, token).ConfigureAwait(false)) {
                return true;
            }

            // A process that has already exited is never going to answer, so the wait ends with the
            // reason rather than running out the whole timeout.
            lock (_gate) {
                if (_process is { HasExited: true }) {
                    Problem = "The database process stopped while it was starting.";
                    return false;
                }
            }

            try {
                await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return false;
            }
        }

        Problem = $"The database did not answer on port {setup.Port} within {setup.StartupTimeout.TotalSeconds:0} seconds.";
        return false;
    }

    /// <summary>Whether anything at all answers as a RavenDB on this port - the launcher's or the server's.</summary>
    public static async Task<bool> AnswersAsync(int port, CancellationToken token = default) {
        try {
            using var response = await s_http
                .GetAsync(PlayerDatabaseClient.BaseUrl(port) + "/", token)
                .ConfigureAwait(false);

            // Any answer at all is the proof wanted: what a RavenDB says at its root varies by version
            // and setup mode, but only something listening can say anything.
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            return false;
        }
    }

    /// <summary>Waits for the port to fall silent again, after the database has been asked to stop.</summary>
    public static async Task<bool> WaitUntilSilentAsync(int port, TimeSpan timeout, CancellationToken token = default) {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline) {
            if (!await AnswersAsync(port, token).ConfigureAwait(false)) {
                return true;
            }

            try {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return false;
            }
        }

        return !await AnswersAsync(port, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the launcher's own database, so the server - or the game, which reaches the server's - can
    /// have the data directory.
    /// </summary>
    public async Task StopAsync() {
        Process? process;
        ProcessJob? job;

        lock (_gate) {
            process = _process;
            job = _job;
            _process = null;
            _job = null;
        }

        var port = Port;

        if (process is not null) {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception) {
                // A database that will not die is reported by the port still answering below, which is
                // the thing that actually matters to the server that is about to start.
            }
            finally {
                process.Dispose();
            }
        }

        job?.Dispose();

        // The port is what the server cares about: it comes up onto it, and a database still holding it
        // would stop Imlight booting with nothing but an exit code to show for it.
        if (port > 0 && !await WaitUntilSilentAsync(port, TimeSpan.FromSeconds(10)).ConfigureAwait(false)) {
            Problem = $"Something is still answering on port {port} after the launcher's database was stopped.";
        }

        Port = 0;
        DataDirectory = null;
    }

    public void Dispose() {
        // Fire and forget would leave the kill half-done at exit; the wait is short and bounded.
        StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The .NET host used to run the database server, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <c>Raven.Server.dll</c> is a framework-dependent assembly - the same shape Imlight's embedded
    /// package runs it in - so it needs a host. <c>dotnet</c> is looked for on <c>PATH</c> first, because
    /// that is the one the user's own dotnet install provides, and then in the machine-wide folder it
    /// normally lives in when it is not on the path at all.
    /// </remarks>
    public static string? FindDotnetHost() {
        var machine = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "dotnet", "dotnet.exe");

        foreach (var installed in new[] { machine, local }) {
            if (File.Exists(installed)) {
                return installed;
            }
        }

        // Nothing is installed where it usually is, so the one on PATH is asked for by name and the
        // operating system resolves it when the process starts. If there is none the start fails and
        // says so, which is the only way to find out without launching something to look.
        return "dotnet";
    }
}
