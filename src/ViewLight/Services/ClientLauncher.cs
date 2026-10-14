using System.Diagnostics;
using System.Globalization;

namespace ViewLight.Services;

/// <summary>Locates and starts the Wizard101 game client from the game folder.</summary>
/// <remarks>
/// The executable is found relative to the game folder, which is why the launcher asks for the folder
/// rather than the executable:
/// <list type="bullet">
///   <item><description>client - <c>bin\WizardGraphicalClient.exe</c></description></item>
///   <item><description>archive - <c>Data\GameData\root.wad</c></description></item>
/// </list>
/// <para>
/// <b>The client must be started from <c>bin\</c>, not from the game folder.</b> It reads
/// <c>PatchConfig.xml</c>, its message tables and <c>WizardClient.log</c> relative to its working
/// directory, and those live in <c>bin\</c>. Started from the game folder it logs
/// <c>Could not open ConfigFile PatchConfig.xml for reading.</c> and never gets as far as the login
/// screen. See <see cref="BuildStartInfo"/> for the evidence.
/// </para>
/// </remarks>
public static class ClientLauncher {

    /// <summary>The documented location of the client inside a game folder.</summary>
    public const string RelativeExecutable = GameFolderLocator.RelativeClientExecutable;

    /// <summary>Globs tried inside <c>bin\</c> if the exact name is not there.</summary>
    private static readonly string[] BinFallbackPatterns = [
        "*GraphicalClient*.exe",
        "wizard101*.exe",
    ];

    public static string? FindExecutable(string? gameFolder) {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) {
            return null;
        }

        var expected = Path.Combine(gameFolder, RelativeExecutable);
        if (File.Exists(expected)) {
            return expected;
        }

        // Fall back to whatever the client is called in this build, but only inside bin\.
        // Deliberately not scanning the game folder itself: a normal Wizard101 install keeps a
        // Wizard101.exe store launcher there, and starting that with -L would be the wrong thing.
        var binFolder = Path.Combine(gameFolder, "bin");
        return Directory.Exists(binFolder)
            ? EnumerateFirst(binFolder, BinFallbackPatterns)
            : null;
    }

    private static string? EnumerateFirst(string folder, IEnumerable<string> patterns) {
        foreach (var pattern in patterns) {
            try {
                var match = Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (match is not null) {
                    return match;
                }
            }
            catch (IOException) {
                // Unreadable folder; keep looking.
            }
            catch (UnauthorizedAccessException) {
            }
        }

        return null;
    }

    /// <summary>
    /// The arguments that point the client at a server: <c>-L {gameServerIp} {loginServerPort}</c>, then
    /// <c>-ST</c> when the account is a Steam one, and - when there is an account to sign in as -
    /// <c>-U ..{accountId} {sessionKey}</c>.
    /// </summary>
    /// <remarks>
    /// <b>The <c>..</c> is not decoration, and the account is an id.</b> The client only takes the
    /// <c>-U</c> path when the value parses as <c>..%llu</c> - a <c>..</c> followed by a 64-bit GID -
    /// which was established by handing a real client each spelling in turn: a plain username, the bare
    /// account id, and <c>..[accountId]</c>. Only the last one skipped the login screen; the others were
    /// ignored and the login screen appeared. What the client then sends is
    /// <c>MSG_USER_VALIDATE</c> carrying that GID, the machine id and a key hashed from the second
    /// value, so this continues a session rather than starting one - see <see cref="AutoLogin"/>.
    /// <para>
    /// <b><c>-ST</c> is "Steam required"</b>, and it is the difference between a Steam account signing
    /// in to Wizard101's own server and not. A standalone account must not be handed it, and a Steam
    /// account cannot get in without it, so the choice is the user's and never guessed at.
    /// </para>
    /// </remarks>
    public static string BuildArguments(
        string gameServerIp, int loginServerPort, ClientLogin? login = null, bool steamRequired = false) {
        var parts = new List<string> {
            "-L",
            gameServerIp,
            loginServerPort.ToString(CultureInfo.InvariantCulture),
        };

        if (steamRequired) {
            parts.Add(SteamArgument);
        }

        if (login is not null) {
            parts.Add("-U");
            parts.Add($"..{login.AccountId.ToString(CultureInfo.InvariantCulture)}");
            parts.Add(login.ClientKey2);
        }

        return string.Join(' ', parts);
    }

    /// <summary><c>-ST</c>, Imlight's own name for "this account is a Steam one".</summary>
    public const string SteamArgument = "-ST";

    /// <summary>The <c>-U</c> arguments as one string, for logging what was actually passed.</summary>
    public static string LoginArguments(ClientLogin login) {
        var account = login.AccountId.ToString(CultureInfo.InvariantCulture);

        return $"-U ..{account} {login.ClientKey2}";
    }

    /// <summary>
    /// The directory the client has to run in: the one holding the executable, i.e. <c>bin\</c>.
    /// </summary>
    public static string WorkingDirectoryFor(string executablePath) {
        var full = Path.GetFullPath(executablePath);

        // GetDirectoryName returns null only for a drive root, where the path is its own directory.
        return Path.GetDirectoryName(full) ?? full;
    }

    /// <summary>Everything needed to start the client, pointed at the given server endpoint.</summary>
    /// <remarks>
    /// The working directory is the executable's own folder rather than the game folder. That was
    /// checked against a real Steam install by starting the client both ways and reading
    /// <c>WizardClient.log</c>: from the game folder the client cannot open <c>PatchConfig.xml</c> (it
    /// lives in <c>bin\</c>), fails to load its message modules and never reaches the login screen;
    /// from <c>bin\</c> it loads the patch config, reports its revision and starts the patcher.
    /// </remarks>
    public static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string gameServerIp,
        int loginServerPort,
        ClientLogin? login = null,
        bool steamRequired = false) {

        var startInfo = new ProcessStartInfo {
            FileName = executablePath,
            WorkingDirectory = WorkingDirectoryFor(executablePath),
            UseShellExecute = false,
        };

        // ArgumentList (rather than a raw string) so nothing needs quoting by hand.
        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add(gameServerIp);
        startInfo.ArgumentList.Add(loginServerPort.ToString(CultureInfo.InvariantCulture));

        if (steamRequired) {
            startInfo.ArgumentList.Add(SteamArgument);
        }

        if (login is not null) {
            startInfo.ArgumentList.Add("-U");
            startInfo.ArgumentList.Add($"..{login.AccountId.ToString(CultureInfo.InvariantCulture)}");
            startInfo.ArgumentList.Add(login.ClientKey2);
        }

        return startInfo;
    }

    /// <summary>Starts the client, pointed at the given server endpoint.</summary>
    /// <exception cref="InvalidOperationException">The client could not be started.</exception>
    public static Process Launch(
        string executablePath,
        string gameServerIp,
        int loginServerPort,
        ClientLogin? login = null,
        bool steamRequired = false)
        => Process.Start(BuildStartInfo(executablePath, gameServerIp, loginServerPort, login, steamRequired))
           ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executablePath)}.");
}
