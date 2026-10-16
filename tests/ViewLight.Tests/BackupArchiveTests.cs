using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The backup itself: the database directories and a manifest in one zip. What matters is the round trip -
/// written, read back as the same thing, restored over a directory - and that a restore that cannot be trusted
/// changes nothing.
/// </summary>
public class BackupArchiveTests {

    [Fact]
    public async Task ABuiltArchiveHoldsTheManifestAndEveryFile() {
        using var workspace = new TempWorkspace();

        var launcher = workspace.CreateDirectory("launcher-data");
        File.WriteAllText(Path.Combine(launcher, "doc.txt"), "launcher");
        var server = workspace.CreateDirectory("server-data");
        Directory.CreateDirectory(Path.Combine(server, "sub"));
        File.WriteAllText(Path.Combine(server, "sub", "doc.txt"), "server");

        var manifest = ManifestFor(launcher, server);
        var archivePath = workspace.Path("backup.zip");

        var problem = await BackupArchive.CreateAsync(
            archivePath, manifest,
            new Dictionary<string, string> {
                [BackupDatabases.Launcher] = launcher,
                [BackupDatabases.Server] = server,
            });

        Assert.Null(problem);

        var contents = BackupArchive.Read(archivePath);

        Assert.NotNull(contents);
        Assert.True(contents!.Manifest.HasSameContent(manifest));
        Assert.Contains(BackupDatabases.Launcher, contents.Databases);
        Assert.Contains(BackupDatabases.Server, contents.Databases);
    }

    [Fact]
    public async Task ARestoreReplacesTheDirectoryWithTheArchivesCopy() {
        using var workspace = new TempWorkspace();

        var source = workspace.CreateDirectory("source");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "kept");
        File.WriteAllText(Path.Combine(source, "gone.txt"), "old");

        var archivePath = workspace.Path("backup.zip");
        await BackupArchive.CreateAsync(
            archivePath, ManifestFor(source, source),
            new Dictionary<string, string> { [BackupDatabases.Launcher] = source });

        var target = workspace.CreateDirectory("target");
        File.WriteAllText(Path.Combine(target, "stale.txt"), "not in the backup");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "changed since");

        var problem = await BackupArchive.RestoreAsync(
            archivePath, new Dictionary<string, string> { [BackupDatabases.Launcher] = target });

        Assert.Null(problem);

        // The restored directory is the backup's, not a merge of it with what was there.
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(target, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(target, "stale.txt")));

        // The staging directory does not survive the restore.
        Assert.False(Directory.Exists(target + BackupArchive.StagingSuffix));
    }

    [Fact]
    public async Task ADatabaseTheArchiveDoesNotHoldIsNotInvented() {
        using var workspace = new TempWorkspace();

        var launcher = workspace.CreateDirectory("launcher-data");
        File.WriteAllText(Path.Combine(launcher, "doc.txt"), "launcher");

        var archivePath = workspace.Path("backup.zip");

        // Only the launcher's notes are in the archive; the server's database was absent when it was taken.
        await BackupArchive.CreateAsync(
            archivePath, ManifestFor(launcher, null),
            new Dictionary<string, string> { [BackupDatabases.Launcher] = launcher });

        var targetLauncher = workspace.CreateDirectory("target-launcher");
        var targetServer = workspace.CreateDirectory("target-server");
        File.WriteAllText(Path.Combine(targetServer, "live.txt"), "a server that has run");

        var problem = await BackupArchive.RestoreAsync(
            archivePath,
            new Dictionary<string, string> {
                [BackupDatabases.Launcher] = targetLauncher,
                [BackupDatabases.Server] = targetServer,
            });

        Assert.Null(problem);

        // The launcher's notes were restored; the server's untouched, because the backup has nothing to say
        // about it. Restoring "the server has never run" must not delete a server that now has.
        Assert.True(File.Exists(Path.Combine(targetServer, "live.txt")));
    }

    [Fact]
    public async Task AnArchiveThatWillNotReadIsNoArchiveRatherThanAPartialOne() {
        using var workspace = new TempWorkspace();

        var notAnArchive = workspace.WriteFile("not-a-backup.zip", "this is not a zip file");
        Assert.Null(BackupArchive.Read(notAnArchive));

        // A restore of something that is not a backup touches nothing at all.
        var target = workspace.CreateDirectory("target");
        File.WriteAllText(Path.Combine(target, "live.txt"), "here");

        var problem = await BackupArchive.RestoreAsync(
            notAnArchive, new Dictionary<string, string> { [BackupDatabases.Launcher] = target });

        Assert.NotNull(problem);
        Assert.Equal("here", File.ReadAllText(Path.Combine(target, "live.txt")));
    }

    [Fact]
    public async Task AnArchiveWithNoManifestIsRefused() {
        using var workspace = new TempWorkspace();

        var empty = workspace.Path("empty.zip");
        using (var file = File.Create(empty)) {
            using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
            zip.CreateEntry("launcher/something.txt");
        }

        Assert.Null(BackupArchive.Read(empty));
    }

    [Fact]
    public async Task WritingIsAtomicallyReplaced() {
        using var workspace = new TempWorkspace();

        var directory = workspace.CreateDirectory("data");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "one");
        var archivePath = workspace.Path("backup.zip");

        await BackupArchive.CreateAsync(archivePath, ManifestFor(directory, directory),
            new Dictionary<string, string> { [BackupDatabases.Launcher] = directory });

        File.WriteAllText(Path.Combine(directory, "a.txt"), "two");
        await BackupArchive.CreateAsync(archivePath, ManifestFor(directory, directory),
            new Dictionary<string, string> { [BackupDatabases.Launcher] = directory });

        // The second write replaced the first rather than failing or merging, and no half-written file is left.
        Assert.False(File.Exists(archivePath + ".writing"));

        var restored = workspace.CreateDirectory("restored");
        await BackupArchive.RestoreAsync(archivePath, new Dictionary<string, string> { [BackupDatabases.Launcher] = restored });
        Assert.Equal("two", File.ReadAllText(Path.Combine(restored, "a.txt")));
    }

    private static BackupManifest ManifestFor(string? launcher, string? server) {
        var manifest = new BackupManifest { TakenAt = DateTimeOffset.UtcNow };

        foreach (var (name, directory) in new[] { (BackupDatabases.Launcher, launcher), (BackupDatabases.Server, server) }) {
            manifest.Databases[name] = DirectoryFingerprint.Of(directory).State;
        }

        return manifest;
    }
}
