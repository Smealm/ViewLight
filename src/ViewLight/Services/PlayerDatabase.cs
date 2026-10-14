using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// The admin account Imlight created with a password other than the one its config names now.
/// </summary>
/// <param name="DocumentId">The document to write, when the config's password is put on the account.</param>
/// <param name="Username">The account, for a sentence a person has to read.</param>
/// <param name="Document">
/// The account's own document, as it was read, so the one field can be changed and the whole thing written
/// back. Carried rather than fetched again because the database's own single-document read does not answer
/// on the route this build serves: <c>GET /databases/{db}/docs?id=…</c> returns the paged listing.
/// </param>
/// <remarks>
/// <b>Imlight only ever creates this account.</b> Its boot calls
/// <c>DatabaseUtilities.CreateEmbeddedDatabaseAccount</c> with the password from <c>Imlight.ini</c> on every
/// start, but <c>AccountCollection.CreateAccount</c> refuses a username it already holds - so changing that
/// password after the first boot leaves the account on the one it was made with, and the game refuses the
/// sign-in the config (and the launcher's own Settings screen) advertises.
/// <para>
/// That is a fact about the password rather than about the database, so it is reported here and repaired
/// by <see cref="PlayerDatabaseClient.AlignAdminPasswordAsync"/> instead of being treated as a database the
/// launcher does not understand. Measured on a real install: an admin account created in October 2026 held
/// <c>base64(SHA512("compsci"))</c> - the password Imlight ships - minutes after the config's password had
/// been changed by the launcher's own Generate button, with the account otherwise entirely Imlight's own
/// (<c>accounts/1-A</c>, a <c>Raven/Hilo/accounts</c> document beside it, <c>Raven-Clr-Type</c> naming its
/// <c>Account</c> model).
/// </para>
/// </remarks>
public sealed record AdminPasswordDrift(string DocumentId, string Username, JsonObject Document);

/// <summary>What the player database held when it was read.</summary>
/// <param name="AdminPassword">
/// The admin account whose stored password is not the one <c>Imlight.ini</c> names, or <c>null</c> when they
/// agree. Its own fact rather than part of <see cref="Problem"/>: the database is readable either way.
/// </param>
public sealed record PlayerDatabaseContents(
    IReadOnlyList<PlayerAccount> Accounts,
    IReadOnlyList<PlayerSessionKey> SessionKeys,
    string? Problem,
    AdminPasswordDrift? AdminPassword = null) {

    /// <summary>Nothing could be read, and this says why.</summary>
    public static PlayerDatabaseContents Unreadable(string problem) => new([], [], problem);

    /// <summary>
    /// Whether every document read back matched the layout this launcher expects. Nothing is written,
    /// and no automatic sign-in is attempted, unless this is true - see <see cref="PlayerDatabaseLayout"/>.
    /// </summary>
    public bool IsTrustworthy => Problem is null;

    public PlayerAccount? Account(ulong accountId) => Accounts.FirstOrDefault(a => a.AccountId == accountId);

    /// <summary>The key stored for an account. Imlight keeps at most one, replacing it on each sign-in.</summary>
    public PlayerSessionKey? SessionKeyFor(ulong accountId)
        => SessionKeys.FirstOrDefault(k => k.AccountId == accountId);
}

/// <summary>What came of asking the database to make an account.</summary>
public enum AccountCreation {

    /// <summary>The account is in the database.</summary>
    Created,

    /// <summary>An account of that name is already there. Imlight refuses these too.</summary>
    NameTaken,

    /// <summary>The name or password is not something an account can be made with.</summary>
    Rejected,

    /// <summary>The database could not be written to, and it says why.</summary>
    Failed,
}

/// <summary>
/// A new account document, built the way Imlight builds one.
/// </summary>
/// <remarks>
/// <para>
/// Every field Imlight writes is written here, with the same values its own <c>.account create</c> command
/// would leave: <c>AuthLevel.None</c> - the command itself is what needs a Hall Monitor, not the account it
/// makes - open chat, no characters, nothing purchased. A document missing a field would not fail to read:
/// it would read as that field's default and be indistinguishable from one the server made, which is why
/// the whole shape is written rather than the interesting half of it.
/// </para>
/// <para>
/// The document id is a name of the launcher's own rather than one from Imlight's hilo sequence, exactly
/// as <see cref="PlayerDatabaseClient"/> does with session keys. Nothing looks an account up by its id - the
/// server queries by <c>AccountId</c> and <c>Username</c>, both of which are in the document - so the id
/// only has to be unique.
/// </para>
/// </remarks>
public static class PlayerAccountDocument {

    /// <summary>The collection Imlight keeps accounts in, as its metadata spells it.</summary>
    public const string Collection = "Accounts";

    /// <summary>A GID for a new account, in the shape Imlight's own generator produces.</summary>
    public static ulong NewAccountId() {
        var bytes = Guid.NewGuid().ToByteArray();
        var id = BitConverter.ToUInt64(bytes, 0);

        // Zero is the "no id" value everywhere GIDs are used, and Imlight's generator can produce it by
        // chance. It costs one bit to rule out rather than leaving an account nothing can look up.
        return id == 0 ? 1UL : id;
    }

    public static string NewDocumentId() => $"Accounts/viewlight-{Guid.NewGuid():N}";

    public static JsonObject Build(ulong accountId, string username, string passwordHash, DateTimeOffset createdAt) {
        var text = createdAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        return new JsonObject {
            ["AccountId"] = accountId,
            ["Username"] = username,
            ["Email"] = string.Empty,
            ["PasswordHash"] = passwordHash,
            ["AuthLevel"] = 0,
            ["ChatMode"] = 0,
            ["CharacterIds"] = new JsonArray(),
            ["InfractionIds"] = new JsonArray(),
            ["CreationTime"] = text,
            ["LastLoginTime"] = DateTime.UnixEpoch.ToString("o", CultureInfo.InvariantCulture),
            ["LastLoginMachineId"] = 0,
            ["LastLoginIp"] = null,
            ["IsLocked"] = false,
            ["PurchasedCharacterSlots"] = 0,
            ["@metadata"] = new JsonObject { ["@collection"] = Collection },
        };
    }

    /// <summary>
    /// Why a name and password cannot be an account, or null when they can.
    /// </summary>
    /// <remarks>
    /// Deliberately permissive: Imlight accepts anything for its own command, so the launcher only refuses
    /// what is certainly broken - nothing to sign in with, whitespace that cannot be typed at a login
    /// prompt, a slash that means something in a document id, or more characters than the client's own
    /// login screen will take.
    /// </remarks>
    public static string? Validate(string? username, string? password) {
        var name = (username ?? string.Empty).Trim();

        if (name.Length == 0) {
            return "An account needs a username.";
        }

        if (name.Length > 32) {
            return "A username can be at most 32 characters.";
        }

        if (name.Any(char.IsWhiteSpace)) {
            return "A username cannot contain spaces.";
        }

        if (name.Contains('/') || name.Contains('\\')) {
            return "A username cannot contain a slash.";
        }

        return string.IsNullOrEmpty(password) ? "An account needs a password." : null;
    }
}

/// <summary>Hashes account passwords the way Imlight does: <c>base64(SHA512(password))</c>.</summary>
public static class PlayerPassword {

    public static string Hash(string plaintextPassword)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(plaintextPassword)));

    /// <summary>Whether a stored hash is the hash of this password.</summary>
    public static bool Matches(string plaintextPassword, string? storedHash)
        => !string.IsNullOrEmpty(storedHash)
           && string.Equals(Hash(plaintextPassword), storedHash, StringComparison.Ordinal);
}

/// <summary>
/// Reads the player database Imlight hosts while it is running, so the launcher can list accounts and
/// find the session key that signs one in without the client's login screen.
/// </summary>
/// <remarks>
/// <para>
/// Imlight hosts its embedded RavenDB <b>in server mode</b> on <c>Database.EmbeddedDatabasePort</c>, so
/// while the server is up it answers over HTTP on this machine. Reading it that way never touches the
/// database's data directory, which RavenDB holds under an exclusive lock - that lock is exactly why
/// the database is reachable over a port at all.
/// </para>
/// <para>
/// <b>Nothing is trusted about the shape of what comes back.</b> Imlight owns this storage format, so
/// every read is verified against the layout the launcher expects and anchored on the admin account
/// Imlight creates on boot; a mismatch switches the feature off and says why rather than guessing.
/// Writes follow the same rule: the guard has to pass first, and only a document this launcher built
/// in full is ever written.
/// </para>
/// </remarks>
public sealed class PlayerDatabaseClient : IDisposable {

    public const string DefaultDatabaseName = "Playerdata";

    /// <summary>Documents are read a page at a time, this many at a time.</summary>
    private const int PageSize = 512;

    /// <summary>
    /// How long a session key lasts. Imlight's own <c>ClientKeyCollection.KeyExpireTimeInHours</c>, and
    /// the expiry metadata the launcher writes so a key it issues behaves like one the server issued.
    /// </summary>
    public static readonly TimeSpan SessionKeyLifetime = TimeSpan.FromHours(30);

    /// <summary>A key with less than this left is replaced rather than handed over mid-session.</summary>
    private static readonly TimeSpan SessionKeyHeadroom = TimeSpan.FromMinutes(30);

    /// <summary>The collection Imlight stores session keys in, and the name written into their metadata.</summary>
    private const string SessionKeyCollection = "SessionKeys";

    private readonly HttpClient _http;
    private readonly string _databaseName;
    private readonly bool _ownsHttp;

    public PlayerDatabaseClient(HttpClient? http = null, string databaseName = DefaultDatabaseName) {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _databaseName = databaseName;
    }

    /// <summary>Where the embedded database answers, on the port Imlight was configured with.</summary>
    public static string BaseUrl(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads the accounts and session keys. A database that cannot be read is reported as
    /// <see cref="PlayerDatabaseContents.Unreadable"/> rather than thrown: the server is only up while
    /// it is running, and the launcher has to work either way.
    /// </summary>
    /// <summary>
    /// Makes sure the database Imlight keeps its accounts in exists, creating it if it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Imlight's own boot does this: it asks Raven for a document store on this database name, and Raven
    /// makes the database on the way. Doing the same here is what lets accounts be made before the server
    /// has ever been started - and it is safe to do, because Imlight creates its admin account on every
    /// boot it takes, not only on a database it made itself.
    /// </para>
    /// <para>
    /// When Imlight is the one running, this is never needed: its database is already there, and the
    /// database is not the launcher's to make while the server is using it.
    /// </para>
    /// </remarks>
    /// <returns>Whether the database is there now, and why not when it is not.</returns>
    public async Task<(bool Exists, string? Problem)> EnsureDatabaseAsync(
        int port, CancellationToken cancellationToken = default) {

        try {
            if (await DatabaseExistsAsync(port, cancellationToken).ConfigureAwait(false)) {
                return (true, null);
            }

            var record = new JsonObject { ["DatabaseName"] = _databaseName };
            using var content = new StringContent(record.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http
                .PutAsync($"{BaseUrl(port)}/admin/databases?name={Uri.EscapeDataString(_databaseName)}",
                    content, cancellationToken)
                .ConfigureAwait(false);

            // 201 is a database made; a database that appeared between the two calls is the same outcome.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict) {
                return (true, null);
            }

            return (false, $"The database \"{_databaseName}\" could not be created ({response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return (false, $"{BaseUrl(port)} did not answer as a database server ({ex.Message}).");
        }
    }

    private async Task<bool> DatabaseExistsAsync(int port, CancellationToken cancellationToken) {
        try {
            using var response = await _http
                .GetAsync($"{BaseUrl(port)}/databases/{_databaseName}/docs?pageSize=1", cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return false;
        }
    }

    public async Task<PlayerDatabaseContents> ReadAsync(
        int port,
        string adminUsername,
        string adminPassword,
        CancellationToken cancellationToken = default) {

        try {
            var documents = await ReadDocumentsAsync(port, cancellationToken).ConfigureAwait(false);

            return PlayerDatabaseLayout.Inspect(documents, adminUsername, adminPassword);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return PlayerDatabaseContents.Unreadable(
                $"{BaseUrl(port)} did not answer as a player database ({ex.Message}).");
        }
    }

    /// <summary>
    /// Reads what the database says about profiles: the wizards an account has, who is signed in right now,
    /// and every command the server has logged.
    /// </summary>
    /// <remarks>
    /// Guarded separately from the accounts on purpose. An unexpected wizard document turns achievements
    /// off and says why, while automatic sign-in carries on; the other way round, a database whose admin
    /// account does not look right still tells the launcher who is playing. Neither may take the other
    /// down, which is why this is its own read rather than another field on
    /// <see cref="PlayerDatabaseContents"/>.
    /// </remarks>
    public async Task<PlayerProfileData> ReadProfileDataAsync(
        int port, CancellationToken cancellationToken = default) {

        try {
            var documents = await ReadDocumentsAsync(port, cancellationToken).ConfigureAwait(false);

            return PlayerProfileLayout.Inspect(documents);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return PlayerProfileData.Unreadable(
                $"{BaseUrl(port)} did not answer as a player database ({ex.Message}).");
        }
    }

    /// <summary>
    /// The session key to sign an account in with, arranging for one to exist when there is not a
    /// usable one: a key this launcher issues for this machine, spelled the way Imlight spells its own
    /// so the server accepts it.
    /// </summary>
    /// <returns>The key to hand the client, or null when one could not be arranged.</returns>
    /// <remarks>
    /// Imlight keeps one key per account and deletes the others when it issues a new one, so replacing
    /// a key is its own normal lifecycle rather than something made up here. Deleting before writing is
    /// what keeps <c>GetSessionKey</c>, which takes the first match, from finding a stale one.
    /// </remarks>
    public async Task<string?> EnsureSessionKeyAsync(
        int port,
        PlayerDatabaseContents contents,
        ulong accountId,
        ulong machineId,
        CancellationToken cancellationToken = default) {

        // The guard comes first: an unexpected layout means no writes at all.
        if (!contents.IsTrustworthy || contents.Account(accountId) is null) {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var existing = contents.SessionKeyFor(accountId);
        if (existing is not null
            && existing.MachineId == machineId
            && existing.Expires is not null
            && existing.Expires > now + SessionKeyHeadroom) {
            return existing.ClientKey2;
        }

        var key = NewClientKey2();

        try {
            foreach (var stale in contents.SessionKeys.Where(k => k.AccountId == accountId)) {
                await DeleteDocumentAsync(port, stale.DocumentId, cancellationToken).ConfigureAwait(false);
            }

            await WriteSessionKeyAsync(port, accountId, machineId, key, now + SessionKeyLifetime, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return null;
        }

        return key;
    }

    /// <summary>
    /// A session key in the shape Imlight issues: base64 of 32 random bytes, the length of its own
    /// <c>SHA256</c> session keys.
    /// </summary>
    private static string NewClientKey2() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private async Task<List<JsonElement>> ReadDocumentsAsync(int port, CancellationToken cancellationToken) {
        var documents = new List<JsonElement>();

        for (var start = 0; ; start += PageSize) {
            var url = $"{BaseUrl(port)}/databases/{_databaseName}/docs?start={start}&pageSize={PageSize}";

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("Results", out var results)
                || results.ValueKind != JsonValueKind.Array) {
                throw new JsonException("The document listing had no Results array.");
            }

            var read = 0;
            foreach (var document in results.EnumerateArray()) {
                // The document outlives the JsonDocument it was parsed from, so it is cloned.
                documents.Add(document.Clone());
                read++;
            }

            if (read < PageSize) {
                return documents;
            }
        }
    }

    /// <summary>
    /// Makes an account in the player database, in the shape Imlight's own accounts have.
    /// </summary>
    /// <param name="contents">
    /// The read the guard is taken from. The layout has to have been recognised before anything is
    /// written: a database that does not look the way the launcher expects is one whose accounts it must
    /// not add to.
    /// </param>
    /// <returns>What happened, and a sentence explaining it when it was not <see cref="AccountCreation.Created"/>.</returns>
    public async Task<(AccountCreation Result, string? Problem)> CreateAccountAsync(
        int port,
        PlayerDatabaseContents contents,
        string username,
        string password,
        CancellationToken cancellationToken = default) {

        if (!contents.IsTrustworthy) {
            return (AccountCreation.Failed,
                $"The player database was not recognised ({contents.Problem}), so no account was added to it.");
        }

        var rejection = PlayerAccountDocument.Validate(username, password);
        if (rejection is not null) {
            return (AccountCreation.Rejected, rejection);
        }

        var name = username.Trim();

        // Imlight's own create refuses a duplicate, and it compares the stored name exactly. The launcher
        // compares without case as well, because two accounts differing only in case look like one account
        // to whoever has to sign in as them - and the game's login screen is no help there either.
        if (contents.Accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase))) {
            return (AccountCreation.NameTaken, $"There is already an account called \"{name}\".");
        }

        var id = PlayerAccountDocument.NewDocumentId();
        var document = PlayerAccountDocument.Build(
            PlayerAccountDocument.NewAccountId(), name, PlayerPassword.Hash(password), DateTimeOffset.UtcNow);

        try {
            await WriteDocumentAsync(port, id, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return (AccountCreation.Failed, $"The database refused the account ({ex.Message}).");
        }

        return (AccountCreation.Created, null);
    }

    /// <summary>
    /// Puts the password <c>Imlight.ini</c> names on the admin account Imlight created with another one.
    /// </summary>
    /// <returns>A sentence saying what happened when it could not be done, or <c>null</c> when it was.</returns>
    /// <remarks>
    /// Only ever that one field on that one document, and only ever after the layout has been recognised -
    /// the same rule every other write here follows. Imlight will not do it itself: its create refuses a
    /// username it already holds, so without this the password its own config names - and the one the
    /// launcher's Settings screen shows - is not the one the account has, and the game refuses the sign-in
    /// both of them advertise. See <see cref="AdminPasswordDrift"/> for how that happens.
    /// </remarks>
    public async Task<string?> AlignAdminPasswordAsync(
        int port,
        PlayerDatabaseContents contents,
        string password,
        CancellationToken cancellationToken = default) {

        var drift = contents.AdminPassword;
        if (drift is null || string.IsNullOrEmpty(password)) {
            return null;
        }

        if (!contents.IsTrustworthy) {
            return "the database was not recognised, so its admin account was left alone";
        }

        try {
            var document = drift.Document;
            document["PasswordHash"] = PlayerPassword.Hash(password);
            DropServerMetadata(document);

            using var content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http
                .PutAsync($"{BaseUrl(port)}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(drift.DocumentId)}",
                    content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) {
                return null;
            }

            // The database's own reason rather than the status alone: a repair that fails quietly leaves a
            // password nobody can sign in with and nothing to go on.
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return $"{BaseUrl(port)} refused the change ({(int)response.StatusCode}: {Clipped(reason)})";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            return $"{BaseUrl(port)} refused the change ({ex.Message})";
        }
    }

    /// <summary>
    /// The first of an error body, in one line, for a sentence in the Log. Raven answers a refused write
    /// with a paragraph of its own diagnostics, and the first line of it is the part that says what is
    /// wrong.
    /// </summary>
    private static string Clipped(string reason) {
        var oneLine = string.Join(' ', reason.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));

        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    /// <summary>
    /// Drops the fields the database server owns, which come back with a document but are not the
    /// launcher's to send: a change vector written back is a version the launcher is not claiming to have
    /// read, and the server rejects the write when it no longer matches.
    /// </summary>
    private static void DropServerMetadata(JsonObject document) {
        if (document["@metadata"] is not JsonObject metadata) {
            return;
        }

        foreach (var field in new[] { "@id", "@change-vector", "@last-modified", "@etag" }) {
            metadata.Remove(field);
        }
    }

    /// <summary>Writes a document, with the metadata the collection is taken from.</summary>
    private async Task WriteDocumentAsync(
        int port, string id, JsonObject document, CancellationToken cancellationToken) {

        using var content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http
            .PutAsync($"{BaseUrl(port)}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(id)}",
                content, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private async Task WriteSessionKeyAsync(
        int port,
        ulong accountId,
        ulong machineId,
        string clientKey2,
        DateTimeOffset expires,
        CancellationToken cancellationToken) {

        // Imlight's own documents are named ClientKeyPairs/{n}-A, so nothing depends on this shape -
        // the account and machine in the document are what is queried. A distinct prefix keeps the
        // launcher's own documents recognisable.
        var id = $"SessionKeys/viewlight-{Guid.NewGuid():N}";

        var document = new JsonObject {
            ["AccountId"] = accountId,
            ["MachineId"] = machineId,
            ["ClientKey2"] = clientKey2,
            ["@metadata"] = new JsonObject {
                ["@collection"] = SessionKeyCollection,
                ["@expires"] = expires.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            },
        };

        await WriteDocumentAsync(port, id, document, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteDocumentAsync(int port, string documentId, CancellationToken cancellationToken) {
        using var response = await _http
            .DeleteAsync($"{BaseUrl(port)}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(documentId)}",
                cancellationToken)
            .ConfigureAwait(false);

        // Already gone is the outcome asked for, so it is not a failure.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.OK) {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public void Dispose() {
        if (_ownsHttp) {
            _http.Dispose();
        }
    }
}

/// <summary>
/// Reads documents Imlight's models describe, refusing anything that does not look the way it should.
/// </summary>
/// <remarks>
/// This is the guard, and it is deliberately strict: an Imlight update that only changes behaviour keeps
/// working, while one that changes how account data is stored turns automatic sign-in off instead of
/// corrupting it. The anchor is the admin account Imlight creates on boot, which must be there and must
/// hash the password in <c>Config/Imlight.ini</c>.
/// </remarks>
internal static class PlayerDatabaseLayout {

    private const string AccountsCollection = "Accounts";

    internal static PlayerDatabaseContents Inspect(
        IReadOnlyList<JsonElement> documents,
        string adminUsername,
        string adminPassword) {

        var accounts = new List<PlayerAccount>();
        var keys = new List<PlayerSessionKey>();
        string? problem = null;

        foreach (var document in documents) {
            var collection = CollectionOf(document);

            switch (collection) {
                case AccountsCollection:
                    if (TryReadAccount(document, out var account, out var accountProblem)) {
                        accounts.Add(account!);
                    }
                    else {
                        problem ??= accountProblem;
                    }

                    break;

                case "SessionKeys":
                    if (TryReadSessionKey(document, out var key, out var keyProblem)) {
                        keys.Add(key!);
                    }
                    else {
                        problem ??= keyProblem;
                    }

                    break;
            }
        }

        // A database with nothing in it is trusted, and only because there is nothing in it to misread:
        // this is what a server that has never booted looks like, and what the launcher's own database
        // looks like the moment it is created. Everything below still has to pass the anchor, which is
        // what protects a database that does have documents in it.
        if (documents.Count == 0) {
            return new PlayerDatabaseContents(accounts, keys, null);
        }

        var (anchorProblem, drift) = Anchor(accounts, documents, adminUsername, adminPassword);
        problem ??= anchorProblem;

        return new PlayerDatabaseContents(accounts, keys, problem, problem is null ? drift : null);
    }

    /// <summary>
    /// The anchor: an account named the way <c>Config/Imlight.ini</c> says has to exist, holding a password
    /// hash of the shape Imlight writes - and, when that hash is of a different password than the config
    /// names, the fact that it is.
    /// </summary>
    /// <remarks>
    /// The password's *value* is deliberately not part of what makes this database recognisable. Imlight
    /// creates the admin account once and never updates it, so a config whose password has been changed
    /// since the first boot holds an account whose hash is something else - while the account, the hash's
    /// shape, and every other document are Imlight's own. Requiring the two to agree made a legitimate
    /// database unreadable and accounts impossible to add, which is what this reports instead.
    /// </remarks>
    private static (string? Problem, AdminPasswordDrift? Drift) Anchor(
        List<PlayerAccount> accounts,
        IReadOnlyList<JsonElement> documents,
        string adminUsername,
        string adminPassword) {

        if (string.IsNullOrWhiteSpace(adminUsername)) {
            return ("No admin username is set, so the player database cannot be recognised.", null);
        }

        var anchor = accounts.FirstOrDefault(a => string.Equals(a.Username, adminUsername, StringComparison.Ordinal));
        if (anchor is null) {
            return ($"The admin account \"{adminUsername}\" Imlight creates on boot is not in the database.",
                null);
        }

        var document = documents
            .Where(d => CollectionOf(d) == AccountsCollection)
            .FirstOrDefault(d => TryReadUlong(d, "AccountId", out var id) && id == anchor.AccountId);

        var hash = document.ValueKind == JsonValueKind.Object
                   && document.TryGetProperty("PasswordHash", out var property)
                   && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

        // Imlight writes base64(SHA512(password)): 88 characters decoding to 64 bytes.
        var hashIsShapedRight = !string.IsNullOrEmpty(hash)
                                && Convert.TryFromBase64String(hash, new byte[hash.Length + 3], out var written)
                                && written == 64;

        if (!hashIsShapedRight) {
            return ($"The password hash stored for \"{adminUsername}\" is not in the shape Imlight writes.",
                null);
        }

        if (string.IsNullOrEmpty(adminPassword) || PlayerPassword.Matches(adminPassword, hash)) {
            return (null, null);
        }

        // A document with no id is one the launcher could not put right, but it is still an account it can
        // read - so this is not a problem either way, and without an id there is nothing to say.
        var documentId = DocumentIdOf(document);

        return (null, documentId.Length == 0
            ? null
            : new AdminPasswordDrift(documentId, anchor.Username,
                JsonNode.Parse(document.GetRawText())!.AsObject()));
    }

    /// <summary>The id a document's own metadata carries, or empty when it carries none.</summary>
    internal static string DocumentIdOf(JsonElement document)
        => document.TryGetProperty("@metadata", out var metadata)
           && metadata.ValueKind == JsonValueKind.Object
           && metadata.TryGetProperty("@id", out var id)
           && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : string.Empty;

    internal static string? CollectionOf(JsonElement document)
        => document.TryGetProperty("@metadata", out var metadata)
           && metadata.ValueKind == JsonValueKind.Object
           && metadata.TryGetProperty("@collection", out var collection)
           && collection.ValueKind == JsonValueKind.String
            ? collection.GetString()
            : null;

    private static bool TryReadAccount(JsonElement document, out PlayerAccount? account, out string? problem) {
        account = null;
        problem = null;

        if (!TryReadUlong(document, "AccountId", out var accountId)) {
            problem = "An account document carried no usable AccountId.";
            return false;
        }

        if (!document.TryGetProperty("Username", out var username)
            || username.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(username.GetString())) {
            problem = "An account document carried no usable Username.";
            return false;
        }

        account = new PlayerAccount(accountId, username.GetString()!);
        return true;
    }

    private static bool TryReadSessionKey(JsonElement document, out PlayerSessionKey? key, out string? problem) {
        key = null;
        problem = null;

        if (!TryReadUlong(document, "AccountId", out var accountId)
            || !TryReadUlong(document, "MachineId", out var machineId)) {
            problem = "A session key document carried no usable AccountId and MachineId.";
            return false;
        }

        if (!document.TryGetProperty("ClientKey2", out var clientKey)
            || clientKey.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(clientKey.GetString())) {
            problem = "A session key document carried no usable ClientKey2.";
            return false;
        }

        // Without an id the key could not be replaced, which is worth knowing about rather than working
        // around.
        var documentId = DocumentIdOf(document);

        if (documentId.Length == 0) {
            problem = "A session key document carried no document id.";
            return false;
        }

        key = new PlayerSessionKey(accountId, machineId, clientKey.GetString()!, documentId, ExpiryOf(document));
        return true;
    }

    private static DateTimeOffset? ExpiryOf(JsonElement document) {
        if (!document.TryGetProperty("@metadata", out var metadata)
            || !metadata.TryGetProperty("@expires", out var expires)
            || expires.ValueKind != JsonValueKind.String) {
            return null;
        }

        return DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    internal static bool TryReadUlong(JsonElement document, string name, out ulong value) {
        value = 0;

        if (!document.TryGetProperty(name, out var property)) {
            return false;
        }

        // Account and machine ids are 64-bit GIDs, past what a double can hold exactly, so they are read
        // as integers rather than through a number that has been through floating point.
        return property.ValueKind switch {
            JsonValueKind.Number => property.TryGetUInt64(out value),
            JsonValueKind.String => ulong.TryParse(property.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
