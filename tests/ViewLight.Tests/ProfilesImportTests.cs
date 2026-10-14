using System.Net;
using System.Text;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The one-time move of an existing launcher's notes out of <c>profiles.json</c> and into its database. The
/// rules that matter are all about running exactly once and about not touching a file it cannot trust: a
/// launcher that arrives with years of playtime must not arrive empty, and a file restored out of an old
/// backup must not be poured over newer notes.
/// </summary>
public class ProfilesImportTests {

    private const int Port = 28099;

    [Fact]
    public async Task AFileOfNotesIsMovedIntoTheDatabaseAndKept() {
        using var workspace = new TempWorkspace();
        LauncherPaths.DataRoot = workspace.Root;

        WriteNotes(workspace, avatar: "avatar.png");
        workspace.WriteFile(Path.Combine("profiles", "42", "avatar.png"), [1, 2, 3, 4]);

        var handler = Answer();

        var result = await ProfilesImport.RunAsync(Client(handler), workspace.Root);

        Assert.Equal(ProfilesImportState.Imported, result.State);
        Assert.Equal(1, result.Profiles);
        Assert.Equal(1, result.Pictures);

        var written = handler.Requests.Where(request => request.Method == "PUT").ToList();

        // The profile, the zone-to-world map, the picture, and the marker that says it was done.
        Assert.Contains(written, request => request.Query("id") == "Profiles/42");
        Assert.Contains(written, request => request.Query("id") == "ZoneWorlds/all");
        Assert.Contains(written, request => request.Query("id") == ProfilesImport.DocumentId);
        Assert.Contains(written, request => request.Query("name") == "avatar.png"
                                            && request.Content.SequenceEqual(new byte[] { 1, 2, 3, 4 }));

        // Tidied rather than deleted: the file being dealt with is the point, and the build that wrote it can
        // still read this.
        Assert.False(File.Exists(workspace.Path(ProfilesImport.FileName)));
        Assert.True(File.Exists(workspace.Path(ProfilesImport.ImportedFileName)));

        var kept = new ProfileStore(workspace.Path(ProfilesImport.ImportedFileName));
        var profile = Assert.Single(kept.Profiles);

        Assert.Equal(42UL, profile.AccountId);
        Assert.Equal(90, profile.PlaytimeSeconds);
        Assert.Equal("WizardCity", kept.ZoneWorlds["WizardCity/WC_Hub"]);
    }

    [Fact]
    public async Task AFileThatWasAlreadyMovedIsLeftAloneEvenWhenItHasNewerNotes() {
        using var workspace = new TempWorkspace();
        LauncherPaths.DataRoot = workspace.Root;

        WriteNotes(workspace, avatar: null);

        // The database says the file has been read before, which is what a restored backup looks like: it was
        // taken after this launcher had moved its notes.
        var handler = new RecordingHttpHandler((request, _) => request.Method == "GET"
            ? Json(HttpStatusCode.OK,
                """{"Results":[{"ImportedAt":"2026-01-01T00:00:00+00:00","@metadata":{"@collection":"LauncherData"}}]}""")
            : Json(HttpStatusCode.Created, "{}"));

        var result = await ProfilesImport.RunAsync(Client(handler), workspace.Root);

        Assert.Equal(ProfilesImportState.NotNeeded, result.State);

        // Nothing written at all, and the file left where it is: pouring it over the newer notes in the
        // database is exactly what the marker exists to prevent.
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.True(File.Exists(workspace.Path(ProfilesImport.FileName)));
    }

    [Fact]
    public async Task ALauncherWithNoFileIsMarkedAsHavingNothingToMove() {
        using var workspace = new TempWorkspace();
        LauncherPaths.DataRoot = workspace.Root;

        var handler = Answer();

        var result = await ProfilesImport.RunAsync(Client(handler), workspace.Root);

        Assert.Equal(ProfilesImportState.NotNeeded, result.State);

        // It looked for the marker and then wrote it, and did nothing else: a file that turns up later - out
        // of an old backup - must not be treated as notes this launcher has never seen.
        Assert.Equal("GET", handler.Requests[0].Method);

        var written = Assert.Single(handler.Requests, request => request.Method == "PUT");
        Assert.Equal(ProfilesImport.DocumentId, written.Query("id"));
    }

    [Fact]
    public async Task AFileThatWillNotReadChangesNothingAtAll() {
        using var workspace = new TempWorkspace();
        LauncherPaths.DataRoot = workspace.Root;

        workspace.WriteFile(ProfilesImport.FileName, "{ this is not the file it was");

        var handler = Answer();

        var result = await ProfilesImport.RunAsync(Client(handler), workspace.Root);

        Assert.Equal(ProfilesImportState.Unreadable, result.State);
        Assert.NotNull(result.Message);

        // Nothing written, nothing renamed: the notes are still there, and still readable by the build that
        // wrote them.
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.True(File.Exists(workspace.Path(ProfilesImport.FileName)));
        Assert.False(File.Exists(workspace.Path(ProfilesImport.ImportedFileName)));
    }

    [Fact]
    public async Task APictureThatIsNoLongerThereDoesNotStopTheRest() {
        using var workspace = new TempWorkspace();
        LauncherPaths.DataRoot = workspace.Root;

        // A profile naming a picture whose file is gone - a folder cleaned out by hand, or a profile written on
        // another machine.
        WriteNotes(workspace, avatar: "avatar.png");

        var result = await ProfilesImport.RunAsync(Client(Answer()), workspace.Root);

        Assert.Equal(ProfilesImportState.Imported, result.State);
        Assert.Equal(1, result.Profiles);
        Assert.Equal(0, result.Pictures);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An old launcher's notes, written the way that launcher wrote them.</summary>
    private static void WriteNotes(TempWorkspace workspace, string? avatar) {
        var notes = new ProfileStore(workspace.Path(ProfilesImport.FileName));
        var profile = notes.GetOrCreate(42, "jordan");

        profile.PlaytimeSeconds = 90;
        profile.SessionCount = 3;
        profile.AvatarFile = avatar;
        profile.FirstPlayedAt = new DateTimeOffset(2026, 2, 3, 18, 4, 0, TimeSpan.Zero);

        notes.ZoneWorlds["WizardCity/WC_Hub"] = "WizardCity";
        notes.Save();
    }

    private static LauncherDatabaseClient Client(RecordingHttpHandler handler)
        => new(Port, http: new HttpClient(handler));

    /// <summary>
    /// A database with no marker in it yet, which answers a write with a created document.
    /// </summary>
    private static RecordingHttpHandler Answer()
        => new((request, _) => request.Method == "GET"
            ? Json(HttpStatusCode.OK, """{"Results":[]}""")
            : Json(HttpStatusCode.Created, "{}"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
