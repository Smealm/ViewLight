using ViewLight.Models;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The manifest a backup carries: for every database, its content hash and the time it was taken. What the
/// sync compares is the hashes, so the equality rule is the thing worth pinning down - and the time is
/// deliberately not part of it.
/// </summary>
public class BackupManifestTests {

    [Fact]
    public void AManifestSurvivesBeingWrittenAndRead() {
        var manifest = new BackupManifest {
            TakenAt = DateTimeOffset.Parse("2026-05-01T10:00:00+00:00"),
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("abc123", 2048, 7),
                [BackupDatabases.Server] = new BackupDatabaseState("def456", 4096, 9),
            },
        };

        var read = BackupManifest.TryParse(manifest.ToJson());

        Assert.NotNull(read);
        Assert.Equal(BackupManifest.CurrentVersion, read!.Version);
        Assert.Equal(manifest.TakenAt, read.TakenAt);
        Assert.Equal("abc123", read.For(BackupDatabases.Launcher)!.Hash);
        Assert.Equal(4096, read.For(BackupDatabases.Server)!.Bytes);
        Assert.Equal(9, read.For(BackupDatabases.Server)!.Files);
    }

    [Fact]
    public void TheSameContentIsTheSameWhateverTheTimeAndSize() {
        var first = Manifest(("h1", 100, 1), ("h2", 200, 2));
        first.TakenAt = DateTimeOffset.Parse("2026-05-01T10:00:00+00:00");

        var second = Manifest(("h1", 999, 99), ("h2", 111, 11));
        second.TakenAt = DateTimeOffset.Parse("2026-06-01T10:00:00+00:00");

        // Same hashes, different times and different recorded sizes: the same data, and a sync that treated
        // them as different would upload the same bytes again for nothing.
        Assert.True(first.HasSameContent(second));
    }

    [Fact]
    public void ADifferentHashIsDifferentContent() {
        var first = Manifest(("h1", 100, 1), ("h2", 200, 2));
        var second = Manifest(("h1", 100, 1), ("h2-changed", 200, 2));

        Assert.False(first.HasSameContent(second));
    }

    [Fact]
    public void ADatabaseThatWasNotThereIsNotTheSameAsOneWithNoFiles() {
        var absent = new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("h1"),
                [BackupDatabases.Server] = BackupDatabaseState.Absent,
            },
        };

        var empty = new BackupManifest {
            Databases = {
                [BackupDatabases.Launcher] = new BackupDatabaseState("h1"),
                [BackupDatabases.Server] = new BackupDatabaseState(string.Empty, 0, 0),
            },
        };

        // "The server has never run" and "the server's database is empty" are different states, and a restore
        // of the first must not invent an empty database beside the launcher's notes.
        Assert.False(absent.HasSameContent(empty));
    }

    [Fact]
    public void AManifestThatWillNotReadIsNoManifestRatherThanAGuess() {
        Assert.Null(BackupManifest.TryParse(null));
        Assert.Null(BackupManifest.TryParse(string.Empty));
        Assert.Null(BackupManifest.TryParse("not json at all"));
        Assert.Null(BackupManifest.TryParse("[1,2,3]"));
    }

    [Fact]
    public void TwoEmptyManifestsAgree() {
        // Nothing to back up on either side is the honest answer for two empty sets, not a difference.
        Assert.True(new BackupManifest().HasSameContent(new BackupManifest()));
    }

    private static BackupManifest Manifest(params (string Hash, long Bytes, int Files)[] databases) {
        var manifest = new BackupManifest();

        var names = new[] { BackupDatabases.Launcher, BackupDatabases.Server };

        for (var i = 0; i < databases.Length && i < names.Length; i++) {
            manifest.Databases[names[i]] =
                new BackupDatabaseState(databases[i].Hash, databases[i].Bytes, databases[i].Files);
        }

        return manifest;
    }
}
