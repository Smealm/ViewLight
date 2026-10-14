using System.Net;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The one request the launcher makes to a patch server: <c>GET /revisions</c>, and what a patch server
/// that does not answer it looks like from here.
/// </summary>
/// <remarks>
/// Nothing about whether the files behind a revision have arrived is asked any more - a patch server
/// serves what it has - so the whole conversation with one is this list, and it is pinned down here.
/// </remarks>
public class PatchServerRevisionsTests {

    private const string HeldRevision = "V_r806919.Wizard_1_610";
    private const string ServerRevision = "V_r803238.Wizard_1_610";

    private static FakeHttpHandler Answering(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });

    [Fact]
    public async Task ListsTheRevisionsAPatchServerNames() {
        var handler = Answering($"""["{HeldRevision}","{ServerRevision}"]""");

        var revisions = await new PatchServerRevisions(new HttpClient(handler))
            .ListAsync("http://127.0.0.1:12369");

        // The order the server lists them in is what comes back: which of them is newest is the mirror's
        // question, not this one's.
        Assert.Equal([HeldRevision, ServerRevision], revisions);
    }

    [Fact]
    public async Task AsksTheServersOwnRevisionsRoute() {
        // A trailing slash on the configured address is not a second request, and not a double slash.
        var handler = Answering($"""["{HeldRevision}"]""");

        var revisions = await new PatchServerRevisions(new HttpClient(handler))
            .ListAsync("http://127.0.0.1:12370/");

        Assert.Equal([HeldRevision], revisions);
        Assert.Equal(["http://127.0.0.1:12370/revisions"], handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task APatchServerThatRefusedTheQuestionAnswersWithNothing(HttpStatusCode status) {
        var revisions = await new PatchServerRevisions(new HttpClient(Answering(string.Empty, status)))
            .ListAsync("http://127.0.0.1:12369");

        Assert.Empty(revisions);
    }

    [Theory]
    // A body that is not a list at all, a list holding things that are not names, and something that is
    // not JSON at all - which is what an HTML error page looks like.
    [InlineData("""{"revisions":["V_r806919.Wizard_1_610"]}""")]
    [InlineData("[1,2,3]")]
    [InlineData("<html><body>404</body></html>")]
    public async Task AnAnswerThatIsNotAListOfNamesIsNoRevisionAtAll(string body) {
        var revisions = await new PatchServerRevisions(new HttpClient(Answering(body)))
            .ListAsync("http://127.0.0.1:12369");

        // Nothing usable came out of it: whatever the body was, no name in it stands for a game revision.
        Assert.All(revisions, name => Assert.Null(PatchServerConfig.GameRevisionFrom(name)));
    }

    [Fact]
    public async Task EntriesThatAreNotNamesAreDroppedAndTheRestAreKept() {
        var revisions = await new PatchServerRevisions(new HttpClient(
                Answering($"""["", null, "{HeldRevision}"]""")))
            .ListAsync("http://127.0.0.1:12369");

        // An empty name is not a revision, and a null is not a name at all - neither is handed on as a
        // blank the mirror would then have to make sense of.
        Assert.Equal([HeldRevision], revisions);
    }

    [Fact]
    public async Task AnAddressThatIsNotOneIsNeverAsked() {
        var handler = Answering($"""["{HeldRevision}"]""");
        var revisions = new PatchServerRevisions(new HttpClient(handler));

        Assert.Empty(await revisions.ListAsync(null));
        Assert.Empty(await revisions.ListAsync(string.Empty));
        Assert.Empty(await revisions.ListAsync("   "));
        Assert.Empty(await revisions.ListAsync("127.0.0.1:12369/not-a-url"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task APatchServerThatIsNotThereAnswersWithNothing() {
        // Unreachable, which is what a patch server that has not been started looks like.
        var unreachable = new PatchServerRevisions(FakeHttpHandler.Client(
            _ => throw new HttpRequestException("no connection could be made")));

        Assert.Empty(await unreachable.ListAsync("http://127.0.0.1:12369"));
    }
}
