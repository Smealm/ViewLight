using System.Net;
using System.Text;
using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Reading the player database, and the guard that decides whether what came back may be trusted.
/// </summary>
/// <remarks>
/// Imlight owns this storage format and the launcher does not, so every one of these is about refusing
/// to act on something that does not look exactly the way it should - including when it cannot tell.
/// </remarks>
public class PlayerDatabaseTests {

    // Invented values: what the launcher reads is shaped like this, and nothing here is anybody's.
    private const string AdminUsername = "exampleadmin";
    private const string AdminPassword = "example-password";
    private const ulong AdminAccountId = 1234567890123456789;
    private const ulong MachineId = 123456789012;
    private const string StoredKey = "cGxhY2Vob2xkZXItc2Vzc2lvbi1rZXktbm90LXJlYWw=";
    private const int Port = 32592;

    // ------------------------------------------------------------------ the shapes Imlight writes

    private static string Account(
        object? accountId, string? username, string? passwordHash, string documentId = "accounts/1-A")
        => JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["AccountId"] = accountId,
            ["Username"] = username,
            ["PasswordHash"] = passwordHash,
            ["@metadata"] = new Dictionary<string, object?> {
                ["@id"] = documentId,
                ["@collection"] = "Accounts",
            },
        });

    private static string SessionKey(
        object? accountId, object? machineId, string? clientKey2,
        string? expires = "2026-10-15T02:16:08.0000000Z", string documentId = "ClientKeyPairs/1-A")
        => JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["AccountId"] = accountId,
            ["MachineId"] = machineId,
            ["ClientKey2"] = clientKey2,
            ["@metadata"] = new Dictionary<string, object?> {
                ["@id"] = documentId,
                ["@collection"] = "SessionKeys",
                ["@expires"] = expires,
            },
        });

    /// <summary>The admin account Imlight creates on boot, hashing the password from Imlight.ini.</summary>
    private static string AdminAccount() => Account(AdminAccountId, AdminUsername, PlayerPassword.Hash(AdminPassword));

    private static string Listing(params string[] documents)
        => $"{{\"Results\":[{string.Join(",", documents)}]}}";

    private static FakeHttpHandler HandlerFor(string listing) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(listing, Encoding.UTF8, "application/json"),
        });

        return handler;
    }

    private static async Task<PlayerDatabaseContents> ReadAsync(FakeHttpHandler handler) {
        using var client = new PlayerDatabaseClient(new HttpClient(handler));

        return await client.ReadAsync(Port, AdminUsername, AdminPassword);
    }

    // ------------------------------------------------------------------ reading it

    [Fact]
    public async Task ReadsTheAccountsAndTheSessionKeys() {
        var handler = HandlerFor(Listing(
            AdminAccount(),
            SessionKey(AdminAccountId, MachineId, StoredKey),
            Account(2222222222222222222, "exampleuser", PlayerPassword.Hash("hunter2"))));

        var contents = await ReadAsync(handler);

        Assert.True(contents.IsTrustworthy, contents.Problem);
        Assert.Equal(2, contents.Accounts.Count);
        Assert.Contains(contents.Accounts, a => a is { AccountId: 2222222222222222222, Username: "exampleuser" });

        // A 64-bit GID is past what a double holds exactly, so it has to survive as an integer.
        Assert.Equal(AdminAccountId, contents.Account(AdminAccountId)!.AccountId);

        var key = contents.SessionKeyFor(AdminAccountId);
        Assert.NotNull(key);
        Assert.Equal(MachineId, key!.MachineId);
        Assert.Equal(StoredKey, key.ClientKey2);
        Assert.Equal("ClientKeyPairs/1-A", key.DocumentId);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 2, 16, 8, TimeSpan.Zero), key.Expires!.Value);
    }

    [Fact]
    public async Task ReadsEveryPageOfTheListing() {
        // A full page is a request for the next one, or the launcher would quietly see part of the
        // database - which is the kind of half-truth that makes an account look missing.
        var handler = new FakeHttpHandler(request => {
            var start = request.RequestUri!.Query.Contains("start=0");

            var documents = start
                ? Enumerable.Range(0, 512).Select(_ => Account(1, "filler", "x", "accounts/9-A"))
                    .Prepend(AdminAccount())
                : [];

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Listing(documents.ToArray()), Encoding.UTF8, "application/json"),
            };
        });

        var contents = await ReadAsync(handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(contents.IsTrustworthy, contents.Problem);
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswerIsReportedRatherThanThrown() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent(string.Empty),
        });

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.NotNull(contents.Problem);
        Assert.Empty(contents.Accounts);
    }

    // ------------------------------------------------------------------ the guard

    [Fact]
    public async Task TheAdminAccountImlightCreatesIsTheAnchor() {
        var handler = HandlerFor(Listing(
            Account(2222222222222222222, "exampleuser", PlayerPassword.Hash("hunter2")),
            SessionKey(AdminAccountId, MachineId, StoredKey)));

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.Contains(AdminUsername, contents.Problem!);
    }

    [Fact]
    public async Task TheAdminHashHasToBeThePasswordInImlightIni() {
        // The anchor is only an anchor because the password is known: an admin account whose hash is
        // some other password is a database the launcher does not understand.
        var handler = HandlerFor(Listing(
            Account(AdminAccountId, AdminUsername, PlayerPassword.Hash("a different password"))));

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.Contains("hash", contents.Problem!);
    }

    [Fact]
    public async Task AnAccountWithoutAnIdIsRefused() {
        var handler = HandlerFor(Listing(AdminAccount(), Account(null, "exampleuser", "hash", "accounts/2-A")));

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.Contains("AccountId", contents.Problem!);
    }

    [Fact]
    public async Task ASessionKeyWithoutAKeyIsRefused() {
        var handler = HandlerFor(Listing(AdminAccount(), SessionKey(AdminAccountId, MachineId, clientKey2: null)));

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.Contains("ClientKey2", contents.Problem!);
    }

    [Fact]
    public async Task ASessionKeyWithoutADocumentIdIsRefused() {
        // Without an id the key could not be replaced, so it is not one the launcher may act on.
        var handler = HandlerFor(Listing(
            AdminAccount(),
            SessionKey(AdminAccountId, MachineId, StoredKey, documentId: string.Empty)));

        var contents = await ReadAsync(handler);

        Assert.False(contents.IsTrustworthy);
        Assert.Contains("document id", contents.Problem!);
    }

    [Fact]
    public void PasswordsAreHashedTheWayImlightHashesThem() {
        Assert.Equal("9gPj3Ct3vP4RFbJYQX6tIKaI1zt5DIMHHgk/BgHWZOFWyzlTkHVtnE0SdlBZi2bfnOtIKBs6jdPAIfVeEFjHCQ==",
            PlayerPassword.Hash(AdminPassword));
        Assert.True(PlayerPassword.Matches(AdminPassword, PlayerPassword.Hash(AdminPassword)));
        Assert.False(PlayerPassword.Matches("wrong", PlayerPassword.Hash(AdminPassword)));
    }

    // ------------------------------------------------------------------ writing a session key

    /// <summary>
    /// A handler that answers the listing and records what is written, so a test can say what was sent
    /// as well as what came back.
    /// </summary>
    private static FakeHttpHandler RecordingHandler(string listing, List<string> methods, List<string?> bodies)
        => new(request => {
            if (request.Method == HttpMethod.Get) {
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(listing, Encoding.UTF8, "application/json"),
                };
            }

            methods.Add(request.Method.Method);
            bodies.Add(request.Content?.ReadAsStringAsync().Result);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

    [Fact]
    public async Task IssuesASessionKeyWhenThereIsNotOne() {
        var methods = new List<string>();
        var bodies = new List<string?>();
        var handler = RecordingHandler(Listing(AdminAccount()), methods, bodies);

        var contents = await ReadAsync(handler);
        using var client = new PlayerDatabaseClient(new HttpClient(handler));

        var key = await client.EnsureSessionKeyAsync(Port, contents, AdminAccountId, MachineId);

        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(["PUT"], methods);

        using var document = JsonDocument.Parse(bodies[0]!);
        Assert.Equal(AdminAccountId, document.RootElement.GetProperty("AccountId").GetUInt64());
        Assert.Equal(MachineId, document.RootElement.GetProperty("MachineId").GetUInt64());
        Assert.Equal(key, document.RootElement.GetProperty("ClientKey2").GetString());

        // Spelled the way Imlight spells its own, so the server takes it and expires it as usual.
        var metadata = document.RootElement.GetProperty("@metadata");
        Assert.Equal("SessionKeys", metadata.GetProperty("@collection").GetString());
        Assert.True(metadata.TryGetProperty("@expires", out _));
    }

    [Fact]
    public async Task ReusesAKeyWithPlentyOfTimeLeft() {
        var expires = DateTimeOffset.UtcNow.AddHours(20).UtcDateTime.ToString("o");
        var methods = new List<string>();
        var handler = RecordingHandler(
            Listing(AdminAccount(), SessionKey(AdminAccountId, MachineId, StoredKey, expires)), methods, []);

        var contents = await ReadAsync(handler);
        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        var key = await client.EnsureSessionKeyAsync(Port, contents, AdminAccountId, MachineId);

        Assert.Equal(StoredKey, key);
        Assert.Empty(methods);
    }

    [Fact]
    public async Task ReplacesAKeyThatIsAnotherMachines() {
        var methods = new List<string>();
        var handler = RecordingHandler(
            Listing(AdminAccount(), SessionKey(AdminAccountId, machineId: 999999999999, StoredKey)), methods, []);

        var contents = await ReadAsync(handler);
        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        var key = await client.EnsureSessionKeyAsync(Port, contents, AdminAccountId, MachineId);

        // The stale document is deleted first: GetSessionKey takes the first match, so leaving it behind
        // is how the server ends up checking a key nobody handed over.
        Assert.Equal(["DELETE", "PUT"], methods);
        Assert.NotEqual(StoredKey, key);
    }

    [Fact]
    public async Task WritesNothingWhenTheDatabaseWasNotReadAsExpected() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{}"),
        });

        var contents = new PlayerDatabaseContents(
            [new PlayerAccount(AdminAccountId, AdminUsername)], [], "An account document carried no usable AccountId.");

        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        var key = await client.EnsureSessionKeyAsync(Port, contents, AdminAccountId, MachineId);

        Assert.Null(key);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WritesNothingForAnAccountThatIsNotThere() {
        var methods = new List<string>();
        var handler = RecordingHandler(Listing(AdminAccount()), methods, []);

        var contents = await ReadAsync(handler);
        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        var key = await client.EnsureSessionKeyAsync(Port, contents, 2222222222222222222, MachineId);

        Assert.Null(key);
        Assert.Empty(methods);
    }
}
