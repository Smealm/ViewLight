using System.Globalization;

namespace ViewLight.Services;

/// <summary>
/// Something the server said that a profile cares about.
/// </summary>
/// <remarks>
/// The server log is the only place several of these exist at all - the player database knows what a
/// wizard <i>has</i>, not what they did - so the facts are read out of the log in the server's own words
/// and kept with the line they came from. That line is what a badge ends up quoting, so nothing here
/// invents a number the server never wrote.
/// </remarks>
public abstract record ServerLogFact(DateTimeOffset At, string Raw) {

    /// <summary>The line, as the evidence a badge points at.</summary>
    public string Evidence => Raw.Trim();
}

/// <summary>
/// A wizard was sent to a game server, which happens once per sign-in.
/// </summary>
/// <remarks>
/// Imlight logs this from the login server, so it means "this account got into the game", not merely
/// "the client opened".
/// </remarks>
public sealed record WizardSignedIn(DateTimeOffset At, string Raw, string Wizard) : ServerLogFact(At, Raw);

/// <summary>A connection to the login or game server went away.</summary>
/// <remarks>
/// The game-server ones are what end a play session. Which server it was matters: the login server drops
/// its own connection the moment the client is handed to a game server, which is the middle of a session
/// rather than the end of one.
/// </remarks>
public sealed record ServerConnectionLost(DateTimeOffset At, string Raw, bool Game, string Host) : ServerLogFact(At, Raw);

/// <summary>A duel ended, and which side won it.</summary>
/// <remarks>
/// The log names the duel and the winning side, not the player: participants are logged by slot. That is
/// why a win is credited to the profile whose player was the only one signed in at the time - see
/// <see cref="ProfileLedger"/>.
/// </remarks>
public sealed record DuelFinished(DateTimeOffset At, string Raw, string DuelId, bool PlayersWon) : ServerLogFact(At, Raw);

/// <summary>
/// A creature took part in a duel, named by the template it was spawned from.
/// </summary>
/// <remarks>
/// <para>
/// <b>This line does not exist yet, and it is the whole reason boss badges cannot be earned.</b> Imlight
/// logs a duel's participants by slot and runtime id only:
/// </para>
/// <list type="bullet">
///   <item><description><c>Duel 5348490766442470254 | Slot 1 | Participant 141BD joined</c></description></item>
///   <item><description><c>Duel 5348490766442470254 | Duel ended. Players win.</c></description></item>
/// </list>
/// <para>
/// A <c>141BD</c> is an id this process made up when it spawned the creature, so it means nothing to
/// anyone outside that run - and no line anywhere writes the creature's own template ID or name. The
/// client knows both (it draws the boss's name), and the server holds both (it created the thing from
/// its template), they simply never reach the file the launcher is allowed to read. Without them, a win
/// cannot be attributed to a creature, so "you beat Wraith-WCBoss" is not a claim the launcher can make.
/// </para>
/// <para>
/// <b>What the server has to write</b> - one line when a duel is created, alongside the existing
/// participant lines, in the same shape:
/// </para>
/// <code>
/// Duel &lt;duelId&gt; | Slot &lt;slot&gt; | Creature &lt;templateId&gt; [&lt;name&gt;] joined
/// Duel 5348490766442470254 | Slot 4 | Creature 35166 Wraith-WCBoss-L05 joined
/// </code>
/// <para>
/// The template ID is the part that matters: it is what <c>creature.manifest</c> is keyed by, so it is
/// what ties a win to a named creature. The name is optional and only ever used as the badge's evidence -
/// the launcher already knows the name from the manifest and prefers its own copy.
/// </para>
/// <para>
/// This is parsed the moment it appears, so the day the server writes it, every boss badge below starts
/// working with no change here. Until then they are simply locked, which is honest.
/// </para>
/// </remarks>
public sealed record DuelCreature(
    DateTimeOffset At, string Raw, string DuelId, int Slot, uint TemplateId, string Name)
    : ServerLogFact(At, Raw);

/// <summary>A wizard entered a zone, named the way the server names its zones.</summary>
public sealed record ZoneEntered(DateTimeOffset At, string Raw, string Wizard, string Zone) : ServerLogFact(At, Raw);

/// <summary>
/// The server started loading a zone, named by its path rather than by its name.
/// </summary>
/// <remarks>
/// The path begins with the world: <c>WizardCity/WC_Streets/WC_Unicorn</c> is a zone of Wizard City. That
/// is the only place the server ever says which world a zone is in, and it says it in the same breath as
/// the zone's own name - see <see cref="ZoneNamed"/> - which is how the two are paired.
/// </remarks>
public sealed record ZoneLoadStarted(DateTimeOffset At, string Raw, string Path) : ServerLogFact(At, Raw) {

    /// <summary>The world the path begins with, or empty for a path that names none.</summary>
    public string World => WorldOf(Path);

    /// <summary>The first segment of a zone path, which is the world it belongs to.</summary>
    public static string WorldOf(string path) {
        var slash = path.IndexOf('/');

        return slash <= 0 ? string.Empty : path[..slash];
    }
}

/// <summary>
/// The server named a zone it has loaded, the way it names its zones: <c>WizardZone_UnicornWay</c>.
/// </summary>
/// <remarks>
/// This is the same name that appears in "added to zone" when somebody walks in, which is what ties the
/// zone a wizard is in to the world the server was loading when it appeared.
/// </remarks>
public sealed record ZoneNamed(DateTimeOffset At, string Raw, string Zone) : ServerLogFact(At, Raw);

/// <summary>A command was used, with the account it came from.</summary>
public sealed record CommandUsed(DateTimeOffset At, string Raw, string Username, string Command) : ServerLogFact(At, Raw);

/// <summary>
/// Reads the facts out of the server's log lines.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a text match rather than a parse of the whole line format. Imlight's log format is a
/// setting (<c>Logging.LogFormat</c>) and the spellings in the wild already differ - one writes
/// <c>[INF]</c>, another writes the level bare and adds the calling class - so nothing here depends on
/// where the level sits or how the timestamp is punctuated. Only the sentence matters, and the sentence
/// comes from the server's own source: it changes when the server's behaviour does, which is exactly when
/// this should have to change too.
/// </para>
/// <para>
/// A line with no readable timestamp yields nothing. That is not a place to guess: a badge dated "now"
/// because the date could not be read would be a claim about when something happened.
/// </para>
/// </remarks>
public static class ServerLogFacts {

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>How many characters a timestamp with a UTC offset takes, "yyyy-MM-dd HH:mm:ss.fff zzz".</summary>
    private const int TimestampWithOffsetLength = 30;

    /// <summary>Every fact this line states. Usually none or one.</summary>
    public static IReadOnlyList<ServerLogFact> Parse(string? line) {
        var facts = new List<ServerLogFact>(1);

        if (string.IsNullOrWhiteSpace(line)) {
            return facts;
        }

        var text = line.TrimEnd('\r', '\n');

        if (!TryTimestamp(text, out var at)) {
            return facts;
        }

        // "Sending wizard Jordan DeathBane to game server 127.0.0.1:12333."
        if (Between(text, "Sending wizard ", " to game server ") is { Length: > 0 } signedIn) {
            facts.Add(new WizardSignedIn(at, text, signedIn));
        }

        // "Imlight.Game.Centaur lost connection to 10.0.0.246" - the name in front of the sentence says
        // which server it was, and only a game server ends a session. A game server is named per realm
        // ("Imlight.Game.Centaur"), so this is about the kind in the name rather than its ending.
        if (NameBefore(text, " lost connection to ") is { Length: > 0 } logger
            && After(text, " lost connection to ") is { Length: > 0 } host) {
            facts.Add(new ServerConnectionLost(at, text, logger.Contains(".Game", StringComparison.Ordinal), host));
        }

        // "Duel 4972210732661112830 | Duel ended. Players win."
        if (text.Contains("Duel ended. Players win.", StringComparison.Ordinal)) {
            facts.Add(new DuelFinished(at, text, DuelIdOf(text), PlayersWon: true));
        }
        else if (text.Contains("Duel ended. Creatures win.", StringComparison.Ordinal)) {
            facts.Add(new DuelFinished(at, text, DuelIdOf(text), PlayersWon: false));
        }

        // "Duel 5348490766442470254 | Slot 4 | Creature 35166 Wraith-WCBoss-L05 joined" - the line the
        // server does not write yet. Read the moment it does; see DuelCreature for why it is needed.
        if (CreatureOf(text, out var creatureSlot, out var creatureId, out var creatureName)) {
            facts.Add(new DuelCreature(at, text, DuelIdOf(text), creatureSlot, creatureId, creatureName));
        }

        // "Jordan DeathBane added to zone WizardZone_UnicornWay." An empty zone name is a zone with none
        // set, which says nothing about where anyone went.
        if (NameBefore(text, " added to zone ") is { Length: > 0 } wizard
            && After(text, " added to zone ").TrimEnd('.', ' ') is { Length: > 0 } zone) {
            facts.Add(new ZoneEntered(at, text, wizard, zone));
        }

        // "Game world starting zone load: WizardCity/WC_Streets/WC_Unicorn" and
        // "Game world created new zone: WizardCity/WC_Hub" - the same load, said twice, and either one on
        // its own is enough.
        var zonePath = After(text, "Game world starting zone load: ");
        if (zonePath.Length == 0) {
            zonePath = After(text, "Game world created new zone: ");
        }

        if (zonePath.Length > 0 && zonePath.Contains('/')) {
            facts.Add(new ZoneLoadStarted(at, text, zonePath));
        }

        // "Zone WizardZone_UnicornWay loaded in 170ms." - the zone's own name. Read from the line that ends
        // the load, which is written at INFO: the supervisor lines around it are DEBUG, and a launcher whose
        // server logs at the stock level must still be able to learn which world a zone is in.
        if (Between(text, "Zone ", " loaded in ") is { Length: > 0 } namedZone
            && namedZone.StartsWith("WizardZone_", StringComparison.Ordinal)) {
            facts.Add(new ZoneNamed(at, text, namedZone));
        }

        // "admin Uses command: account create jordan purple4652786"
        if (NameBefore(text, " Uses command: ") is { Length: > 0 } username
            && After(text, " Uses command: ") is { Length: > 0 } command) {
            facts.Add(new CommandUsed(at, text, username, command));
        }

        return facts;
    }

    /// <summary>
    /// The complete lines in a chunk of the log, and how many characters of the chunk they took up.
    /// </summary>
    /// <remarks>
    /// A line still being written is not a line yet: reading half of it would parse half a command and
    /// then parse the whole thing again a second later, which is how one command becomes two.
    /// </remarks>
    public static (IReadOnlyList<string> Lines, int Consumed) CompleteLines(string chunk) {
        var lines = new List<string>();
        var start = 0;

        while (true) {
            var end = chunk.IndexOf('\n', start);
            if (end < 0) {
                break;
            }

            lines.Add(chunk[start..end]);
            start = end + 1;
        }

        return (lines, start);
    }

    /// <summary>
    /// Reads "| Creature &lt;templateId&gt; [&lt;name&gt;] joined" into its parts.
    /// </summary>
    /// <remarks>
    /// The template ID is required and has to be a number: a line that says "Creature" without one is not
    /// a fact about a creature, and guessing which creature it was is exactly the mistake the id exists to
    /// prevent. The name is whatever sits between the ID and the word "joined", and may be left out.
    /// </remarks>
    private static bool CreatureOf(string text, out int slot, out uint templateId, out string name) {
        slot = -1;
        templateId = 0;
        name = string.Empty;

        var tail = After(text, "| Creature ");
        if (tail.Length == 0) {
            return false;
        }

        var words = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0
            || !uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out templateId)) {
            return false;
        }

        if (int.TryParse(Between(text, "| Slot ", " |"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) {
            slot = parsed;
        }

        // Everything up to the trailing verb, which is what the name sits in front of.
        var named = new List<string>();
        for (var i = 1; i < words.Length; i++) {
            if (string.Equals(words[i], "joined", StringComparison.OrdinalIgnoreCase)) {
                break;
            }

            named.Add(words[i]);
        }

        name = string.Join(' ', named);
        return true;
    }

    private static string DuelIdOf(string text) {
        // "Duel <id> | ..." - the id is what makes two duels tellable apart in a badge's evidence.
        var after = After(text, "Duel ");
        var bar = after.IndexOf('|');

        return (bar < 0 ? after : after[..bar]).Trim();
    }

    /// <summary>
    /// The timestamp a line starts with, in either spelling Imlight writes: the stock
    /// <c>2026-10-13 16:33:36.083 -04:00</c>, and one without the offset when a log format leaves it out.
    /// </summary>
    private static bool TryTimestamp(string line, out DateTimeOffset at) {
        at = default;

        // "yyyy-MM-dd HH:mm:ss.fff", which every format begins with.
        if (line.Length < 23
            || !DateTime.TryParseExact(line[..23], TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) {
            return false;
        }

        // A " -04:00" after the milliseconds is the server saying which offset it wrote in. Without one the
        // offset is unknown, and the clock it was written on is the server's own - so it is read as local
        // time, which is what it was written as.
        if (line.Length >= TimestampWithOffsetLength
            && line[23] == ' '
            && DateTimeOffset.TryParseExact(line[..TimestampWithOffsetLength], $"{TimestampFormat} zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset)) {
            at = offset;
            return true;
        }

        at = new DateTimeOffset(local);
        return true;
    }

    /// <summary>
    /// Whatever the line puts in front of a sentence - the timestamp, the level, and the calling class
    /// when the format includes it - taken off, leaving the name that sentence is about.
    /// </summary>
    private static string NameBefore(string text, string marker) {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) {
            return string.Empty;
        }

        var before = text[..index];

        // The format that names the calling class writes it between the level and the message, with a
        // colon: "DBG WizardCollection.UpdateCharacterLevel@L279 : Jordan DeathBane added to zone ...".
        var colon = before.LastIndexOf(':');
        if (colon >= 0) {
            before = before[(colon + 1)..];
        }

        // The format that brackets the level puts it last before the message: "[DBG] Enzo ArcaneShield".
        var bracket = before.LastIndexOf(']');
        if (bracket >= 0) {
            before = before[(bracket + 1)..];
        }

        return before.Trim();
    }

    private static string After(string text, string marker) {
        var index = text.IndexOf(marker, StringComparison.Ordinal);

        return index < 0 ? string.Empty : text[(index + marker.Length)..].Trim();
    }

    private static string Between(string text, string start, string end) {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) {
            return string.Empty;
        }

        from += start.Length;

        var to = text.IndexOf(end, from, StringComparison.Ordinal);

        return to < 0 ? string.Empty : text[from..to].Trim();
    }
}
