using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The sync rule, on its own. Everything about a cloud backup that can be got wrong is got wrong here first:
/// which side wins, and the one case - both sides changed - that has to stop and ask rather than pick.
/// </summary>
public class BackupSyncTests {

    [Fact]
    public void WithNothingAnywhereThereIsNothingToDo() {
        var plan = BackupSync.Plan(null, null, null);

        Assert.Equal(BackupSyncAction.Nothing, plan.Action);
    }

    [Fact]
    public void WithNothingInTheCloudTheLocalDataGoesUp() {
        var plan = BackupSync.Plan(Manifest("local"), null, null);

        Assert.Equal(BackupSyncAction.Upload, plan.Action);
    }

    [Fact]
    public void WithNothingHereAStoredBackupComesDown() {
        var plan = BackupSync.Plan(null, Manifest("remote"), null);

        Assert.Equal(BackupSyncAction.Download, plan.Action);
    }

    [Fact]
    public void AFirstSyncOfTwoIdenticalCopiesIsNothingToDo() {
        var plan = BackupSync.Plan(Manifest("same"), Manifest("same"), null);

        Assert.Equal(BackupSyncAction.Nothing, plan.Action);
    }

    [Fact]
    public void AFirstSyncOfTwoDifferentCopiesTakesTheCloudsCopy() {
        // No common ancestor and the two sides disagree - and the safe answer is the cloud's, not a question.
        // A machine that has never synced has whatever a fresh install has: a database it has just created, and
        // at most a server one from a single run. Asking which to keep made "this machine's data" a button that
        // writes that over a real backup, and nothing puts that back.
        var plan = BackupSync.Plan(Manifest("here"), Manifest("there"), null);

        Assert.Equal(BackupSyncAction.Download, plan.Action);
        Assert.Contains("never synced", plan.Reason);

        // Stated as the reason it gives, so the Log says which rule decided rather than only what happened.
        Assert.Contains("nothing here is uploaded over it", plan.Reason);
    }

    [Fact]
    public void TheCloudsCopyWinsBeforeAnythingIsUploadedEvenWhenTheLocalSideIsBehind() {
        // The same rule from the other direction: a local copy that *is* different in both databases is still
        // replaced by the cloud's, because a first sync has no way to tell which side a person meant.
        var local = new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("launcher"),
                [BackupDatabases.Server] = BackupDatabaseState.Absent,
            },
        };
        var remote = Manifest("there");

        Assert.Equal(BackupSyncAction.Download, BackupSync.Plan(local, remote, null).Action);

        // And once that restore has happened the two sides have an ancestor, so this is never the ordinary
        // case: the very next sync is judged the usual way.
        Assert.Equal(BackupSyncAction.Nothing, BackupSync.Plan(remote, remote, remote).Action);
        Assert.Equal(BackupSyncAction.Upload,
            BackupSync.Plan(Manifest("changed"), remote, remote).Action);
    }

    [Fact]
    public void NothingChangedSinceTheLastSyncIsQuiet() {
        var plan = BackupSync.Plan(Manifest("same"), Manifest("same"), Manifest("same"));

        Assert.Equal(BackupSyncAction.Nothing, plan.Action);
    }

    [Fact]
    public void ALocalChangeWithTheCloudUnchangedUploads() {
        var plan = BackupSync.Plan(Manifest("new"), Manifest("old"), Manifest("old"));

        Assert.Equal(BackupSyncAction.Upload, plan.Action);
    }

    [Fact]
    public void ACloudChangeWithTheLocalUnchangedDownloads() {
        var plan = BackupSync.Plan(Manifest("old"), Manifest("new"), Manifest("old"));

        Assert.Equal(BackupSyncAction.Download, plan.Action);
    }

    [Fact]
    public void BothSidesChangedIsTheOneCaseThatAsks() {
        var plan = BackupSync.Plan(Manifest("mine"), Manifest("theirs"), Manifest("agreed"));

        Assert.Equal(BackupSyncAction.Conflict, plan.Action);
    }

    [Fact]
    public void BothSidesChangingToTheSameThingIsNotAConflict() {
        // The two sides agreeing is exactly the state a sync wants to end in; it only looks like a change
        // because the ancestor is older.
        var plan = BackupSync.Plan(Manifest("same-new"), Manifest("same-new"), Manifest("agreed"));

        Assert.Equal(BackupSyncAction.Nothing, plan.Action);
    }

    [Fact]
    public void ABackupThatWasDeletedIsPutBackRatherThanRestored() {
        // The ancestor says the two were equal once, but the cloud now holds nothing. With one side empty
        // there is nothing to weigh against the ancestor, and the copy that exists is the one that wins.
        var plan = BackupSync.Plan(Manifest("here"), null, Manifest("here"));

        Assert.Equal(BackupSyncAction.Upload, plan.Action);
    }

    [Fact]
    public void AChangedLocalSideWithNoCloudCopyStillUploads() {
        var plan = BackupSync.Plan(Manifest("changed"), null, Manifest("was"));

        Assert.Equal(BackupSyncAction.Upload, plan.Action);
    }

    [Fact]
    public void ADatabaseThatAppearedOnOneSideCountsAsAChange() {
        // The launcher's notes unchanged, but the server has now run and its database exists where it did not.
        var ancestor = new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("launcher"),
                [BackupDatabases.Server] = BackupDatabaseState.Absent,
            },
        };

        var local = new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("launcher"),
                [BackupDatabases.Server] = new BackupDatabaseState("server"),
            },
        };

        var plan = BackupSync.Plan(local, ancestor, ancestor);

        Assert.Equal(BackupSyncAction.Upload, plan.Action);
    }

    /// <summary>A manifest whose two databases hash to a marker built from the given word.</summary>
    private static BackupManifest Manifest(string word) {
        return new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState($"{word}-launcher"),
                [BackupDatabases.Server] = new BackupDatabaseState($"{word}-server"),
            },
        };
    }
}
