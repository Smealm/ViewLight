using System.Globalization;
using System.Text.Json;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Reads the documents Imlight's models describe for profiles: wizards, the online list, and the command
/// log.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the same one the account guard follows, applied to different data: <b>refuse rather than
/// guess, and never claim something the server did not write.</b> A wizard document with no id cannot be
/// attributed to anybody and is refused; a level field of the wrong type means the layout has moved under
/// the launcher, so the whole read is refused rather than read as zero.
/// </para>
/// <para>
/// It is strict about that and relaxed about absence, which is not a contradiction: a wizard document with
/// no quest registry has no completed quests, and reading that as none can only ever under-count. Reading a
/// changed field as zero is different - it would quietly stop every badge about progress from ever being
/// earned, and nothing would say why.
/// </para>
/// </remarks>
internal static class PlayerProfileLayout {

    /// <summary>The collection Imlight stores player characters in.</summary>
    internal const string WizardsCollection = "Wizards";

    /// <summary>The collection it writes while a wizard is attached to a game server.</summary>
    internal const string OnlinePlayersCollection = "OnlinePlayers";

    /// <summary>The collection it writes every command to, before running it.</summary>
    internal const string CommandLogCollection = "CommandLog";

    /// <summary>
    /// What Imlight appends to a quest's name in the registry when the quest is finished: a registry key
    /// <c>WC-TUT-C05-001_Complete</c> is what <c>HasCompletedQuest</c> looks for, so it is what a completed
    /// quest is. A half-finished quest has no such key - only its goals have entries.
    /// </summary>
    internal const string QuestCompleteSuffix = "_Complete";

    internal static PlayerProfileData Inspect(IReadOnlyList<JsonElement> documents) {
        var wizards = new List<PlayerWizardRecord>();
        var online = new List<PlayerOnlineRecord>();
        var commands = new List<PlayerCommandRecord>();
        string? problem = null;

        foreach (var document in documents) {
            switch (PlayerDatabaseLayout.CollectionOf(document)) {
                case WizardsCollection:
                    if (TryReadWizard(document, out var wizard, out var wizardProblem)) {
                        wizards.Add(wizard!);
                    }
                    else {
                        problem ??= wizardProblem;
                    }

                    break;

                case OnlinePlayersCollection:
                    if (TryReadOnlinePlayer(document, out var player, out var playerProblem)) {
                        online.Add(player!);
                    }
                    else {
                        problem ??= playerProblem;
                    }

                    break;

                case CommandLogCollection:
                    if (TryReadCommand(document, out var command, out var commandProblem)) {
                        commands.Add(command!);
                    }
                    else {
                        problem ??= commandProblem;
                    }

                    break;
            }
        }

        return new PlayerProfileData(wizards, online, commands, problem);
    }

    private static bool TryReadWizard(JsonElement document, out PlayerWizardRecord? wizard, out string? problem) {
        wizard = null;
        problem = null;

        if (!PlayerDatabaseLayout.TryReadUlong(document, "AccountId", out var accountId)
            || !PlayerDatabaseLayout.TryReadUlong(document, "CharId", out var charId)) {
            problem = "A wizard document carried no usable AccountId and CharId.";
            return false;
        }

        var levelField = TryReadNumber(document, "MagicSchoolBehavior", "Level", out var level);
        if (levelField == Field.Unreadable) {
            problem = "A wizard document did not carry a level in MagicSchoolBehavior.Level.";
            return false;
        }

        var goldField = TryReadNumber(document, "GameStats", "m_currentGold", out var gold);
        if (goldField == Field.Unreadable) {
            problem = "A wizard document did not carry gold in GameStats.m_currentGold.";
            return false;
        }

        if (!TryReadCompletedQuests(document, out var quests, out var questProblem)) {
            problem = questProblem;
            return false;
        }

        if (!TryReadLearnedSpells(document, out var spells, out var spellProblem)) {
            problem = spellProblem;
            return false;
        }

        var (name, nameKeys, gender) = ReadName(document);

        wizard = new PlayerWizardRecord(
            accountId, charId, (int) level, gold, quests, ReadSchool(document), spells,
            name, nameKeys, gender);

        return true;
    }

    /// <summary>
    /// The wizard's own name, as the character document carries it.
    /// </summary>
    /// <remarks>
    /// Never refuses, for the same reason the school never refuses: a name that cannot be read costs that
    /// wizard its name and nothing else. What comes back is the raw material rather than a name - the
    /// override when there is one, and otherwise the packed key and the gender that resolve it through the
    /// game's own tables, which is done where the game folder is known.
    /// </remarks>
    private static (string Name, uint? NameKeys, uint? Gender) ReadName(JsonElement document) {
        if (!document.TryGetProperty("PlayerNameBehavior", out var behavior)
            || behavior.ValueKind != JsonValueKind.Object) {
            return (string.Empty, null, null);
        }

        var name = behavior.TryGetProperty("NameOverride", out var over)
                   && over.ValueKind == JsonValueKind.String
            ? over.GetString() ?? string.Empty
            : string.Empty;

        uint? keys = behavior.TryGetProperty("NameIndices", out var indices)
                     && indices.ValueKind == JsonValueKind.Number
                     && indices.TryGetUInt32(out var packed)
            ? packed
            : null;

        uint? gender = behavior.TryGetProperty("Gender", out var value)
                       && value.ValueKind == JsonValueKind.Number
                       && value.TryGetUInt32(out var stored)
            ? stored
            : null;

        return (name, keys, gender);
    }

    /// <summary>
    /// The school the wizard belongs to, named, or nothing when the document does not say.
    /// </summary>
    /// <remarks>
    /// <b>This is the one read here that never refuses.</b> Level and gold have to be readable, because a
    /// badge about progress is judged by multiplying against them and a zero read from a moved field would
    /// quietly stop every one of them from ever being earned. A school is different: it only ever adds,
    /// so a document that does not say - one that has not been given a school, or a revision that moved
    /// the field, or a number that is not one of the seven a wizard can be - reads as no school and costs
    /// nothing but that wizard's school badges, which stay honestly locked. Refusing the document instead
    /// would switch off every badge about the account over a field nothing else depends on.
    /// </remarks>
    private static string ReadSchool(JsonElement document)
        => TryReadNumber(document, "MagicSchoolBehavior", "MagicSchool", out var id) == Field.Readable
            ? WizardSchool.NameOf((uint) id)
            : string.Empty;

    /// <summary>
    /// The spells in the wizard's spellbook.
    /// </summary>
    /// <remarks>
    /// A missing spellbook is a wizard who has learned nothing, which is what a new character is. A
    /// spellbook whose list is not a list of numbers is a layout this launcher does not know.
    /// </remarks>
    private static bool TryReadLearnedSpells(
        JsonElement document, out IReadOnlyList<uint> spells, out string? problem) {

        spells = [];
        problem = null;

        if (!document.TryGetProperty("SpellbookBehavior", out var behavior)) {
            return true;
        }

        if (behavior.ValueKind != JsonValueKind.Object
            || !behavior.TryGetProperty("LearnedSpellTemplateIds", out var list)) {
            return true;
        }

        if (list.ValueKind != JsonValueKind.Array) {
            problem = "A wizard document's SpellbookBehavior.LearnedSpellTemplateIds was not a list.";
            return false;
        }

        var learned = new List<uint>();

        foreach (var entry in list.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetUInt32(out var templateId)) {
                problem = "A wizard document's spellbook held an entry that was not a spell template ID.";
                return false;
            }

            learned.Add(templateId);
        }

        spells = learned;
        return true;
    }

    /// <summary>
    /// The quests whose registry entry says they were finished.
    /// </summary>
    /// <remarks>
    /// A missing registry is a wizard who has finished nothing, which is the truth about a new character.
    /// A registry that is there but is not a set of quest names is a layout this launcher does not know,
    /// and reading it as "no quests" would quietly disable every badge about progress.
    /// </remarks>
    private static bool TryReadCompletedQuests(
        JsonElement document, out IReadOnlyList<string> quests, out string? problem) {

        quests = [];
        problem = null;

        if (!document.TryGetProperty("QuestBehavior", out var behavior)
            || !behavior.TryGetProperty("Registry", out var registry)) {
            return true;
        }

        if (behavior.ValueKind != JsonValueKind.Object || registry.ValueKind != JsonValueKind.Object) {
            problem = "A wizard document's QuestBehavior.Registry was not a set of quest names.";
            return false;
        }

        var completed = new List<string>();

        foreach (var entry in registry.EnumerateObject()) {
            if (!entry.Name.EndsWith(QuestCompleteSuffix, StringComparison.Ordinal)) {
                continue;
            }

            // A key that exists with a zero value is a quest that was un-completed again - which Imlight
            // does when it wants a quest to be offered once more - so it is not a completion.
            if (entry.Value.ValueKind == JsonValueKind.Number
                && entry.Value.TryGetInt64(out var value)
                && value > 0) {
                completed.Add(entry.Name[..^QuestCompleteSuffix.Length]);
            }
        }

        quests = completed;
        return true;
    }

    private static bool TryReadOnlinePlayer(
        JsonElement document, out PlayerOnlineRecord? player, out string? problem) {

        player = null;
        problem = null;

        if (!PlayerDatabaseLayout.TryReadUlong(document, "AccountId", out var accountId)) {
            problem = "An online player document carried no usable AccountId.";
            return false;
        }

        // The character is what a name from the log gets attached to, but being online is the account's
        // business: a record that cannot say which character it is still says the account is playing.
        PlayerDatabaseLayout.TryReadUlong(document, "CharacterId", out var charId);

        player = new PlayerOnlineRecord(
            accountId,
            charId,
            ReadText(document, "CurrentZone"),
            ReadText(document, "CurrentRealm"));

        return true;
    }

    private static bool TryReadCommand(
        JsonElement document, out PlayerCommandRecord? command, out string? problem) {

        command = null;
        problem = null;

        if (!PlayerDatabaseLayout.TryReadUlong(document, "AccountId", out var accountId)) {
            problem = "A command log document carried no usable AccountId.";
            return false;
        }

        PlayerDatabaseLayout.TryReadUlong(document, "CharacterId", out var charId);

        command = new PlayerCommandRecord(accountId, charId, ReadTime(document), ReadText(document, "Message"));
        return true;
    }

    /// <summary>What came of looking for a number inside a nested object.</summary>
    private enum Field {

        /// <summary>The object is not there at all: a document that never got that far.</summary>
        Absent,

        /// <summary>The number is there and reads as one.</summary>
        Readable,

        /// <summary>The object or the field is there but is not what it should be.</summary>
        Unreadable,
    }

    /// <summary>
    /// A number out of a nested object of a character document.
    /// </summary>
    /// <remarks>
    /// Absence and nonsense are told apart on purpose. No such object is a document that has not been
    /// filled in, and reading that as zero can only ever under-count; an object whose field is missing or
    /// is the wrong type is a layout that has moved, and reading <i>that</i> as zero would silently stop
    /// every badge about progress from ever being earned.
    /// </remarks>
    private static Field TryReadNumber(JsonElement document, string container, string field, out long value) {
        value = 0;

        if (!document.TryGetProperty(container, out var nested)) {
            return Field.Absent;
        }

        if (nested.ValueKind != JsonValueKind.Object
            || !nested.TryGetProperty(field, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out value)) {
            return Field.Unreadable;
        }

        return Field.Readable;
    }

    private static string ReadText(JsonElement document, string name)
        => document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// When a command was used, or the beginning of time when the document does not say.
    /// </summary>
    /// <remarks>
    /// A command with no readable timestamp still happened, so it still counts towards the account's
    /// standing - it just cannot be the "last command" on the profile, because that would mean claiming
    /// when it was used.
    /// </remarks>
    private static DateTimeOffset ReadTime(JsonElement document) {
        if (!document.TryGetProperty("TimeStamp", out var stamp)) {
            return default;
        }

        return stamp.ValueKind switch {
            JsonValueKind.String when DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => default,
        };
    }
}
