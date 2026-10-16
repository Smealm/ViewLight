using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The orchestration: hash what is here, read what is there, move whichever side the plan names. The decision
/// itself is <see cref="BackupSyncTests"/>; what is checked here is that the right bytes end up in the right
/// place, and that a failure leaves the machine as it was.
/// </summary>
public class CloudBackupTests {

    [Fact]
    public async Task AnUploadLeavesTheArchiveAndItsManifestInTheCloud() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");

        var local = fixture.Backup.BuildLocalManifest();
        var problem = await fixture.Backup.UploadAsync(local);

        Assert.Null(problem);
        Assert.Contains(CloudBackupFiles.Archive, fixture.Store.Names);
        Assert.Contains(CloudBackupFiles.Manifest, fixture.Store.Names);

        // The manifest up there is the one that was uploaded.
        var remote = BackupManifest.TryParse(System.Text.Encoding.UTF8.GetString(
            fixture.Store.Contents(CloudBackupFiles.Manifest)!));

        Assert.True(remote!.HasSameContent(local));
    }

    [Fact]
    public async Task AfterAnUploadTheNextPlanIsQuiet() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");

        var uploaded = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(uploaded);

        // Nothing has changed on either side since, so the sync has nothing to say.
        var result = await fixture.Backup.PlanAsync(ancestor: uploaded);

        Assert.Equal(BackupSyncAction.Nothing, result.Plan.Action);
    }

    [Fact]
    public async Task ALocalChangeIsUploaded() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");

        var uploaded = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(uploaded);

        fixture.Write("doc.txt", "two");

        var result = await fixture.Backup.PlanAsync(uploaded);

        Assert.Equal(BackupSyncAction.Upload, result.Plan.Action);
    }

    [Fact]
    public async Task ACloudChangeIsBroughtDown() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");

        var uploaded = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(uploaded);

        // Something else took a backup: the cloud's manifest now says something the local copy never saw.
        fixture.Store.Put(CloudBackupFiles.Manifest,
            System.Text.Encoding.UTF8.GetBytes(new BackupManifest {
                Databases = { [BackupDatabases.Launcher] = new BackupDatabaseState("elsewhere") },
            }.ToJson()));

        var result = await fixture.Backup.PlanAsync(uploaded);

        Assert.Equal(BackupSyncAction.Download, result.Plan.Action);
    }

    [Fact]
    public async Task BothSidesChangingAsks() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");

        var uploaded = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(uploaded);

        fixture.Write("doc.txt", "two");
        fixture.Store.Put(CloudBackupFiles.Manifest,
            System.Text.Encoding.UTF8.GetBytes(new BackupManifest {
                Databases = { [BackupDatabases.Launcher] = new BackupDatabaseState("elsewhere") },
            }.ToJson()));

        var result = await fixture.Backup.PlanAsync(uploaded);

        Assert.Equal(BackupSyncAction.Conflict, result.Plan.Action);
    }

    [Fact]
    public async Task AFreshLauncherDoesNotWriteOverTheBackupAlreadyInTheCloud() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "the copy from another machine");

        // A backup this machine did not take - another machine's, or this one's from before.
        var stored = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(stored);

        // And now the state a fresh launcher is in: a database directory it has just created, empty, and no
        // record of ever having synced with this cloud.
        File.Delete(Path.Combine(fixture.DataDirectory, "doc.txt"));

        var result = await fixture.Backup.PlanAsync(ancestor: null);

        // The cloud's copy wins, so what a fresh machine has - nothing - is never what goes up. Uploading here
        // is the loss that cannot be undone: the backup is real, the local copy is a database the launcher
        // created a moment ago.
        Assert.Equal(BackupSyncAction.Download, result.Plan.Action);

        // Taken, and it lands: the stored backup is still exactly what it was, and the data comes back.
        var (restored, problem) = await fixture.Backup.DownloadAndRestoreAsync();

        Assert.Null(problem);
        Assert.NotNull(restored);
        Assert.True(restored!.HasSameContent(stored));
        Assert.Equal("the copy from another machine",
            File.ReadAllText(Path.Combine(fixture.DataDirectory, "doc.txt")));

        // And with the ancestor that restore settles, the next sync is the ordinary kind: nothing changed on
        // either side, so it is quiet rather than a second restore.
        Assert.Equal(BackupSyncAction.Nothing, BackupSync.Plan(restored, restored, restored).Action);
    }

    [Fact]
    public async Task ABackupWithNoManifestBesideItIsBroughtDownRatherThanWrittenOver() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "here");

        // What an upload that stopped between its two files leaves behind, and what a manifest somebody removed
        // leaves behind: a real archive up there, and nothing beside it saying what is in it.
        fixture.Store.Put(CloudBackupFiles.Archive, [1, 2, 3]);

        var result = await fixture.Backup.PlanAsync(ancestor: null);

        // Read as "there is no backup in the cloud", this is an upload - a fresh launcher writing its own
        // database over somebody's backup, which nothing puts back. The archive carries its own manifest, so it
        // comes down instead and is read from there.
        Assert.Equal(BackupSyncAction.Download, result.Plan.Action);
        Assert.Contains("no manifest beside it", result.Plan.Reason);
    }

    [Fact]
    public async Task ACloudThatWillNotSayWhetherAnArchiveIsThereAsksRatherThanUploadingBlind() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "here");

        // No manifest to read, and no answer about the archive either. The one conclusion not to draw from a
        // Drive that will not answer is that there is nothing up there, so this stops rather than uploads.
        fixture.Store.FailExists = true;

        var result = await fixture.Backup.PlanAsync(ancestor: null);

        Assert.Equal(BackupSyncAction.Conflict, result.Plan.Action);
        Assert.Contains("could not be searched", result.Plan.Reason);
    }

    [Fact]
    public async Task AManifestThatWillNotReadAsksRatherThanOverwriting() {
        using var fixture = new Fixture();

        fixture.Store.Put(CloudBackupFiles.Manifest, System.Text.Encoding.UTF8.GetBytes("not a manifest"));

        var result = await fixture.Backup.PlanAsync(ancestor: null);

        // A manifest that could not be read is not "there is no backup up there": planning an upload would
        // overwrite a backup this machine simply could not see.
        Assert.Equal(BackupSyncAction.Conflict, result.Plan.Action);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadAsksRatherThanSayingItIsGone() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "here");

        var uploaded = fixture.Backup.BuildLocalManifest();
        await fixture.Backup.UploadAsync(uploaded);

        // Something else takes the launcher's database - Imlight, or a second launcher - and it is held the
        // way RavenDB holds it. A hash of a directory that cannot be read would look like a database that has
        // been emptied, and uploading that would tell the cloud the data had gone.
        using (new FileStream(Path.Combine(fixture.DataDirectory, "doc.txt"),
                   FileMode.Open, FileAccess.Read, FileShare.None)) {
            var result = await fixture.Backup.PlanAsync(uploaded);

            Assert.Equal(BackupSyncAction.Conflict, result.Plan.Action);
            Assert.NotNull(result.LocalProblem);
        }
    }

    [Fact]
    public async Task ACloudThatCannotBeReachedAsksRatherThanUploadingBlind() {
        using var fixture = new Fixture();
        fixture.Store.FailDownload = true;

        var result = await fixture.Backup.PlanAsync(ancestor: null);

        Assert.Equal(BackupSyncAction.Conflict, result.Plan.Action);
    }

    [Fact]
    public async Task ARefusedUploadLeavesNothingBehind() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "one");
        fixture.Store.FailUpload = true;

        var problem = await fixture.Backup.UploadAsync(fixture.Backup.BuildLocalManifest());

        Assert.NotNull(problem);
        Assert.Empty(fixture.Store.Names);
    }

    [Fact]
    public async Task ARestorePutsTheCloudsCopyBack() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "the backup");

        await fixture.Backup.UploadAsync(fixture.Backup.BuildLocalManifest());

        // The machine is rebuilt: the data directory goes away, as it would on a fresh install.
        Directory.Delete(fixture.DataDirectory, recursive: true);

        var (manifest, problem) = await fixture.Backup.DownloadAndRestoreAsync();

        Assert.Null(problem);
        Assert.NotNull(manifest);
        Assert.Equal("the backup", File.ReadAllText(Path.Combine(fixture.DataDirectory, "doc.txt")));
    }

    [Fact]
    public async Task ARestoreOfNothingLeavesTheDirectoryAlone() {
        using var fixture = new Fixture();
        fixture.Write("doc.txt", "here");

        var (manifest, problem) = await fixture.Backup.DownloadAndRestoreAsync();

        Assert.Null(manifest);
        Assert.NotNull(problem);
        Assert.Equal("here", File.ReadAllText(Path.Combine(fixture.DataDirectory, "doc.txt")));
    }

    /// <summary>A launcher's data directory, a fake cloud, and the backup that joins them.</summary>
    private sealed class Fixture : IDisposable {

        private readonly TempWorkspace _workspace = new();

        public Fixture() {
            DataDirectory = _workspace.CreateDirectory("database");

            Store = new FakeCloudBackupStore();

            Backup = new CloudBackup(
                Store,
                new Dictionary<string, string> { [BackupDatabases.Launcher] = DataDirectory },
                _workspace.CreateDirectory("work"));
        }

        public string DataDirectory { get; }

        public FakeCloudBackupStore Store { get; }

        public CloudBackup Backup { get; }

        public void Write(string name, string content) => File.WriteAllText(Path.Combine(DataDirectory, name), content);

        public void Dispose() => _workspace.Dispose();
    }
}
