using System.Net;
using System.Text;
using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher's notes in its own database: one document per profile rather than one file holding everything,
/// the zone-to-world map beside them, and the pictures as attachments. What is checked here is the reading and
/// writing of those documents, and what a store does when there is nowhere to keep anything yet.
/// </summary>
public class ProfilePersistenceTests {

    private const int Port = 28099;

    [Fact]
    public void TheDatabaseKeepsOneDocumentPerProfileAndOneForTheMap() {
        var handler = Answer(_ => Json(HttpStatusCode.Created, "{}"));

        var saved = Database(handler).Save(new LauncherProfiles {
            Profiles = [Profile(42, "jordan"), Profile(7, "sam")],
            ZoneWorlds = { ["WizardCity/WC_Hub"] = "WizardCity" },
        }, out var problem);

        Assert.True(saved);
        Assert.Null(problem);

        Assert.Equal(["Profiles/42", "Profiles/7", "ZoneWorlds/all"],
            handler.Requests.Select(request => request.Query("id")));

        Assert.Contains("\"AccountId\":42", handler.Requests[0].Body);
        Assert.Contains("\"@collection\":\"Profiles\"", handler.Requests[0].Body);
        Assert.Contains("WizardCity/WC_Hub", handler.Requests[2].Body);
    }

    [Fact]
    public void ARefusedWriteIsReportedRatherThanThrown() {
        var persistence = Database(Answer(_ => Json(HttpStatusCode.InternalServerError, "no")));

        Assert.False(persistence.Save(new LauncherProfiles { Profiles = [Profile(42, "jordan")] }, out var problem));
        Assert.Contains("refused a write", problem);
    }

    [Fact]
    public void TheProfilesAreReadOutOfTheCollectionTheyAreIn() {
        var persistence = Database(Answer(_ => Json(HttpStatusCode.OK, """
            {"Results":[
              {"AccountId":42,"Username":"jordan","@metadata":{"@collection":"Profiles","@id":"Profiles/42"}},
              {"AccountId":9,"Username":"someone else","@metadata":{"@collection":"Accounts"}},
              {"ZoneWorlds":{"WizardCity/WC_Hub":"WizardCity"},"@metadata":{"@collection":"ZoneWorlds"}}]}
            """)));

        var loaded = persistence.Load(out var problem);

        Assert.NotNull(loaded);
        Assert.Null(problem);

        // The accounts collection is the server's, not the launcher's notes: a document there is not a profile.
        var profile = Assert.Single(loaded!.Profiles);
        Assert.Equal(42UL, profile.AccountId);
        Assert.Equal("jordan", profile.Username);
        Assert.Equal("WizardCity", loaded.ZoneWorlds["WizardCity/WC_Hub"]);
    }

    [Fact]
    public void AProfileThatWillNotReadIsSkippedAndSaid() {
        var persistence = Database(Answer(_ => Json(HttpStatusCode.OK, """
            {"Results":[
              {"AccountId":42,"Username":"jordan","@metadata":{"@collection":"Profiles"}},
              {"AccountId":"not a number","@metadata":{"@collection":"Profiles"}}]}
            """)));

        var loaded = persistence.Load(out var problem);

        // What could be read still loads, and what could not is said rather than passed over in silence.
        Assert.Single(loaded!.Profiles);
        Assert.Contains("One profile", problem);
        Assert.Contains("could not be read", problem);
    }

    [Fact]
    public void AReadThatFailsIsNullRatherThanAnEmptySet() {
        var persistence = Database(Answer(_ => Json(HttpStatusCode.InternalServerError, "no")));

        // Null is what stops a launcher writing an empty set back over everything the database holds.
        Assert.Null(persistence.Load(out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void TheProfilesComeBackInTheOrderTheyWereFirstSeen() {
        var persistence = Database(Answer(_ => Json(HttpStatusCode.OK, """
            {"Results":[
              {"AccountId":3,"FirstPlayedAt":"2026-03-03T00:00:00+00:00","@metadata":{"@collection":"Profiles"}},
              {"AccountId":1,"FirstPlayedAt":"2026-01-01T00:00:00+00:00","@metadata":{"@collection":"Profiles"}},
              {"AccountId":2,"FirstPlayedAt":"2026-02-02T00:00:00+00:00","@metadata":{"@collection":"Profiles"}}]}
            """)));

        var loaded = persistence.Load(out _);

        // The document listing comes back keyed by account id, which is an arbitrary order to show a person
        // their accounts in.
        Assert.Equal([1UL, 2UL, 3UL], loaded!.Profiles.Select(profile => profile.AccountId));
    }

    [Fact]
    public async Task APictureIsReadFromTheProfileItBelongsTo() {
        var handler = Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        var picture = await Database(handler).ReadImageAsync(42, "avatar.png");

        Assert.Equal(new byte[] { 1, 2, 3 }, picture);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/databases/{LauncherDatabase.DatabaseName}/attachments", new Uri(request.Url).AbsolutePath);
        Assert.Equal("Profiles/42", request.Query("id"));
        Assert.Equal("avatar.png", request.Query("name"));
    }

    // ------------------------------------------------------------------ the store above it

    [Fact]
    public void AStoreWithNowhereToKeepNotesSaysSoRatherThanWritingElsewhere() {
        using var workspace = new TempWorkspace();

        // The launcher's database has not come up yet, which is not the same as a first run: there is nowhere
        // for a note to go, and writing it to a file instead would be a second answer to every question.
        var store = new ProfileStore();

        Assert.False(store.IsAttached);
        Assert.False(store.Reload());

        store.GetOrCreate(42, "jordan");
        store.Save();

        Assert.Contains("not up yet", store.Problem);
        Assert.False(File.Exists(workspace.Path("profiles.json")));
    }

    [Fact]
    public void AStoreTakesOnWhatIsInTheDatabaseWhenItIsAttached() {
        using var workspace = new TempWorkspace();

        var store = new ProfileStore();

        Assert.Empty(store.Profiles);

        var attached = store.Attach(new JsonProfilePersistence(workspace.Path("profiles.json")));

        // An empty file is a launcher whose database is new: attached, with nothing in it yet.
        Assert.True(attached);
        Assert.True(store.IsAttached);
        Assert.Empty(store.Profiles);

        // And once the notes are there - which is what the second store below sees - they arrive with it.
        var first = new ProfileStore(workspace.Path("profiles.json"));
        first.GetOrCreate(42, "jordan");
        first.ZoneWorlds["WizardCity/WC_Hub"] = "WizardCity";
        first.Save();

        Assert.True(store.Reload());
        Assert.Equal(42UL, Assert.Single(store.Profiles).AccountId);
        Assert.Equal("WizardCity", store.ZoneWorlds["WizardCity/WC_Hub"]);
    }

    [Fact]
    public void AReadThatFailsLeavesTheStoreHoldingWhatItHad() {
        using var workspace = new TempWorkspace();

        var store = new ProfileStore(workspace.Path("profiles.json"));
        store.GetOrCreate(42, "jordan");
        store.Save();

        // The database stops answering: what is already known must not be replaced by an empty set, which is
        // what would then be written back over everything the database still holds.
        Assert.False(store.Attach(new NowhereToKeepNotesIn()));

        Assert.Equal(42UL, Assert.Single(store.Profiles).AccountId);
        Assert.Equal("nowhere to read from", store.Problem);
    }

    [Fact]
    public async Task APictureOfAProfileWithNoPictureIsNothingRatherThanARead() {
        using var workspace = new TempWorkspace();

        var store = new ProfileStore(workspace.Path("profiles.json"));

        Assert.Null(await store.ReadImageAsync(42, null));
        Assert.Null(await store.ReadImageAsync(42, "  "));
    }

    // ------------------------------------------------------------------ helpers

    private static DatabaseProfilePersistence Database(RecordingHttpHandler handler)
        => new(new LauncherDatabaseClient(Port, http: new HttpClient(handler)));

    private static RecordingHttpHandler Answer(Func<RecordedRequest, HttpResponseMessage> responder)
        => new((request, _) => responder(request));

    private static LauncherProfile Profile(ulong accountId, string username)
        => new() { AccountId = accountId, Username = username, FirstPlayedAt = DateTimeOffset.UnixEpoch };

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Somewhere that cannot be read and cannot be written, which is what a database that is down is.</summary>
    private sealed class NowhereToKeepNotesIn : IProfilePersistence {

        public LauncherProfiles? Load(out string? problem) {
            problem = "nowhere to read from";
            return null;
        }

        public bool Save(LauncherProfiles data, out string? problem) {
            problem = "nowhere to write to";
            return false;
        }

        public Task<byte[]?> ReadImageAsync(ulong accountId, string fileName, CancellationToken token = default)
            => Task.FromResult<byte[]?>(null);

        public Task<bool> WriteImageAsync(
            ulong accountId, string fileName, byte[] content, CancellationToken token = default)
            => Task.FromResult(false);

        public Task DeleteImageAsync(ulong accountId, string fileName, CancellationToken token = default)
            => Task.CompletedTask;
    }
}
