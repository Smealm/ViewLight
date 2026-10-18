namespace ViewLight.Services;

/// <summary>
/// One thing a full reset deletes.
/// </summary>
/// <param name="Name">What it is, in the words the screen uses.</param>
/// <param name="Path">Where it is.</param>
/// <param name="Directory">Whether it is a folder rather than a single file.</param>
public sealed record ResetTarget(string Name, string Path, bool Directory);

/// <summary>What a wipe actually did, so the Log can say so line by line.</summary>
/// <param name="Deleted">The targets that are gone.</param>
/// <param name="Missing">The targets that were not there, which is the outcome asked for.</param>
/// <param name="Problems">The targets that would not go, and why.</param>
public sealed record ResetReport(
    IReadOnlyList<string> Deleted, IReadOnlyList<string> Missing, IReadOnlyList<string> Problems) {

    /// <summary>Whether everything asked for is gone.</summary>
    public bool IsComplete => Problems.Count == 0;
}

/// <summary>
/// Starting over: everything this machine remembers about playing here, deleted, so the launcher can make
/// itself a new database and a server can make itself a new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> A launcher that has been backed up to the cloud has no way back to a clean
/// slate: the databases it holds are older than the copy up there, so a sync restores what was wiped, and
/// the only advice left is to delete the backup by hand in a Drive folder that cannot be seen. So the way
/// out is a reset that wipes both sides - this machine's databases and the copy of them in the cloud - and
/// then lets the launcher make itself a new database and back <i>that</i> up, which is what stops the next
/// sync from putting the old one back.
/// </para>
/// <para>
/// <b>What it does not touch.</b> The two databases are the whole of what is remembered about a player:
/// the accounts, the wizards, the profiles, the playtime, the badges and the pictures. Everything else -
/// the server's files, the patch server's fetched revisions, the game, the launcher's own settings - is
/// downloaded or chosen again cheaply, or is a preference rather than data, and deleting it would cost
/// hours to put back rather than seconds. The screen says both lists out loud before anything is deleted.
/// </para>
/// <para>
/// <b>Deleting is the only thing that is allowed to be destructive here, so it is deliberately narrow.</b>
/// A target has to be a rooted path inside a place the launcher already owns, and a path that is a drive
/// root, the launcher's own root, or a parent of it is refused rather than deleted - a wrong path here is
/// somebody's whole disk, so the guard is a rule the code checks and a test pins down rather than a
/// comment asking for care.
/// </para>
/// </remarks>
public static class LauncherReset {

    /// <summary>
    /// The sentence a person has to type before a full reset happens.
    /// </summary>
    /// <remarks>
    /// The issue behind this screen asked for exactly this wording - the wipe is local <i>and</i> the cloud
    /// copy of the same data, and the sentence is the one line that says both. It is compared case- and
    /// whitespace-insensitively, because the point is that the user read it rather than that they can type.
    /// </remarks>
    public const string ConfirmationPhrase =
        "i understand i am wiping all data and resetting my launcher and also deleting all backed up data "
        + "off my google drive";

    /// <summary>
    /// How long the first screen is up before it can be gone past.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds, as the issue asked for. It is not a security measure and is not pretending to be
    /// one: it is the time it takes to read what is about to be lost, and having to wait is what makes a
    /// person read it. The count is over a clock the screen can show, so the wait is visible rather than a
    /// button that is mysteriously dead.
    /// </remarks>
    public static readonly TimeSpan ReadingTime = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the sentence just typed is the confirmation.
    /// </summary>
    /// <remarks>
    /// Whitespace is normalised and case ignored: a phrase typed from memory with a doubled space or a
    /// capital letter is the user having read it, which is the whole point of asking for it.
    /// </remarks>
    public static bool PhraseMatches(string? typed) => Normalise(typed) == Normalise(ConfirmationPhrase);

    private static string Normalise(string? text)
        => string.Join(' ', (text ?? string.Empty)
            .Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    /// <summary>
    /// Whether a reset may be started right now.
    /// </summary>
    /// <remarks>
    /// Both databases have to be shut for their folders to be deleted, so the launcher has to be idle: no
    /// server running or starting (it owns the server's database), and no cloud sync in flight (it has both
    /// of them closed and is copying them). The screen says which one is in the way rather than offering a
    /// button that cannot work.
    /// </remarks>
    public static bool MayReset(bool serverRunningOrStarting, bool syncRunning)
        => !serverRunningOrStarting && !syncRunning;

    /// <summary>
    /// Why not, in the words the screen shows, or <c>null</c> when a reset may be started.
    /// </summary>
    public static string? WhyNot(bool serverRunningOrStarting, bool syncRunning) => (serverRunningOrStarting, syncRunning) switch {
        (true, _) => "The server is running, and its database is in its hands. Stop the server and the "
                     + "reset can be started.",
        (_, true) => "A cloud sync is running, and it has both databases open to copy them. Try again when "
                     + "it has finished.",
        _ => null,
    };

    /// <summary>
    /// Everything a full reset deletes, in the order it deletes it.
    /// </summary>
    /// <param name="dataRoot">The launcher's own root - <see cref="LauncherPaths.DataRoot"/> in the launcher.</param>
    /// <param name="serverDatabaseDirectory">
    /// The server's own database directory as the server's config resolves it, or <c>null</c> when there is
    /// no server here to have one.
    /// </param>
    /// <remarks>
    /// The launcher's database is first because it is the one that holds the sign-in: deleting it while
    /// nothing is reading it is what makes a reset a reset rather than a restore of the same profiles.
    /// </remarks>
    public static IReadOnlyList<ResetTarget> Targets(string dataRoot, string? serverDatabaseDirectory) {
        var targets = new List<ResetTarget> {
            new("ViewLight's own database - the profiles, the playtime, the badges, the pictures and the "
                + "cloud sign-in",
                LauncherDatabase.DataDirectoryFor(dataRoot), Directory: true),

            new("ViewLight's older profile files and pictures, if a launcher from before the database left "
                + "any behind",
                Path.Combine(dataRoot, "profiles"), Directory: true),
        };

        if (!string.IsNullOrWhiteSpace(serverDatabaseDirectory)) {
            targets.Add(new ResetTarget(
                "The server's database - every account, every wizard and everything they own",
                serverDatabaseDirectory, Directory: true));
        }

        targets.Add(new ResetTarget(
            "The record of what was last backed up, so the next sync does not think the cloud and this "
            + "machine already agree",
            CloudBackupState.SyncStatePath(dataRoot), Directory: false));

        targets.Add(new ResetTarget(
            "The archive the last backup was assembled in, which is a copy of the two databases",
            Path.Combine(dataRoot, "backup"), Directory: true));

        return targets;
    }

    /// <summary>
    /// Whether a path is one a reset is allowed to delete.
    /// </summary>
    /// <remarks>
    /// Four refusals, each of which is somebody's whole installation if it is wrong: an empty path, a path
    /// that is not rooted (so it would resolve against whatever the working directory happens to be), a
    /// filesystem root, and the launcher's own root or anything containing it. Everything the launcher
    /// deletes is a folder <i>inside</i> a root it already owns, which is what makes this check short.
    /// </remarks>
    public static bool IsSafeToDelete(string? path, string dataRoot) {
        // Not rooted is refused before anything is resolved: a relative path would be resolved against
        // whatever the working directory happens to be, which is a folder nobody chose.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) {
            return false;
        }

        try {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            // A filesystem root: "/", "C:\", "\\server\share".
            var fileSystemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);

            if (fileSystemRoot.Length == 0
                || string.Equals(full, fileSystemRoot, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            // The launcher's own root, or anything above it - which is the same mistake seen from above and
            // would take the settings, the caches and, for a portable launcher, the game with it.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));

            return !string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                   && !root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// Deletes what the targets name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything answers rather than throws, and a target that would not go is reported rather than
    /// stopping the rest: half a reset is worth knowing about, and it is said out loud in the Log. A target
    /// that is not there is the outcome asked for - a launcher whose server has never run has no server
    /// database to delete.
    /// </para>
    /// <para>
    /// Synchronous, because it is a handful of folder deletions; the caller runs it off the UI thread, which
    /// is where several gigabytes of RavenDB files being removed would otherwise be felt.
    /// </para>
    /// </remarks>
    public static ResetReport Delete(IEnumerable<ResetTarget> targets, string dataRoot) {
        var deleted = new List<string>();
        var missing = new List<string>();
        var problems = new List<string>();

        foreach (var target in targets) {
            if (!IsSafeToDelete(target.Path, dataRoot)) {
                problems.Add($"{target.Name} was left alone: {target.Path} is not a folder this launcher is "
                             + "allowed to delete.");
                continue;
            }

            try {
                if (target.Directory) {
                    if (Directory.Exists(target.Path)) {
                        Directory.Delete(target.Path, recursive: true);
                        deleted.Add(target.Path);
                    }
                    else {
                        missing.Add(target.Path);
                    }
                }
                else if (File.Exists(target.Path)) {
                    File.Delete(target.Path);
                    deleted.Add(target.Path);
                }
                else {
                    missing.Add(target.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
                problems.Add($"{target.Name} could not be deleted ({ex.Message}). Something may still be "
                             + "holding it open - stop the game, the server and the patch server and try "
                             + "again.");
            }
        }

        return new ResetReport(deleted, missing, problems);
    }
}
