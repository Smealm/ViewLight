using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The one question the launcher asks a patch server about its files: whether the revision a session is
/// about to ask for can be served yet.
/// </summary>
/// <remarks>
/// It is asked because a patch server that has just started cannot answer it: Aurorium writes a revision
/// and its whole asset list into its database before it downloads anything, so a fresh one lists a
/// revision whose files are not there, and Imlight's first request - <c>Root.wad</c> - is the 404 that
/// stops it. The answer is reported, not obeyed; what is pinned down here is the question.
/// </remarks>
public class PatchServerServingTests {

    private const string HeldRevision = "r806919.Wizard_1_610";

    [Fact]
    public void AsksForTheFileImlightLoadsFirst() {
        // Imlight builds {PatchServerInternalUrl}/V_{GameRevision}/... itself, so this is that address.
        Assert.Equal(
            "http://127.0.0.1:12369/V_r806919.Wizard_1_610/Data/GameData/Root.wad",
            PatchServerServing.BootFileUrlFor("http://127.0.0.1:12369", HeldRevision));

        // A trailing slash on the configured address is not a double slash, and a revision that already
        // carries the prefix is not given a second one.
        Assert.Equal(
            "http://127.0.0.1:12369/V_r806919.Wizard_1_610/Data/GameData/Root.wad",
            PatchServerServing.BootFileUrlFor("http://127.0.0.1:12369/", "V_r806919.Wizard_1_610"));

        Assert.Null(PatchServerServing.BootFileUrlFor(null, HeldRevision));
        Assert.Null(PatchServerServing.BootFileUrlFor("127.0.0.1:12369", HeldRevision));
        Assert.Null(PatchServerServing.BootFileUrlFor("http://127.0.0.1:12369", null));
        Assert.Null(PatchServerServing.BootFileUrlFor("http://127.0.0.1:12369", "  "));
    }

    [Fact]
    public async Task APatchServerServingTheRevisionIsServing() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent([0]),
        });

        Assert.True(await new PatchServerServing(new HttpClient(handler))
            .IsServingAsync("http://127.0.0.1:12369", HeldRevision));

        Assert.Equal(
            ["http://127.0.0.1:12369/V_r806919.Wizard_1_610/Data/GameData/Root.wad"],
            handler.Requests);
    }

    [Theory]
    // The 404 that stops a session, a server that answers something else about it, and a revision the
    // patch server does not hold at all - which is the same 404 from here.
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task APatchServerThatWillNotHandOverTheFileIsNotServing(HttpStatusCode status) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(status));

        Assert.False(await new PatchServerServing(new HttpClient(handler))
            .IsServingAsync("http://127.0.0.1:12369", HeldRevision));
    }

    [Fact]
    public async Task APatchServerThatIsNotThereIsNotServing() {
        var unreachable = new PatchServerServing(FakeHttpHandler.Client(
            _ => throw new HttpRequestException("no connection could be made")));

        Assert.False(await unreachable.IsServingAsync("http://127.0.0.1:12369", HeldRevision));
    }

    [Fact]
    public async Task AnAddressOrRevisionThatIsNotOneIsNeverAsked() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var serving = new PatchServerServing(new HttpClient(handler));

        Assert.False(await serving.IsServingAsync(null, HeldRevision));
        Assert.False(await serving.IsServingAsync(string.Empty, HeldRevision));
        Assert.False(await serving.IsServingAsync("   ", HeldRevision));
        Assert.False(await serving.IsServingAsync("127.0.0.1:12369/not-a-url", HeldRevision));
        Assert.False(await serving.IsServingAsync("http://127.0.0.1:12369", null));
        Assert.False(await serving.IsServingAsync("http://127.0.0.1:12369", "   "));

        Assert.Empty(handler.Requests);
    }
}
