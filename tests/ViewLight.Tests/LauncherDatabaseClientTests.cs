using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher's own database over HTTP, driven against answers rather than against a database: what it asks
/// for, what it makes of what comes back, and what it says when the answer is a refusal. The shapes themselves
/// - a listing wrapped in <c>Results</c>, a document by id, an attachment - were checked against a real
/// RavenDB; these are the rules the client puts on top of them.
/// </summary>
public class LauncherDatabaseClientTests {

    private const int Port = 28099;

    [Fact]
    public async Task TheDatabaseIsMadeWhenItIsNotThereYet() {
        var handler = Answer(request => request.Method == "PUT"
            ? Json(HttpStatusCode.Created, "{}")

            // The existence probe, before anything is made.
            : Json(HttpStatusCode.NotFound, "{}"));

        using var client = Client(handler);

        Assert.True(await client.EnsureDatabaseAsync());
        Assert.Null(client.Problem);

        Assert.Equal(2, handler.Requests.Count);

        // It looks for the database first - one document is enough to answer that - and only then makes one.
        Assert.Equal($"/databases/{LauncherDatabase.DatabaseName}/docs", new Uri(handler.Requests[0].Url).AbsolutePath);
        Assert.Equal("1", handler.Requests[0].Query("pageSize"));
        Assert.Equal("/admin/databases", new Uri(handler.Requests[1].Url).AbsolutePath);
        Assert.Equal(LauncherDatabase.DatabaseName, handler.Requests[1].Query("name"));
    }

    [Fact]
    public async Task ADatabaseAlreadyThereIsNotMadeTwice() {
        var handler = Answer(_ => Json(HttpStatusCode.OK, "{}"));

        using var client = Client(handler);

        Assert.True(await client.EnsureDatabaseAsync());
        Assert.Single(handler.Requests);
        Assert.Equal("GET", handler.Requests[0].Method);
    }

    [Fact]
    public async Task ARefusedCreationIsAProblemRatherThanAnException() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.InternalServerError, "no")));

        Assert.False(await client.EnsureDatabaseAsync());
        Assert.Contains("could not be created", client.Problem);
    }

    [Fact]
    public async Task ADocumentListingIsReadOutOfItsResultsWrapper() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.OK, """
            {"Results":[{"AccountId":42,"@metadata":{"@collection":"Profiles"}},
                        {"ZoneWorlds":{"A/A":"A"},"@metadata":{"@collection":"ZoneWorlds"}}],
             "TotalResults":2}
            """)));

        var documents = await client.ReadDocumentsAsync();

        Assert.NotNull(documents);
        Assert.Equal(2, documents!.Count);
        Assert.Equal(42UL, documents[0].GetProperty("AccountId").GetUInt64());
        Assert.Equal("ZoneWorlds", documents[1].GetProperty("@metadata").GetProperty("@collection").GetString());
    }

    [Fact]
    public async Task AListingIsReadAPageAtATimeAndNotJustTheFirstPage() {
        // A launcher with more profiles than fit in one page must not silently lose the rest of them.
        var handler = Answer((request, _) => {
            var start = int.Parse(request.Query("start") ?? "0");

            if (start > 0) {
                return Json(HttpStatusCode.OK, """{"Results":[{"AccountId":2}]}""");
            }

            // A full first page, which is what says there may be more.
            var page = string.Join(',', Enumerable.Range(0, 512).Select(_ => "{\"AccountId\":1}"));

            return Json(HttpStatusCode.OK, $"{{\"Results\":[{page}]}}");
        });

        using var client = Client(handler);

        var documents = await client.ReadDocumentsAsync();

        Assert.Equal(513, documents!.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("512", handler.Requests[1].Query("start"));
    }

    [Fact]
    public async Task AnAnswerThatIsNotADocumentListingIsAProblem() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.OK, """{"SomethingElse":true}""")));

        Assert.Null(await client.ReadDocumentsAsync());
        Assert.Contains("not a document listing", client.Problem);
    }

    [Fact]
    public async Task ADatabaseThatIsDownIsAProblemRatherThanAnException() {
        using var client = Client(Answer(_ => throw new HttpRequestException("nothing is listening")));

        Assert.Null(await client.ReadDocumentsAsync());
        Assert.Contains("did not answer as a database server", client.Problem);
    }

    [Fact]
    public async Task ADocumentIsWrittenByItsId() {
        var handler = Answer(_ => Json(HttpStatusCode.Created, "{}"));

        using var client = Client(handler);

        Assert.True(await client.WriteDocumentAsync("Profiles/42", new JsonObject { ["AccountId"] = 42 }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal($"/databases/{LauncherDatabase.DatabaseName}/docs", new Uri(request.Url).AbsolutePath);
        Assert.Equal("Profiles/42", request.Query("id"));
        Assert.Contains("\"AccountId\":42", request.Body);
    }

    [Fact]
    public async Task ARefusedWriteSaysWhatTheDatabaseSaid() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.Conflict,
            "Concurrency conflict\nOn document Profiles/42")));

        Assert.False(await client.WriteDocumentAsync("Profiles/42", new JsonObject()));

        // What the database said, flattened onto one line for a sentence in the Log.
        Assert.Contains("Concurrency conflict On document Profiles/42", client.Problem);
    }

    [Fact]
    public async Task ARefusedWriteDoesNotSayAParagraph() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.Conflict, new string('x', 500))));

        Assert.False(await client.WriteDocumentAsync("Profiles/42", new JsonObject()));

        // Clipped: a status line in the Log is not the place for half a page of diagnostics.
        Assert.True(client.Problem!.Length < 400, client.Problem);
        Assert.Contains("…", client.Problem);
    }

    [Fact]
    public async Task OneDocumentIsFetchedWithoutReadingTheRest() {
        var handler = Answer(_ => Json(HttpStatusCode.OK,
            """{"Results":[{"AccountId":42,"Username":"jordan"}]}"""));

        using var client = Client(handler);

        var document = await client.ReadDocumentAsync("Profiles/42");

        Assert.NotNull(document);
        Assert.Equal("jordan", document!.Value.GetProperty("Username").GetString());
        Assert.Equal("Profiles/42", handler.Requests[0].Query("id"));
    }

    [Fact]
    public async Task ADocumentThatIsNotThereIsNotAProblem() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.OK, """{"Results":[]}""")));

        Assert.Null(await client.ReadDocumentAsync("Profiles/42"));
        Assert.Null(client.Problem);
    }

    [Fact]
    public async Task APictureTravelsAsAnAttachmentOnTheDocument() {
        var picture = new byte[] { 1, 2, 3, 4 };
        var handler = Answer(request => request.Method == "GET"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) }
            : Json(request.Method == "DELETE" ? HttpStatusCode.NoContent : HttpStatusCode.Created, string.Empty));

        using var client = Client(handler);

        Assert.True(await client.PutAttachmentAsync("Profiles/42", "avatar.png", picture));
        Assert.Equal(picture, await client.ReadAttachmentAsync("Profiles/42", "avatar.png"));
        Assert.True(await client.DeleteAttachmentAsync("Profiles/42", "avatar.png"));

        Assert.All(handler.Requests, request => {
            Assert.Equal($"/databases/{LauncherDatabase.DatabaseName}/attachments", new Uri(request.Url).AbsolutePath);
            Assert.Equal("Profiles/42", request.Query("id"));
            Assert.Equal("avatar.png", request.Query("name"));
        });

        Assert.Equal(["PUT", "GET", "DELETE"], handler.Requests.Select(request => request.Method));

        // The bytes that were sent, not a string of them.
        Assert.Equal(picture, handler.Requests[0].Content);
    }

    [Fact]
    public async Task AProfileWithNoPictureIsNotAProblem() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.NotFound, string.Empty)));

        // The ordinary case for a profile nobody has given a picture to: nothing there, nothing wrong.
        Assert.Null(await client.ReadAttachmentAsync("Profiles/42", "avatar.png"));
        Assert.Null(client.Problem);
    }

    [Fact]
    public async Task RemovingSomethingThatIsAlreadyGoneIsTheOutcomeAskedFor() {
        using var client = Client(Answer(_ => Json(HttpStatusCode.NotFound, string.Empty)));

        Assert.True(await client.DeleteAttachmentAsync("Profiles/42", "avatar.png"));
        Assert.True(await client.DeleteDocumentAsync("Profiles/42"));
        Assert.Null(client.Problem);
    }

    private static LauncherDatabaseClient Client(RecordingHttpHandler handler)
        => new(Port, http: new HttpClient(handler));

    private static RecordingHttpHandler Answer(Func<RecordedRequest, HttpResponseMessage> responder)
        => new((request, _) => responder(request));

    private static RecordingHttpHandler Answer(Func<RecordedRequest, int, HttpResponseMessage> responder)
        => new(responder);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
