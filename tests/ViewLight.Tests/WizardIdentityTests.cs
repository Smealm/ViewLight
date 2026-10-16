using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Who a wizard is on the profile screen: its name, its school and its level.
/// </summary>
/// <remarks>
/// <para>
/// Imlight stores a wizard's name as three table indices rather than as text, and its school as a template
/// id rather than as a running number - so both have to be turned into something readable, and neither may
/// be guessed at. A character whose name cannot be honestly resolved is shown by its id, which is ugly and
/// true, rather than under a name that may be somebody else's.
/// </para>
/// <para>
/// The documents below are built to imitate Imlight's own models - <c>Wizard</c>,
/// <c>ServerMagicSchoolBehavior</c>, <c>ServerWizPlayerNameBehavior</c> - because the read cannot be
/// exercised against a live server without one running.
/// </para>
/// </remarks>
public class WizardIdentityTests {

    private const int Port = 32592;
    private const ulong AccountId = 5479226295515708948;
    private const ulong CharId = 7715317;

    private static readonly DateTimeOffset At = new(2026, 10, 14, 20, 0, 0, 0, TimeSpan.FromHours(-4));

    private sealed class Bench : IDisposable {

        private readonly TempWorkspace _workspace = new();

        public Bench() {
            Store = new ProfileStore(_workspace.Path("profiles.json"));
            Ledger = new ProfileLedger(Store);
            Ledger.NoteAccounts([new PlayerAccount(AccountId, "jordan")]);
        }

        public ProfileStore Store { get; }

        public ProfileLedger Ledger { get; }

        public LauncherProfile Profile => Store.GetOrCreate(AccountId, "jordan");

        public void Dispose() => _workspace.Dispose();
    }

    // ------------------------------------------------------------------ the shapes Imlight writes

    private static string Wizard(
        object? school = null, object? level = null, string? nameOverride = null,
        object? nameIndices = null, object? gender = null)
        => Document("Wizards", new Dictionary<string, object?> {
            ["AccountId"] = AccountId,
            ["CharId"] = CharId,
            ["MagicSchoolBehavior"] = new Dictionary<string, object?> {
                ["Level"] = level ?? 1,
                ["MagicSchool"] = school ?? 0,
            },
            ["GameStats"] = new Dictionary<string, object?> { ["m_currentGold"] = 0 },
            ["QuestBehavior"] = new Dictionary<string, object?> { ["Registry"] = new Dictionary<string, object?>() },
            ["PlayerNameBehavior"] = new Dictionary<string, object?> {
                ["NameOverride"] = nameOverride ?? string.Empty,
                ["NameIndices"] = nameIndices,
                ["Gender"] = gender,
            },
        });

    private static string Document(string collection, Dictionary<string, object?> body) {
        body["@metadata"] = new Dictionary<string, object?> {
            ["@id"] = $"{collection}/1-A",
            ["@collection"] = collection,
        };

        return JsonSerializer.Serialize(body);
    }

    private static string Listing(params string[] documents)
        => $"{{\"Results\":[{string.Join(",", documents)}]}}";

    private static async Task<PlayerProfileData> ReadAsync(params string[] documents) {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(Listing(documents), Encoding.UTF8, "application/json"),
        });

        using var client = new PlayerDatabaseClient(new HttpClient(handler));
        return await client.ReadProfileDataAsync(Port);
    }

    private static string OnlinePlayer()
        => Document("OnlinePlayers", new Dictionary<string, object?> {
            ["AccountId"] = AccountId,
            ["CharacterId"] = CharId,
            ["CurrentZone"] = "WizardZone_TheCommons",
            ["CurrentRealm"] = "Centaur",
        });

    private static async Task<ProfileWizard> WizardsAsync(Bench bench, params string[] documents) {
        var data = await ReadAsync(documents);

        Assert.True(data.IsUsable, data.Problem);
        bench.Ledger.ApplyDatabase(data, At);

        return Assert.Single(bench.Profile.Wizards);
    }

    // ------------------------------------------------------------------ the school

    [Fact]
    public void TheSevenSchoolsAreNamedAndNothingElseIs() {
        // Template ids, not ordinals: these are the numbers Imlight persists, so they are pinned here.
        Assert.Equal("Fire", WizardSchool.NameOf(2343174u));
        Assert.Equal("Ice", WizardSchool.NameOf(72777u));
        Assert.Equal("Storm", WizardSchool.NameOf(83375795u));
        Assert.Equal("Myth", WizardSchool.NameOf(2448141u));
        Assert.Equal("Life", WizardSchool.NameOf(2330892u));
        Assert.Equal("Death", WizardSchool.NameOf(78318724u));
        Assert.Equal("Balance", WizardSchool.NameOf(1027491821u));

        // A number that is not one of the seven is no school at all - the four schools that belong to
        // creatures and cards are deliberately not among them, so nothing here invents a name for one.
        Assert.Equal(string.Empty, WizardSchool.NameOf(0u));
        Assert.Equal(string.Empty, WizardSchool.NameOf(123456789u));
        Assert.Equal(string.Empty, WizardSchool.NameOf(uint.MaxValue));

        Assert.Equal(7, WizardSchool.Names.Count);
        Assert.Equal(WizardSchool.Names.Count, WizardSchool.Names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ASchoolStoredAsTextStillReadsAsASchool() {
        // Both spellings appear in the same family of documents, and refusing the readable one would turn
        // a school badge off over punctuation.
        Assert.Equal("Storm", WizardSchool.NameOf("83375795"));
        Assert.Equal("Storm", WizardSchool.NameOf(" 83375795 "));
        Assert.Equal(string.Empty, WizardSchool.NameOf("not a school"));
        Assert.Equal(string.Empty, WizardSchool.NameOf(""));
        Assert.Equal(string.Empty, WizardSchool.NameOf((string?) null));
    }

    [Fact]
    public void ASchoolStoredUnderItsOwnNameReadsAsASchool() {
        // What a real database actually holds: Imlight's school is an enum and a stored enum arrives as its
        // name - the same document that holds an account says "AuthLevel": "Administrator". Reading only the
        // template id is what left every wizard on a real server shown as "School not read".
        Assert.Equal("Storm", WizardSchool.NameOf("Storm"));
        Assert.Equal("Balance", WizardSchool.NameOf(" Balance "));
        Assert.Equal("Death", WizardSchool.NameOf("death"));

        // And a school only a creature can be is still not a wizard's school, however it is spelled.
        Assert.Equal(string.Empty, WizardSchool.NameOf("Shadow"));
        Assert.Equal(string.Empty, WizardSchool.NameOf("None"));
        Assert.Equal(string.Empty, WizardSchool.NameOf("Firecat"));
    }

    [Fact]
    public async Task TheSchoolAndLevelComeOffTheDocument() {
        using var bench = new Bench();

        var wizard = await WizardsAsync(bench, Wizard(school: 2448141u, level: 12));

        Assert.Equal("Myth", wizard.School);
        Assert.Equal("Myth", wizard.SchoolText);
        Assert.Equal(12, wizard.Level);
        Assert.Equal("Level 12", wizard.LevelText);
    }

    [Fact]
    public async Task ASchoolTheLauncherDoesNotKnowIsNotNamed() {
        using var bench = new Bench();

        var wizard = await WizardsAsync(bench, Wizard(school: 999, level: 12));

        Assert.Equal(string.Empty, wizard.School);
        Assert.Equal("School not read", wizard.SchoolText);
    }

    [Fact]
    public void ALevelThatHasNotBeenReadSaysSoRatherThanShowingZero()
        => Assert.Equal("Level not read yet", new ProfileWizard { CharId = CharId }.LevelText);

    // ------------------------------------------------------------------ the name

    [Fact]
    public async Task ANameTheDocumentSpellsOutIsTakenAsItStands() {
        // NameOverride is a name already spelled out - a renamed character - so there is nothing to resolve.
        using var bench = new Bench();

        var wizard = await WizardsAsync(bench, Wizard(nameOverride: "Jordan DeathBane"));

        Assert.Equal("Jordan DeathBane", wizard.Name);
        Assert.Equal("Jordan DeathBane", wizard.DisplayName);
    }

    [Fact]
    public async Task ACharacterWithNoNameToReadIsShownByItsId() {
        // The usual case for a launcher with no game folder: the name is three table indices, the tables
        // are in the client's archive, and there is nothing here to resolve them with.
        using var bench = new Bench();

        var wizard = await WizardsAsync(bench, Wizard(nameIndices: 0x4A2C0107u, gender: 2));

        Assert.Equal(string.Empty, wizard.Name);
        Assert.Equal($"Character {CharId}", wizard.DisplayName);
    }

    [Fact]
    public async Task ANameKeyWithNoGenderIsNotGuessedAt() {
        // The two first-name tables are different lists, so without a gender the key could name a boy or a
        // girl and there is no honest answer - the character keeps its id.
        using var bench = new Bench();

        var wizard = await WizardsAsync(bench, Wizard(nameIndices: 0x4A2C0107u, gender: null));

        Assert.Equal(string.Empty, wizard.Name);
        Assert.StartsWith("Character ", wizard.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void APackedNameKeyIsOneBytePerNamePart() {
        // First, middle and last, most significant first - the top byte is not a name part.
        Assert.Equal((7, 3, 9), WizardNameTable.NameParts(0x004A070309u));

        // A middle or last name of zero is one the player did not choose.
        Assert.Equal((7, 0, 0), WizardNameTable.NameParts(0x008F070000u));

        // The whole of the low three bytes is read, including a value at each end of the range.
        Assert.Equal((255, 255, 255), WizardNameTable.NameParts(0xFF000000u | (255u << 16) | (255u << 8) | 255u));
    }

    [Fact]
    public void GenderIsReadOnlyWhenItIsOneTheLauncherKnows() {
        // The client spells the flag on the name key 0x82 for a boy and 0x80 for a girl, and the ordinals
        // it stores are taken to be one and two. Anything else is refused rather than assumed, because
        // assuming wrong picks the other table and hands the character somebody else's first name.
        Assert.False(WizardNameTable.IsFemale(1));
        Assert.False(WizardNameTable.IsFemale(0x82));
        Assert.True(WizardNameTable.IsFemale(2));
        Assert.True(WizardNameTable.IsFemale(0x80));

        Assert.Null(WizardNameTable.IsFemale(0));
        Assert.Null(WizardNameTable.IsFemale(7));
        Assert.Null(WizardNameTable.IsFemale(null));
    }

    [Fact]
    public void ANameTableWithNoGameFolderResolvesNothing() {
        // Not an error and not a guess: nothing to read means nothing to say.
        Assert.Null(WizardNameTable.Unavailable.Resolve(0x4A2C0107u, isFemale: false));
        Assert.Null(WizardNameTable.Unavailable.Resolve(0x4A2C0107u, isFemale: true));
        Assert.Same(WizardNameTable.Unavailable, WizardNameTable.For(null));
        Assert.Same(WizardNameTable.Unavailable, WizardNameTable.For("  "));
    }

    [Fact]
    public async Task TheLogsNameForASigningInWizardIsMatchedToTheCharacter() {
        // The log names a wizard and the database knows which character the account is on, and neither
        // side has the other's half - so signing in is what matches them, and that is how a profile ends
        // up showing a name rather than an id even with no game folder to read the name tables from.
        using var bench = new Bench();

        bench.Ledger.ApplyLogLines([SignIn()]);

        var wizard = await WizardsAsync(bench, Wizard(nameIndices: 0x4A2C0107u, gender: 2), OnlinePlayer());

        Assert.Equal("Jordan DeathBane", wizard.Name);
        Assert.Equal("Jordan DeathBane", wizard.DisplayName);
    }

    [Fact]
    public async Task ANameSpelledOutOnTheDocumentIsKeptOverTheLogs() {
        // A stored override is the character's own name as the server holds it, so a name is only filled
        // in from the log when there is none at all - the two agreeing is the usual case, and where they
        // do not, the one written down against the character is the one that is about it.
        using var bench = new Bench();

        bench.Ledger.ApplyLogLines([SignIn()]);

        var wizard = await WizardsAsync(bench, Wizard(nameOverride: "Something Else"), OnlinePlayer());

        Assert.Equal("Something Else", wizard.Name);
    }

    private static string SignIn()
        => $"{At.AddSeconds(-30).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} "
           + "[INF] Sending wizard Jordan DeathBane to game server 127.0.0.1:12333.";

    // ------------------------------------------------------------------ what the screen draws

    private static PlayerProfileData Database(params PlayerWizardRecord[] wizards)
        => new(wizards, [new PlayerOnlineRecord(AccountId, CharId, "WizardZone_TheCommons", "Centaur")], [], null);

    private static IReadOnlyList<ProfileWizardRow> Rows(Bench bench) => ProfileWizardRow.For(bench.Profile);

    [Fact]
    public void ARowThatMovedIsADifferentRow() {
        // The whole of the live-update fix. The screen is bound to rows rather than to the wizard objects, so
        // a level that moved is a row that is not equal to the one on screen. A field moving inside an object
        // nobody is watching is invisible: the binding is handed the same list instance either way, which is
        // why the wizards section used to be right only after a restart.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(AccountId, CharId, 12, 500, [], "Storm")), At);
        var before = Rows(bench);

        // The same account read again with nothing about the wizard changed: nothing for the screen to do.
        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(AccountId, CharId, 12, 500, [], "Storm")), At);
        Assert.Equal(before, Rows(bench));

        // A level reached and a purse that got heavier.
        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(AccountId, CharId, 13, 900, [], "Storm")), At);
        var after = Rows(bench);

        Assert.NotEqual(before, after);
        Assert.Equal("Level 13", after[0].LevelText);
        Assert.Equal("900 gold", after[0].GoldText);
    }

    [Fact]
    public void AWizardMadeWhileTheServerRunsIsAnotherRow() {
        // A wizard arriving is an addition to a list rather than a field moving, and both have to reach the
        // screen: it is the same complaint, and the comparison over the rows catches both.
        using var bench = new Bench();

        bench.Ledger.ApplyDatabase(Database(new PlayerWizardRecord(AccountId, CharId, 12, 0, [], "Storm")), At);
        Assert.Single(Rows(bench));

        bench.Ledger.ApplyDatabase(
            Database(
                new PlayerWizardRecord(AccountId, CharId, 12, 0, [], "Storm"),
                new PlayerWizardRecord(AccountId, 4242, 5, 120, [], "Fire")),
            At);

        var rows = Rows(bench);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Fire", rows[1].SchoolText);
        Assert.Equal("Level 5", rows[1].LevelText);
    }

    [Fact]
    public void ARowSaysWhatCouldNotBeReadRatherThanAZero() {
        // A wizard the database has not described yet - a launcher with no server running, or a character
        // the server has not written a level for - is said so rather than drawn as a level 0 wizard.
        using var bench = new Bench();

        Assert.Empty(Rows(bench));

        var unwritten = ProfileWizardRow.For(new LauncherProfile {
            AccountId = AccountId,
            Username = "jordan",
            Wizards = [new ProfileWizard { CharId = CharId }],
        });

        Assert.Equal("Level not read yet", Assert.Single(unwritten).LevelText);
        Assert.Equal("School not read", unwritten[0].SchoolText);
        Assert.Equal(string.Empty, unwritten[0].LastSeenText);
    }
}
