using System.Net;
using System.Text;
using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Reading the profile half of the player database, and the guard that goes with it.
/// </summary>
/// <remarks>
/// <b>The documents here are built to imitate Imlight's own models</b> - the field names come from
/// <c>Wizard</c>, <c>ServerMagicSchoolBehavior</c>, <c>ServerWizGameStats</c>, <c>ServerQuestBehavior</c>,
/// <c>OnlinePlayer</c> and <c>ChatLog</c> in its source - because this read cannot be exercised against a
/// live server without one running. What is pinned down is which shapes are accepted and which are refused,
/// which is the part that decides whether the launcher is guessing.
/// </remarks>
public class PlayerProfileDataTests {

    private const int Port = 32592;
    private const ulong AccountId = 5479226295515708948;
    private const ulong CharId = 7715317;

    // ------------------------------------------------------------------ the shapes Imlight writes

    private static string Wizard(
        object? accountId = null, object? charId = null, object? level = null, object? gold = null,
        Dictionary<string, object?>? registry = null)
        => Document("Wizards", new Dictionary<string, object?> {
            ["AccountId"] = accountId ?? AccountId,
            ["CharId"] = charId ?? CharId,
            ["MagicSchoolBehavior"] = new Dictionary<string, object?> { ["Level"] = level ?? 1 },
            ["GameStats"] = new Dictionary<string, object?> { ["m_currentGold"] = gold ?? 0 },
            ["QuestBehavior"] = new Dictionary<string, object?> { ["Registry"] = registry ?? [] },
        });

    private static string OnlinePlayer(object? accountId = null, object? charId = null)
        => Document("OnlinePlayers", new Dictionary<string, object?> {
            ["AccountId"] = accountId ?? AccountId,
            ["CharacterId"] = charId ?? CharId,
            ["CurrentZone"] = "WizardCity/WC_Streets/WC_Unicorn",
            ["CurrentRealm"] = "Centaur",
        });

    private static string Command(object? accountId = null, string? message = "account info jordan",
        string? stamp = "2026-10-13T20:34:25.2700000Z")
        => Document("CommandLog", new Dictionary<string, object?> {
            ["AccountId"] = accountId ?? AccountId,
            ["CharacterId"] = CharId,
            ["Message"] = message,
            ["TimeStamp"] = stamp,
        });

    private static string Document(string collection, Dictionary<string, object?> body) {
        body["@metadata"] = new Dictionary<string, object?> {
            ["@id"] = $"{collection}/1-A",
            ["@collection"] = collection,
        };

        return JsonSerializer.Serialize(body);
    }

    /// <summary>A document whose collection is not one this read has any business in.</summary>
    private static string Account() => Document("Accounts", new Dictionary<string, object?> { ["Username"] = "x" });

    private static string Listing(params string[] documents)
        => $"{{\"Results\":[{string.Join(",", documents)}]}}";

    private static async Task<PlayerProfileData> ReadAsync(string listing) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(listing, Encoding.UTF8, "application/json"),
        });

        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        return await client.ReadProfileDataAsync(Port);
    }

    // ------------------------------------------------------------------ what reads back

    [Fact]
    public async Task ReadsAWizardsLevelGoldAndFinishedQuests() {
        var data = await ReadAsync(Listing(Wizard(level: 23, gold: 45_678, registry: new() {
            ["WC-TUT-C05-001_Complete"] = 1,
            ["WC-UNICORN-MAIN-002_Complete"] = 1,
            // A goal's entry is not a completion, and a quest that was un-completed is not one either.
            ["WC-UNICORN-MAIN-002_Goal1"] = 1,
            ["WC-TUT-C09-014_Complete"] = 0,
        })));

        Assert.True(data.IsUsable, data.Problem);

        var wizard = Assert.Single(data.Wizards);
        Assert.Equal(AccountId, wizard.AccountId);
        Assert.Equal(CharId, wizard.CharId);
        Assert.Equal(23, wizard.Level);
        Assert.Equal(45_678, wizard.Gold);
        Assert.Equal(2, wizard.CompletedQuests.Count);
        Assert.Contains("WC-TUT-C05-001", wizard.CompletedQuests);
    }

    [Fact]
    public async Task ACharacterWithNoQuestsHasFinishedNoQuests() {
        var data = await ReadAsync(Listing(Wizard()));

        Assert.True(data.IsUsable, data.Problem);
        Assert.Empty(Assert.Single(data.Wizards).CompletedQuests);
    }

    [Fact]
    public async Task ReadsWhoIsSignedInRightNow() {
        var data = await ReadAsync(Listing(OnlinePlayer()));

        Assert.True(data.IsUsable, data.Problem);

        var player = Assert.Single(data.Online);
        Assert.Equal(AccountId, player.AccountId);
        Assert.Equal(CharId, player.CharId);
        Assert.Equal("Centaur", player.Realm);
    }

    [Fact]
    public async Task ReadsTheCommandsTheServerHasLogged() {
        var data = await ReadAsync(Listing(Command(message: "account create jordan purple4652786")));

        Assert.True(data.IsUsable, data.Problem);

        var command = Assert.Single(data.Commands);
        Assert.Equal(AccountId, command.AccountId);
        Assert.Equal("account create jordan purple4652786", command.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 20, 34, 25, 270, TimeSpan.Zero), command.At);
    }

    [Fact]
    public async Task DocumentsFromOtherCollectionsAreNotThisReadsBusiness() {
        var data = await ReadAsync(Listing(Account(), Wizard(), OnlinePlayer(), Command()));

        Assert.True(data.IsUsable, data.Problem);
        Assert.Single(data.Wizards);
        Assert.Single(data.Online);
        Assert.Single(data.Commands);
    }

    // ------------------------------------------------------------------ what is refused

    [Fact]
    public async Task AWizardWithNoIdCannotBeAttributedAndIsRefused() {
        var data = await ReadAsync(Listing(Wizard(charId: null, accountId: "not a number")));

        Assert.False(data.IsUsable);
        Assert.Contains("AccountId", data.Problem!);
        Assert.Empty(data.Wizards);
    }

    [Fact]
    public async Task ALevelThatIsNotANumberIsARefusalRatherThanAZero() {
        // Reading this as level 0 would stop every level badge from ever being earned, and nothing on
        // screen would say why.
        var data = await ReadAsync(Listing(Wizard(level: "twenty-three")));

        Assert.False(data.IsUsable);
        Assert.Contains("MagicSchoolBehavior.Level", data.Problem!);
    }

    [Fact]
    public async Task GoldThatIsNotANumberIsRefusedToo() {
        var data = await ReadAsync(Listing(Wizard(gold: new Dictionary<string, object?> { ["a"] = 1 })));

        Assert.False(data.IsUsable);
        Assert.Contains("m_currentGold", data.Problem!);
    }

    [Fact]
    public async Task AQuestRegistryThatIsNotASetOfNamesIsRefused() {
        var data = await ReadAsync(Listing(Document("Wizards", new Dictionary<string, object?> {
            ["AccountId"] = AccountId,
            ["CharId"] = CharId,
            ["MagicSchoolBehavior"] = new Dictionary<string, object?> { ["Level"] = 5 },
            ["GameStats"] = new Dictionary<string, object?> { ["m_currentGold"] = 10 },
            ["QuestBehavior"] = new Dictionary<string, object?> { ["Registry"] = "not a registry" },
        })));

        Assert.False(data.IsUsable);
        Assert.Contains("Registry", data.Problem!);
    }

    [Fact]
    public async Task AnOnlinePlayerWithNoAccountIsRefused() {
        var data = await ReadAsync(Listing(OnlinePlayer(accountId: "not an id")));

        Assert.False(data.IsUsable);
        Assert.Contains("online player document", data.Problem!);
    }

    [Fact]
    public async Task ACommandWithNoAccountIsRefused() {
        var data = await ReadAsync(Listing(Command(accountId: "not an id")));

        Assert.False(data.IsUsable);
        Assert.Contains("command log document", data.Problem!);
    }

    [Fact]
    public async Task ACommandWithNoReadableTimeStillCounts() {
        // It happened, so it belongs in the standing. It just cannot be the "last command", because that
        // would mean saying when it was used.
        var data = await ReadAsync(Listing(Command(stamp: null)));

        Assert.True(data.IsUsable, data.Problem);

        var command = Assert.Single(data.Commands);
        Assert.Equal(default, command.At);
        Assert.Equal("account info jordan", command.Text);
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswerIsReportedRatherThanThrown() {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent(string.Empty),
        });

        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        var data = await client.ReadProfileDataAsync(Port);

        Assert.False(data.IsUsable);
        Assert.NotNull(data.Problem);
        Assert.Empty(data.Wizards);
    }

    [Fact]
    public async Task TheProfileReadDoesNotTouchTheAccountsGuard() {
        // Two reads of the same database, deliberately separate: a wizard document that does not look right
        // must turn badges off without touching automatic sign-in, and the admin anchor must not be able to
        // turn badges off either.
        var reads = 0;
        var handler = new FakeHttpHandler(_ => {
            reads++;
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Listing(Wizard(level: "nope")), Encoding.UTF8, "application/json"),
            };
        });

        using var client = new PlayerDatabaseClient(new HttpClient(handler));

        var accounts = await client.ReadAsync(Port, "exampleadmin", "example-password");
        var profiles = await client.ReadProfileDataAsync(Port);

        // Both reads happened, and each came to its own conclusion about the same documents.
        Assert.Equal(2, reads);
        Assert.False(accounts.IsTrustworthy);
        Assert.False(profiles.IsUsable);
    }
}
