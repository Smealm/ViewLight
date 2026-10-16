using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// What a cloud backup remembers between runs: the signed-in account's refresh token, and the manifest both
/// sides were last equal at. The two are kept in different places on purpose, and both are checked here.
/// </summary>
public class CloudBackupStateTests {

    [Fact]
    public void TheLastSyncedManifestSurvivesOnDisk() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        // Nothing has synced yet: no ancestor, which is what makes a first sync careful rather than confident.
        Assert.Null(CloudBackupState.ReadSyncState(root).Ancestor);

        var manifest = new BackupManifest {
            Databases = { [BackupDatabases.Launcher] = new BackupDatabaseState("h1", 10, 1) },
        };

        Assert.True(CloudBackupState.WriteSyncState(root, manifest));

        var state = CloudBackupState.ReadSyncState(root);

        Assert.NotNull(state.Ancestor);
        Assert.True(state.Ancestor!.HasSameContent(manifest));
        Assert.NotNull(state.LastSyncedAt);
    }

    [Fact]
    public void AFolderWithNoStateHasNoAncestor() {
        using var workspace = new TempWorkspace();

        // A fresh machine, or a portable launcher that never synced: no ancestor, no completed sync.
        var state = CloudBackupState.ReadSyncState(workspace.Path("never-synced"));

        Assert.Null(state.Ancestor);
        Assert.Null(state.LastSyncedAt);
    }

    [Fact]
    public async Task TheRefreshTokenIsKeptInTheLauncherDatabase() {
        var handler = new RecordingHttpHandler((request, _) => request.Method == "PUT"
            ? Json(HttpStatusCode.Created, "{}")
            : Json(HttpStatusCode.OK, "{}"));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        Assert.True(await CloudBackupState.WriteRefreshTokenAsync(client, "refresh-token-1", "someone@example.com"));

        var write = handler.Requests[0];

        Assert.Equal("PUT", write.Method);
        Assert.Equal(CloudBackupState.TokenDocumentId, write.Query("id"));

        // It is a document in the launcher's own database - where its long-lived state goes - rather than beside
        // the settings, and the token is in it.
        Assert.Contains("refresh-token-1", write.Body);
        Assert.Contains(CloudBackupState.Collection, write.Body);
    }

    [Fact]
    public async Task TheStoredTokenIsReadBack() {
        var handler = new RecordingHttpHandler((_, _) => Json(HttpStatusCode.OK,
            new JsonObject {
                ["Results"] = new JsonArray(new JsonObject { ["RefreshToken"] = "stored-token" }),
            }.ToJsonString()));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        Assert.Equal("stored-token", await CloudBackupState.ReadRefreshTokenAsync(client));
    }

    [Fact]
    public async Task WithNoStoredTokenTheLauncherIsSignedOut() {
        var handler = new RecordingHttpHandler((_, _) => Json(HttpStatusCode.NotFound, "{}"));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        Assert.Null(await CloudBackupState.ReadRefreshTokenAsync(client));
    }

    [Fact]
    public async Task ASignInThisRunStillHoldsIsPutBackIntoADatabaseThatLostIt() {
        // What a restore does to the sign-in: the whole database directory is replaced by the copy in the cloud,
        // and that copy can be one taken before this machine ever signed in. Restoring data is not a sign-out,
        // so the token this run already has is written back and the launcher stays signed in.
        var handler = new RecordingHttpHandler((request, _) => request.Method == "PUT"
            ? Json(HttpStatusCode.Created, "{}")
            : Json(HttpStatusCode.NotFound, "{}"));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        var refresh = await CloudBackupState.ReadRefreshTokenOrRestoreAsync(client, "token-this-run-has");

        Assert.Equal("token-this-run-has", refresh);

        // The read came first and the write second: nothing is written over a sign-in the database already has.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("PUT", handler.Requests[1].Method);
        Assert.Equal(CloudBackupState.TokenDocumentId, handler.Requests[1].Query("id"));
        Assert.Contains("token-this-run-has", handler.Requests[1].Body);
    }

    [Fact]
    public async Task AStoredSignInIsNotOverwrittenByThisRunsCopy() {
        // The ordinary start: the database has the sign-in, so it is read and nothing is written.
        var handler = new RecordingHttpHandler((_, _) => Json(HttpStatusCode.OK,
            new JsonObject {
                ["Results"] = new JsonArray(new JsonObject { ["RefreshToken"] = "stored-token" }),
            }.ToJsonString()));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        Assert.Equal("stored-token",
            await CloudBackupState.ReadRefreshTokenOrRestoreAsync(client, "an-older-copy"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ASignOutIsNotUndoneWithNothingToUndoItFrom() {
        // Nothing stored and nothing held: a launcher that starts signed out stays signed out, and no token is
        // conjured out of a run that never had one.
        var handler = new RecordingHttpHandler((_, _) => Json(HttpStatusCode.NotFound, "{}"));

        using var client = new LauncherDatabaseClient(28099, http: new HttpClient(handler));

        Assert.Null(await CloudBackupState.ReadRefreshTokenOrRestoreAsync(client, null));

        // Read only: a PUT here would be a sign-in written by a launcher that has none.
        Assert.Single(handler.Requests);
        Assert.Equal("GET", handler.Requests[0].Method);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
