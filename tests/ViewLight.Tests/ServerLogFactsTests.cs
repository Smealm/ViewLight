using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Reading facts out of the server's own log lines.
/// </summary>
/// <remarks>
/// The lines are copied out of real Imlight logs from two local installs - <c>&lt;server&gt;\logs\</c> -
/// rather than written to match the parser. That is the point of the test: the sentence has to be the
/// server's, because that is what the launcher will be reading when it matters. The one test that writes
/// its own lines says so.
/// </remarks>
public class ServerLogFactsTests {

    private static readonly DateTimeOffset SignedInAt =
        new(2026, 10, 13, 16, 33, 36, 83, TimeSpan.FromHours(-4));

    private static ServerLogFact Single(string line) => Assert.Single(ServerLogFacts.Parse(line));

    // ------------------------------------------------------------------ the sentences

    [Fact]
    public void ReadsAWizardBeingSentIntoTheGame() {
        var fact = Assert.IsType<WizardSignedIn>(Single(
            "2026-10-13 16:33:36.083 -04:00 [INF] Sending wizard Enzo ArcaneShield to game server 127.0.0.1:12333."));

        Assert.Equal("Enzo ArcaneShield", fact.Wizard);
        Assert.Equal(SignedInAt, fact.At);
    }

    [Fact]
    public void ReadsAGameServersConnectionGoingAway() {
        var fact = Assert.IsType<ServerConnectionLost>(Single(
            "2026-10-13 16:35:19.287 -04:00 [INF] Imlight.Game.Centaur lost connection to 10.0.0.246"));

        Assert.True(fact.Game);
        Assert.Equal("10.0.0.246", fact.Host);
    }

    [Fact]
    public void TellsTheLoginConnectionApartFromAGameOne() {
        // The client is handed to a game server the moment it signs in, and the login server drops its
        // connection then - which is the middle of a session, not the end of one.
        var fact = Assert.IsType<ServerConnectionLost>(Single(
            "2026-10-13 16:33:36.250 -04:00 [INF] Imlight.Login lost connection to 10.0.0.246"));

        Assert.False(fact.Game);
    }

    [Theory]
    [InlineData("2026-10-13 16:39:16.460 -04:00 [DBG] Duel 4972210732661112830 | Duel ended. Creatures win.",
        "4972210732661112830", false)]
    [InlineData("2026-10-12 17:29:36.611 -04:00 [DBG] Duel 4972648824001099218 | Duel ended. Players win.",
        "4972648824001099218", true)]
    public void ReadsWhoWonADuel(string line, string duelId, bool playersWon) {
        var fact = Assert.IsType<DuelFinished>(Single(line));

        Assert.Equal(playersWon, fact.PlayersWon);
        Assert.Equal(duelId, fact.DuelId);
    }

    [Fact]
    public void ReadsAZoneBeingEntered() {
        var fact = Assert.IsType<ZoneEntered>(Single(
            "2026-10-13 16:33:42.164 -04:00 [DBG] Enzo ArcaneShield added to zone WizardZone_GolemCourt."));

        Assert.Equal("Enzo ArcaneShield", fact.Wizard);
        Assert.Equal("WizardZone_GolemCourt", fact.Zone);
    }

    [Fact]
    public void ReadsACommandBeingUsed() {
        var fact = Assert.IsType<CommandUsed>(Single(
            "2026-10-13 16:34:25.270 -04:00 [INF] admin Uses command: account create jordan purple4652786"));

        Assert.Equal("admin", fact.Username);
        Assert.Equal("account create jordan purple4652786", fact.Command);
    }

    // ------------------------------------------------------------------ what is not a fact

    [Fact]
    public void AZoneWithNoNameSaysNothingAboutWhereAnyoneWent() {
        // A real line: "Enzo ArcaneShield added to zone ." - the zone had no name to give.
        Assert.Empty(ServerLogFacts.Parse("2026-10-13 16:35:20.185 -04:00 [DBG] Enzo ArcaneShield added to zone ."));
    }

    [Fact]
    public void MostLinesAreAboutSomethingElseEntirely() {
        string[] nothing = [
            "2026-10-13 11:59:24.983 -04:00 [INF] Imlight configuration loaded.",
            "2026-10-13 12:30:49.123 -04:00 [WRN] No handler found for requirement type: ReqMana",
            "2026-10-13 12:08:39.215 -04:00 [DBG] Recalculation of game stats for Jordan DeathBane.",
            "**not a log line at all**",
            "",
        ];

        foreach (var line in nothing) {
            Assert.Empty(ServerLogFacts.Parse(line));
        }
    }

    [Fact]
    public void ALineWithNoReadableTimeIsNotDatedNow() {
        // Half a line, or a format this launcher has never seen. Neither is something to date by guessing.
        Assert.Empty(ServerLogFacts.Parse("Sending wizard Enzo ArcaneShield to game server 127.0.0.1:12333."));
    }

    [Fact]
    public void TheOtherLogFormatIsReadToo() {
        // <b>These two lines are written, not copied.</b> Both real installs seen so far log in the bracketed
        // spelling above; this one is what the other setting produces - Imlight's own Logger adds the calling
        // class between the level and the message, and its documented format leaves the offset off. The
        // sentences in them are the server's, which is what the launcher matches on.
        var fact = Assert.IsType<ZoneEntered>(Single(
            "2026-10-13 16:33:42.164 DBG WizardCollection.UpdateCharacterLevel@L279 : "
            + "Jordan DeathBane added to zone WizardZone_TheCommons."));

        Assert.Equal("Jordan DeathBane", fact.Wizard);
        Assert.Equal("WizardZone_TheCommons", fact.Zone);

        // No offset in the line means the clock it was written on is the server's own, so the time is read
        // as local - and the wall clock it names is the one that comes back.
        Assert.Equal(new DateTime(2026, 10, 13, 16, 33, 42, 164), fact.At.DateTime);

        var command = Assert.IsType<CommandUsed>(Single(
            "2026-10-13 16:34:25.270 INF CommandDispatcher.Dispatch@L169 : "
            + "admin Uses command: account info jordan"));

        Assert.Equal("admin", command.Username);
    }

    // ------------------------------------------------------------------ reading a growing file

    [Fact]
    public void OnlyWholeLinesAreRead() {
        var (lines, consumed) = ServerLogFacts.CompleteLines("first\r\nsecond\r\nhalf a thi");

        Assert.Equal(["first\r", "second\r"], lines);

        // Exactly the two complete lines, so the fragment is left for the next read and parsed once, whole.
        Assert.Equal("first\r\nsecond\r\n".Length, consumed);
    }

    [Fact]
    public void AChunkWithNoLineBreakReadsNothing() {
        var (lines, consumed) = ServerLogFacts.CompleteLines("still being written");

        Assert.Empty(lines);
        Assert.Equal(0, consumed);
    }
}
