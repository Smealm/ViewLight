using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// A database directory as one content hash. "Unchanged" has to mean unchanged for a sync to ever be quiet,
/// so what this pins down is that the hash follows the bytes and the paths, and nothing else.
/// </summary>
public class DirectoryFingerprintTests {

    [Fact]
    public void TheSameContentHashesTheSame() {
        using var workspace = new TempWorkspace();

        var one = workspace.CreateDirectory("one");
        File.WriteAllText(Path.Combine(one, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(one, "nested"));
        File.WriteAllText(Path.Combine(one, "nested", "b.txt"), "world");

        var two = workspace.CreateDirectory("two");
        Directory.CreateDirectory(Path.Combine(two, "nested"));
        File.WriteAllText(Path.Combine(two, "nested", "b.txt"), "world");
        File.WriteAllText(Path.Combine(two, "a.txt"), "hello");

        // Written in a different order, in a different location: the same three facts, so the same hash.
        Assert.Equal(DirectoryFingerprint.Of(one).State.Hash, DirectoryFingerprint.Of(two).State.Hash);
    }

    [Fact]
    public void TouchingAFileDoesNotChangeTheHash() {
        using var workspace = new TempWorkspace();

        var directory = workspace.CreateDirectory("data");
        var file = Path.Combine(directory, "a.txt");
        File.WriteAllText(file, "hello");

        var before = DirectoryFingerprint.Of(directory).State.Hash;

        // A RavenDB rewrites its own bookkeeping constantly; a hash that moved with the mtimes would say
        // "changed" every time the launcher started.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(1));

        Assert.Equal(before, DirectoryFingerprint.Of(directory).State.Hash);
    }

    [Fact]
    public void DifferentBytesAreADifferentHash() {
        using var workspace = new TempWorkspace();

        var directory = workspace.CreateDirectory("data");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello");

        var before = DirectoryFingerprint.Of(directory).State.Hash;

        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello!");
        var after = DirectoryFingerprint.Of(directory).State.Hash;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AFilesPathIsPartOfItsIdentity() {
        using var workspace = new TempWorkspace();

        var directory = workspace.CreateDirectory("data");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "same");

        var flat = DirectoryFingerprint.Of(directory).State.Hash;

        Directory.CreateDirectory(Path.Combine(directory, "moved"));
        File.Move(Path.Combine(directory, "a.txt"), Path.Combine(directory, "moved", "a.txt"));

        // Same bytes, different name: not the same database.
        Assert.NotEqual(flat, DirectoryFingerprint.Of(directory).State.Hash);
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsAbsentRatherThanAnError() {
        using var workspace = new TempWorkspace();

        var result = DirectoryFingerprint.Of(workspace.Path("nowhere"));

        Assert.False(result.State.Present);
        Assert.Equal(string.Empty, result.State.Hash);
        Assert.Null(result.Problem);

        // A launcher with no folder to point at is the same answer as one whose folder has not been made yet.
        Assert.False(DirectoryFingerprint.Of(null).State.Present);
    }

    [Fact]
    public void ADirectoryThatCannotBeReadSaysSoRatherThanLookingAbsent() {
        using var workspace = new TempWorkspace();
        var directory = workspace.CreateDirectory("data");
        var path = Path.Combine(directory, "held.bin");
        File.WriteAllText(path, "locked");

        // A database another process has open is exactly this: there, and impossible to read. On Windows a
        // file held with no sharing is what makes File.OpenRead throw - the same shape a RavenDB lock has.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
            var result = DirectoryFingerprint.Of(directory);

            // Not the same answer as "the directory is not there": the reason is what stops a sync treating a
            // locked database as one that has been deleted and uploading that.
            Assert.NotNull(result.Problem);
        }

        // Readable again, it hashes as usual - the failure was the lock, not the directory.
        Assert.Null(DirectoryFingerprint.Of(directory).Problem);
    }

    [Fact]
    public void AnEmptyDirectoryIsPresentWithNoFiles() {
        using var workspace = new TempWorkspace();

        var result = DirectoryFingerprint.Of(workspace.CreateDirectory("empty"));

        Assert.True(result.State.Present);
        Assert.Equal(0, result.State.Files);
        Assert.Equal(0, result.State.Bytes);
    }

    [Fact]
    public void TheSizeIsTheSumOfWhatItHolds() {
        using var workspace = new TempWorkspace();
        var directory = workspace.CreateDirectory("data");

        File.WriteAllBytes(Path.Combine(directory, "a.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(directory, "b.bin"), new byte[11]);

        var state = DirectoryFingerprint.Of(directory).State;

        Assert.Equal(1245, state.Bytes);
        Assert.Equal(2, state.Files);
    }
}
