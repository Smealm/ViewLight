using System.Net;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The Drive store, checked against what it asks Drive for rather than against Drive: the app-only folder on
/// every call, one file per name found before it is replaced, and a missing file treated as an ordinary state
/// rather than a failure.
/// </summary>
public class GoogleDriveBackupStoreTests {

    [Fact]
    public async Task EveryCallNamesTheApplicationsOwnFolder() {
        var handler = new RecordingHttpHandler(Listing([]));
        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        await store.DownloadAsync(CloudBackupFiles.Manifest);

        // spaces=appDataFolder is the whole safety property: the folder belongs to the application, the user
        // cannot see it, and nothing here can reach a file outside it.
        Assert.Equal("appDataFolder", handler.Requests[0].Query("spaces"));
        Assert.Contains("name = 'manifest.json'", handler.Requests[0].Query("q"));
    }

    [Fact]
    public async Task AMissingFileIsAnOrdinaryAnswerNotAFailure() {
        var store = new GoogleDriveBackupStore(Tokens(), RecordingHttpHandler.Client(Listing([])));

        var bytes = await store.DownloadAsync(CloudBackupFiles.Archive);

        Assert.Null(bytes);

        // Not there is not a problem: a Drive that has never been written to is the first backup's normal state.
        Assert.Null(store.Problem);
    }

    [Fact]
    public async Task ADownloadFetchesTheFileTheListingNamed() {
        var handler = new RecordingHttpHandler((request, _) => request.Method == "GET" && request.Path.Contains("/files?")
            ? Listing([("FILE-1", "manifest.json")])(request, 1)
            : Bytes([1, 2, 3]));

        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        var bytes = await store.DownloadAsync(CloudBackupFiles.Manifest);

        Assert.Equal([1, 2, 3], bytes);

        // The media download goes through the id the search found, with alt=media - not the name again.
        Assert.Contains("/files/FILE-1", handler.Requests[1].Path);
        Assert.Equal("media", handler.Requests[1].Query("alt"));
    }

    [Fact]
    public async Task AFirstUploadCreatesTheFileInTheAppFolder() {
        var handler = new RecordingHttpHandler((request, _) =>
            request.Method == "GET" ? Listing([])(request, 1) : Ok("{\"id\":\"NEW\"}"));

        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        var ok = await store.UploadAsync(CloudBackupFiles.Manifest, Encoding.UTF8.GetBytes("{}"));

        Assert.True(ok);

        var create = handler.Requests[1];

        Assert.Equal("POST", create.Method);
        Assert.Equal("multipart", create.Query("uploadType"));

        // Two parts: the metadata that puts the file in the app folder, and the bytes.
        Assert.Contains("\"name\":\"manifest.json\"", create.Body);
        Assert.Contains("\"parents\":[\"appDataFolder\"]", create.Body);
        Assert.Contains("{}", create.Body);
    }

    [Fact]
    public async Task ASecondUploadReplacesTheFileItFound() {
        var handler = new RecordingHttpHandler((request, _) => request.Method == "GET"
            ? Listing([("FILE-1", "backup.zip")])(request, 1)
            : Ok("{\"id\":\"FILE-1\"}"));

        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        var ok = await store.UploadAsync(CloudBackupFiles.Archive, [9, 9]);

        Assert.True(ok);

        var replace = handler.Requests[1];

        // Drive has no "replace": the file that was found is updated, keeping its id and its place.
        Assert.Equal("PATCH", replace.Method);
        Assert.Contains("/files/FILE-1", replace.Path);
        Assert.Equal("media", replace.Query("uploadType"));
        Assert.Equal([9, 9], replace.Content);
    }

    [Fact]
    public async Task DeletingSomethingThatIsNotThereIsDone() {
        var handler = new RecordingHttpHandler(Listing([]));
        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        var ok = await store.DeleteAsync(CloudBackupFiles.Archive);

        Assert.True(ok);

        // Nothing to delete, so nothing was deleted - and no DELETE was sent.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DeletingSomethingThatIsThereRemovesIt() {
        var handler = new RecordingHttpHandler((request, _) => request.Method == "GET"
            ? Listing([("FILE-1", "backup.zip")])(request, 1)
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        var store = new GoogleDriveBackupStore(Tokens(), new HttpClient(handler));

        Assert.True(await store.DeleteAsync(CloudBackupFiles.Archive));

        Assert.Equal("DELETE", handler.Requests[1].Method);
        Assert.Contains("/files/FILE-1", handler.Requests[1].Path);
    }

    [Fact]
    public async Task ASignedOutStoreSaysSoAndTouchesNothing() {
        var handler = new RecordingHttpHandler(Listing([]));
        var store = new GoogleDriveBackupStore(
            _ => Task.FromResult<(GoogleTokens?, string?)>((null, "Not signed in.")),
            new HttpClient(handler));

        Assert.False(await store.IsSignedInAsync());
        Assert.Null(await store.DownloadAsync(CloudBackupFiles.Archive));
        Assert.False(await store.UploadAsync(CloudBackupFiles.Archive, [1]));
        Assert.Equal("Not signed in.", store.Problem);

        // No call reached the network at all.
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------ helpers

    private static Func<CancellationToken, Task<(GoogleTokens? Tokens, string? Problem)>> Tokens()
        => _ => Task.FromResult<(GoogleTokens?, string?)>(
            (new GoogleTokens("access-token", null, DateTimeOffset.UtcNow.AddHours(1)), null));

    private static Func<RecordedRequest, int, HttpResponseMessage> Listing(
        (string Id, string Name)[] files) {
        var json = "{\"files\":[" + string.Join(',',
            files.Select(f => $"{{\"id\":\"{f.Id}\",\"name\":\"{f.Name}\"}}")) + "]}";

        return (_, _) => Ok(json);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] payload) => new(HttpStatusCode.OK) {
        Content = new ByteArrayContent(payload),
    };
}
