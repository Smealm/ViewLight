using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// The two directories a backup is made of, resolved to the folders they actually are on this machine.
/// </summary>
/// <remarks>
/// <para>
/// A backup is the launcher's own database directory and the server's own RavenDB data directory, and
/// nothing else. The first is the launcher's notes about a server - the profiles, the playtime, the badges -
/// and lives wherever <see cref="LauncherPaths.DataRoot"/> is, so a portable launcher carries it. The second
/// is the server's own storage, and Imlight's config is what says where it is.
/// </para>
/// <para>
/// The two are never the same folder, and both are resolved the way the launcher already resolves them to
/// open them, so a backup copies exactly what a running launcher would have written - not a second guess at
/// it. A server that is not installed yet simply has no server directory, which is a database the manifest
/// records as absent rather than a reason not to back the launcher's own notes up.
/// </para>
/// </remarks>
public static class BackupSet {

    /// <summary>The databases this machine has somewhere to keep, keyed by <see cref="BackupDatabases"/>.</summary>
    /// <param name="dataRoot">The launcher's own root - <see cref="LauncherPaths.DataRoot"/> in the launcher.</param>
    /// <param name="config">The server's config, or <c>null</c> when there is not one to read.</param>
    /// <param name="serverPath">The server folder, or blank when none is set.</param>
    public static IReadOnlyDictionary<string, string> Directories(
        string dataRoot, ImlightConfig? config, string? serverPath) {

        var directories = new Dictionary<string, string>(StringComparer.Ordinal) {
            [BackupDatabases.Launcher] = LauncherDatabase.DataDirectoryFor(dataRoot),
        };

        if (config is not null && !string.IsNullOrWhiteSpace(serverPath)) {
            try {
                directories[BackupDatabases.Server] = config.ResolveEmbeddedDatabaseDirectory(serverPath);
            }
            catch (Exception) {
                // A config that will not resolve the folder is a server database with nowhere to be read
                // from, so it is simply not part of the set - the launcher's own notes still are.
            }
        }

        return directories;
    }
}
