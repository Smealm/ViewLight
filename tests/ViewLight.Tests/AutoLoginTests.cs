using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The rule that decides whether Wizard101 is signed in or left to ask for a password.
/// </summary>
/// <remarks>
/// Getting this wrong is not cosmetic: the wrong answer is a session key from another machine, which
/// the client answers with an error instead of the game.
/// </remarks>
public class AutoLoginTests {

    // Invented values: ids and keys are shaped like this, and none of these belong to anybody.
    private const ulong AccountId = 1234567890123456789;
    private const ulong OtherAccountId = 2222222222222222222;
    private const ulong ThisMachine = 123456789012;
    private const ulong OtherMachine = 999999999999;
    private const string Key = "cGxhY2Vob2xkZXItc2Vzc2lvbi1rZXktbm90LXJlYWw=";

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    private static PlayerDatabaseContents Database(
        PlayerSessionKey[]? keys = null,
        string? problem = null,
        ulong accountId = AccountId) =>
        new([new PlayerAccount(accountId, "exampleadmin")], keys ?? [], problem);

    private static PlayerSessionKey StoredKey(ulong machineId = ThisMachine, DateTimeOffset? expires = null)
        => new(AccountId, machineId, Key, "ClientKeyPairs/1-A", expires ?? Now.AddHours(20));

    [Fact]
    public void SignsInWithTheKeyStoredForThisMachine() {
        var login = AutoLogin.Choose(Database([StoredKey()]), AccountId, ThisMachine, Now);

        Assert.NotNull(login);
        Assert.Equal(AccountId, login!.AccountId);
        Assert.Equal(Key, login.ClientKey2);
    }

    [Fact]
    public void SignsInWhileTheMachineIsStillUnknown() {
        // Nothing on this machine says which machine it is, and the database is local to this server.
        // The stored key is therefore the only candidate; if it turns out to belong to another machine
        // the client simply asks to sign in, and that sign-in teaches the launcher the machine id.
        var login = AutoLogin.Choose(Database([StoredKey()]), AccountId, machineId: null, Now);

        Assert.NotNull(login);
        Assert.Equal(Key, login!.ClientKey2);
    }

    [Fact]
    public void NeverHandsOverAnotherMachinesKey() {
        var login = AutoLogin.Choose(Database([StoredKey(OtherMachine)]), AccountId, ThisMachine, Now);

        Assert.Null(login);
    }

    [Fact]
    public void NeverHandsOverAnExpiredKey() {
        var expired = StoredKey(expires: Now.AddSeconds(-1));

        Assert.Null(AutoLogin.Choose(Database([expired]), AccountId, ThisMachine, Now));
    }

    [Fact]
    public void DoesNothingWithoutAKey() {
        Assert.Null(AutoLogin.Choose(Database(), AccountId, ThisMachine, Now));
    }

    [Fact]
    public void DoesNothingForAnAccountThatIsNotThere() {
        Assert.Null(AutoLogin.Choose(Database([StoredKey()]), OtherAccountId, ThisMachine, Now));
    }

    [Fact]
    public void DoesNothingWhenTheDatabaseWasNotReadAsExpected() {
        var contents = Database([StoredKey()], problem: "An account document carried no usable AccountId.");

        Assert.False(contents.IsTrustworthy);
        Assert.Null(AutoLogin.Choose(contents, AccountId, ThisMachine, Now));
    }

    [Fact]
    public void AKeyThatChangedIsHowTheMachineIdIsLearned() {
        var before = Database();
        var after = Database([StoredKey(ThisMachine)]);

        Assert.Equal(ThisMachine, AutoLogin.MachineIdFrom(before, after, AccountId));
    }

    [Fact]
    public void AKeyThatIsReplacedByANewOneAlsoTeachesIt() {
        var before = Database([new PlayerSessionKey(AccountId, ThisMachine, "old", "ClientKeyPairs/1-A", null)]);
        var after = Database([StoredKey()]);

        Assert.Equal(ThisMachine, AutoLogin.MachineIdFrom(before, after, AccountId));
    }

    [Fact]
    public void AKeyThatHasNotMovedTeachesNothing() {
        var before = Database([StoredKey()]);
        var after = Database([StoredKey()]);

        // Adopting the id of a key nobody touched is how a launcher ends up believing it is another
        // machine.
        Assert.Null(AutoLogin.MachineIdFrom(before, after, AccountId));
    }

    [Fact]
    public void AnUnreadableDatabaseTeachesNothing() {
        Assert.Null(AutoLogin.MachineIdFrom(Database(), Database(problem: "no answer"), AccountId));
    }
}
