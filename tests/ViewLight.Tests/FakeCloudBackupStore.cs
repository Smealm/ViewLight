using ViewLight.Services;

namespace ViewLight.Tests;

/// <summary>
/// An <see cref="ICloudBackupStore"/> that keeps its files in memory, so a sync can be driven end to end with
/// no Google in sight. The two failure switches are the point of it: a store that refuses a write, and one
/// whose manifest will not read, are the cases the sync has to get right without breaking anything.
/// </summary>
internal sealed class FakeCloudBackupStore : ICloudBackupStore {

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public string? Problem { get; set; }

    public bool SignedIn { get; set; } = true;

    /// <summary>When set, a download fails the way an unreachable Drive does.</summary>
    public bool FailDownload { get; set; }

    /// <summary>When set, an upload is refused.</summary>
    public bool FailUpload { get; set; }

    /// <summary>
    /// When set, a search fails while reads still work - a Drive that will answer for a file but not for
    /// whether one exists, which is the case that must not be read as "there is nothing up there".
    /// </summary>
    public bool FailExists { get; set; }

    public IReadOnlyCollection<string> Names => _files.Keys;

    public byte[]? Contents(string name) => _files.TryGetValue(name, out var bytes) ? bytes : null;

    public void Put(string name, byte[] contents) => _files[name] = contents;

    public Task<bool> IsSignedInAsync(CancellationToken token = default) => Task.FromResult(SignedIn);

    public Task<bool> ExistsAsync(string name, CancellationToken token = default) {
        if (!SignedIn) {
            Problem = "Not signed in.";
            return Task.FromResult(false);
        }

        if (FailDownload) {
            Problem = "The cloud could not be reached.";
            return Task.FromResult(false);
        }

        if (FailExists) {
            Problem = "The cloud could not be searched.";
            return Task.FromResult(false);
        }

        Problem = null;
        return Task.FromResult(_files.ContainsKey(name));
    }

    public Task<byte[]?> DownloadAsync(string name, CancellationToken token = default) {
        if (!SignedIn) {
            Problem = "Not signed in.";
            return Task.FromResult<byte[]?>(null);
        }

        if (FailDownload) {
            Problem = "The cloud could not be reached.";
            return Task.FromResult<byte[]?>(null);
        }

        Problem = null;

        // Not there is not a problem, and Problem stays null - the same contract the Drive store has.
        return Task.FromResult(_files.TryGetValue(name, out var bytes) ? bytes : null);
    }

    public Task<bool> UploadAsync(string name, byte[] content, CancellationToken token = default) {
        if (!SignedIn) {
            Problem = "Not signed in.";
            return Task.FromResult(false);
        }

        if (FailUpload) {
            Problem = "The cloud refused the write.";
            return Task.FromResult(false);
        }

        Problem = null;
        _files[name] = content;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken token = default) {
        Problem = null;
        _files.Remove(name);
        return Task.FromResult(true);
    }
}
