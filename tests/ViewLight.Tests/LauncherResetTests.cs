using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Starting over: what the reset deletes, what it refuses to delete, and the two things that have to be
/// true before it does anything.
/// </summary>
/// <remarks>
/// The deletion is the only irreversible thing in the launcher, so the guard is pinned down here rather
/// than trusted: a wrong path is somebody's whole install, and the three refusals - the launcher's own
/// root, a filesystem root, and a path that is not rooted at all - are each a way of getting one.
/// </remarks>
public class LauncherResetTests {

    [Fact]
    public void TheSentenceIsTheOneTheScreenAsksFor() {
        Assert.True(LauncherReset.PhraseMatches(LauncherReset.ConfirmationPhrase));

        // Typed from memory: a capital, a doubled space and a line break are all still the sentence.
        Assert.True(LauncherReset.PhraseMatches(
            "I understand i am wiping   all data and resetting my launcher and also deleting all backed up "
            + "data off my\r\nGoogle drive"));

        // And anything else is not: a half-typed sentence, or one word changed, is not a confirmation.
        Assert.False(LauncherReset.PhraseMatches(null));
        Assert.False(LauncherReset.PhraseMatches(string.Empty));
        Assert.False(LauncherReset.PhraseMatches("i understand"));
        Assert.False(LauncherReset.PhraseMatches(
            "i understand i am wiping all data and resetting my launcher and also deleting all backed up "
            + "data off my dropbox"));
    }

    [Fact]
    public void TheFirstStepIsGivenTheTimeToBeRead() {
        Assert.Equal(TimeSpan.FromSeconds(15), LauncherReset.ReadingTime);
    }

    [Fact]
    public void AResetNeedsTheServerDownAndNoSyncRunning() {
        Assert.True(LauncherReset.MayReset(serverRunningOrStarting: false, syncRunning: false));
        Assert.False(LauncherReset.MayReset(serverRunningOrStarting: true, syncRunning: false));
        Assert.False(LauncherReset.MayReset(serverRunningOrStarting: false, syncRunning: true));
        Assert.False(LauncherReset.MayReset(serverRunningOrStarting: true, syncRunning: true));

        // And when it may not, the reason names the thing in the way rather than saying no.
        Assert.Null(LauncherReset.WhyNot(false, false));
        Assert.Contains("server is running", LauncherReset.WhyNot(true, false));
        Assert.Contains("sync is running", LauncherReset.WhyNot(false, true));
    }

    [Fact]
    public void BothDatabasesAreNamedAndTheServersOnlyWhenThereIsOne() {
        using var workspace = new TempWorkspace();
        var dataRoot = workspace.Path("launcher-data");
        workspace.CreateDirectory("server/database");

        var withServer = LauncherReset.Targets(dataRoot, workspace.Path("server/database"));

        Assert.Contains(withServer, t => t.Path == LauncherDatabase.DataDirectoryFor(dataRoot) && t.Directory);
        Assert.Contains(withServer, t => t.Path == workspace.Path("server/database") && t.Directory);
        Assert.Contains(withServer, t => t.Path == CloudBackupState.SyncStatePath(dataRoot) && !t.Directory);

        // A launcher with no server here has no second database, and does not name one that is not there.
        var withoutServer = LauncherReset.Targets(dataRoot, null);

        Assert.DoesNotContain(withoutServer, t => t.Path.Contains("server"));
        Assert.All(withoutServer, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
    }

    [Fact]
    public void DeletingRefusesThePathsThatWouldTakeTheWholeInstallWithThem() {
        using var workspace = new TempWorkspace();
        var dataRoot = workspace.Path("launcher-data");
        workspace.CreateDirectory("launcher-data/database");

        // Everything the launcher owns lives under its own root, so the root itself is never a target.
        Assert.False(LauncherReset.IsSafeToDelete(dataRoot, dataRoot));
        Assert.False(LauncherReset.IsSafeToDelete(workspace.Root, dataRoot));
        Assert.False(LauncherReset.IsSafeToDelete(Path.GetPathRoot(workspace.Root), dataRoot));
        Assert.False(LauncherReset.IsSafeToDelete(string.Empty, dataRoot));
        Assert.False(LauncherReset.IsSafeToDelete(null, dataRoot));
        Assert.False(LauncherReset.IsSafeToDelete("database", dataRoot));

        // What it does delete is a folder inside one: the two databases and the launcher's own bookkeeping.
        Assert.True(LauncherReset.IsSafeToDelete(LauncherDatabase.DataDirectoryFor(dataRoot), dataRoot));
        Assert.True(LauncherReset.IsSafeToDelete(CloudBackupState.SyncStatePath(dataRoot), dataRoot));
    }

    [Fact]
    public void DeletingTakesTheFoldersAndTheFilesAndSaysWhatItFound() {
        using var workspace = new TempWorkspace();
        var dataRoot = workspace.Path("launcher-data");
        var database = workspace.CreateDirectory("launcher-data/database");
        var serverDatabase = workspace.CreateDirectory("server/database");
        var syncState = workspace.WriteFile("launcher-data/backup-sync.json", "{}");

        File.WriteAllText(Path.Combine(database, "document.json"), "{}");
        File.WriteAllText(Path.Combine(serverDatabase, "account.json"), "{}");

        var targets = new List<ResetTarget> {
            new("the launcher's database", database, Directory: true),
            new("the server's database", serverDatabase, Directory: true),
            new("the sync record", syncState, Directory: false),
            new("a folder that is not there", workspace.Path("launcher-data/profiles"), Directory: true),
        };

        var report = LauncherReset.Delete(targets, dataRoot);

        Assert.True(report.IsComplete);
        Assert.False(Directory.Exists(database));
        Assert.False(Directory.Exists(serverDatabase));
        Assert.False(File.Exists(syncState));
        Assert.Equal(3, report.Deleted.Count);

        // Not there is the outcome asked for rather than a failure: a first-run launcher has no profiles.
        Assert.Single(report.Missing);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void DeletingRefusesATargetOutsideWhatTheLauncherOwnsAndLeavesItAlone() {
        using var workspace = new TempWorkspace();
        var dataRoot = workspace.Path("launcher-data");
        var somebodyElsesFolder = workspace.CreateDirectory("pictures");
        File.WriteAllText(Path.Combine(somebodyElsesFolder, "holiday.png"), "not really a picture");

        var report = LauncherReset.Delete(
            [new ResetTarget("the launcher's own root", dataRoot, Directory: true)], dataRoot);

        Assert.False(report.IsComplete);
        Assert.Contains(report.Problems, problem => problem.Contains("not a folder this launcher is allowed"));
        Assert.Empty(report.Deleted);
    }
}
