using System.Globalization;
using System.Text;
using System.Xml;

namespace ViewLight.Services;

/// <summary>
/// Turns the name a wizard is stored under into the name the game shows.
/// </summary>
/// <remarks>
/// <para>
/// Imlight does not store a wizard's name as text. It stores a packed pair of table indices - one byte each
/// for the first, middle and last name - and resolves them through the game's own name tables, which live in
/// <c>Root.wad</c>: <c>CharacterNames.xml</c> lists a key per index, and <c>Locale/&lt;language&gt;/CharacterNames.lang</c>
/// maps each key to the English word. Those two files are what this reads.
/// </para>
/// <para>
/// <b>Why the launcher can read them at all.</b> Everything else in the archive is a <c>BINd</c> object
/// graph that needs Imcodec's generated type cache - but these two files are not: the name table is ordinary
/// UTF-8 XML and the locale file is ordinary UTF-16 text in key/delimiter/value runs. So the same name the
/// client draws can be built here, without Imcodec and without guessing.
/// </para>
/// <para>
/// <b>Nothing is invented.</b> A character whose name key points outside the tables, or whose gender is not
/// one this understands, resolves to nothing and is shown by its id - which is worse to look at than a name
/// and much better than somebody else's name. Names the server's own log has already given are kept ahead of
/// anything read here, because the log naming a wizard is direct evidence and this is a reconstruction.
/// </para>
/// </remarks>
public sealed class WizardNameTable {

    /// <summary>The name table inside <c>Root.wad</c>, as plain UTF-8 XML.</summary>
    public const string BankFileName = "CharacterNames.xml";

    /// <summary>Where the English words for the bank's keys live, for the stock language.</summary>
    public static string LocaleFileName(string language) => $"Locale/{language}/CharacterNames.lang";

    /// <summary>The language Imlight reads when its config does not say otherwise.</summary>
    public const string DefaultLanguage = "en-US";

    /// <summary>
    /// The name table for a game folder, or <see cref="Unavailable"/> when there is nothing to read.
    /// </summary>
    /// <remarks>
    /// Cheap and lazy: the archive is 300 MB and its index is the only part of it that is read to find these
    /// two entries, and that does not happen until a wizard actually needs a name. Most do not - the server's
    /// log names anyone who has signed in.
    /// </remarks>
    public static WizardNameTable For(string? gameFolder)
        => string.IsNullOrWhiteSpace(gameFolder) ? Unavailable : new WizardNameTable(gameFolder);

    /// <summary>A table that has nothing to read, for a launcher with no game folder yet.</summary>
    public static WizardNameTable Unavailable { get; } = new();

    private readonly string? _gameFolder;
    private readonly Lock _gate = new();

    private Dictionary<string, Dictionary<string, string>>? _locale;
    private Dictionary<string, List<string>>? _bank;
    private bool _tried;

    private WizardNameTable() {
    }

    private WizardNameTable(string gameFolder) => _gameFolder = gameFolder;

    /// <summary>
    /// The name a packed name key stands for, or null when it cannot be read.
    /// </summary>
    /// <param name="nameKeys">
    /// The packed key: one byte each for the first, middle and last name, most significant first.
    /// </param>
    /// <param name="isFemale">Which of the game's two first-name tables to read from.</param>
    public string? Resolve(uint nameKeys, bool isFemale) {
        Load();

        if (_bank is null || _locale is null) {
            return null;
        }

        var (first, middle, last) = NameParts(nameKeys);
        var table = isFemale ? "FirstName_HumanFemale" : "FirstName_HumanMale";

        var firstWord = Word(table, first);
        if (firstWord is null) {
            return null;
        }

        // A middle or last name of index zero is one the player did not choose rather than a missing entry,
        // which is what Imlight's own reader says about it as well.
        var middleWord = middle == 0 ? string.Empty : Word("MiddleName_Human", middle);
        var lastWord = last == 0 ? string.Empty : Word("LastName_Human", last);

        if (middleWord is null || lastWord is null) {
            return null;
        }

        var parts = new[] { firstWord, middleWord, lastWord }
            .Where(p => p.Length > 0);

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The three table indices packed into a name key.
    /// </summary>
    /// <remarks>
    /// The top byte is not a name part and is left out, exactly as Imlight's own reader does.
    /// </remarks>
    public static (int First, int Middle, int Last) NameParts(uint nameKeys)
        => ((int) ((nameKeys >> 16) & 0xFF), (int) ((nameKeys >> 8) & 0xFF), (int) (nameKeys & 0xFF));

    /// <summary>One word of a name, out of the bank and then out of the locale file.</summary>
    private string? Word(string tableName, int index) {
        if (_bank is null || _locale is null
            || !_bank.TryGetValue(tableName, out var entries)
            || index < 0 || index >= entries.Count) {
            return null;
        }

        var key = entries[index];
        if (string.IsNullOrEmpty(key)) {
            return string.Empty;
        }

        return _locale.TryGetValue(tableName, out var words) && words.TryGetValue(key, out var word)
            ? word
            : null;
    }

    /// <summary>Reads the two files once, off the archive, on first use.</summary>
    private void Load() {
        if (_tried) {
            return;
        }

        lock (_gate) {
            if (_tried) {
                return;
            }

            _tried = true;

            if (_gameFolder is null) {
                return;
            }

            try {
                var path = RootWadItemService.FindRootWad(_gameFolder);
                if (path is null) {
                    return;
                }

                using var archive = KiWadArchive.Open(path);

                if (archive.FindEntry(BankFileName) is { } bankEntry) {
                    _bank = ReadBank(archive.ReadEntry(bankEntry));
                }

                if (archive.FindEntry(LocaleFileName(DefaultLanguage)) is { } localeEntry) {
                    _locale = ReadLocale(archive.ReadEntry(localeEntry));
                }
            }
            catch (Exception) {
                // A game folder that cannot be read leaves both null, and every name resolves to nothing -
                // which the profile shows as the character's id rather than as a name that is not theirs.
                _bank = null;
                _locale = null;
            }
        }
    }

    /// <summary>
    /// The name bank: one list of keys per table.
    /// </summary>
    /// <remarks>
    /// The file carries a table per locale, and they all point at the same English words. Imlight reads the
    /// German one, so that is tried first - the two agreeing is what makes a name here the same name the game
    /// shows - and the rest are fallbacks for a client that no longer ships it.
    /// </remarks>
    private static Dictionary<string, List<string>>? ReadBank(byte[] xml) {
        var document = new XmlDocument();

        // Loaded as a stream rather than as text: the file begins with a byte-order mark, which is part of
        // the file and not part of its first character.
        using (var stream = new MemoryStream(xml, writable: false)) {
            document.Load(stream);
        }

        var tables = document.SelectNodes("/CharacterNameTable/Table");
        if (tables is null) {
            return null;
        }

        string[] preferred = ["de", "en-US", string.Empty];
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var locale in preferred) {
            foreach (XmlNode table in tables) {
                var name = table.Attributes?["Name"]?.Value;
                var tableLocale = table.Attributes?["Locale"]?.Value ?? string.Empty;

                if (name is null || result.ContainsKey(name)) {
                    continue;
                }

                // An empty preference means "whatever is left", so the last pass takes anything.
                if (locale.Length > 0 && !string.Equals(tableLocale, locale, StringComparison.Ordinal)) {
                    continue;
                }

                var entries = new List<string>();
                foreach (XmlNode entry in table.SelectNodes("CharacterName")!) {
                    entries.Add(entry.InnerText);
                }

                result[name] = entries;
            }
        }

        return result;
    }

    /// <summary>
    /// The locale file: key, delimiter and word, one per line, after a line of header.
    /// </summary>
    /// <remarks>
    /// Sixteen-bit text, not XML, despite the extension - and read as a run of lines exactly as Imlight reads
    /// it, so a key here maps to the same word it does there.
    /// </remarks>
    private static Dictionary<string, Dictionary<string, string>>? ReadLocale(byte[] raw) {
        var text = Encoding.Unicode.GetString(raw);
        var words = new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = new StringReader(text);

        // The header, which names the file rather than a word in it.
        reader.ReadLine();

        while (reader.ReadLine() is { } key) {
            // The delimiter line, which is punctuation rather than content.
            if (reader.ReadLine() is null) {
                break;
            }

            if (reader.ReadLine() is not { } word) {
                break;
            }

            words[key] = word;
        }

        return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal) {
            ["CharacterNames"] = words,
        };
    }

    /// <summary>
    /// Whether a stored gender reads as female.
    /// </summary>
    /// <remarks>
    /// <b>This is the one inferred thing here, and it is said rather than hidden.</b> The client writes its
    /// gender as a flag on the name key - <c>0x80</c> for a girl, <c>0x82</c> for a boy - and Imlight's own
    /// enum for the field is not in the source the launcher can read, so the ordinals it stores are taken to
    /// be the client's usual ones, one for a boy and two for a girl. A value that is none of those is refused
    /// rather than assumed, so an unknown gender shows a character by its id instead of under a first name
    /// that may belong to the other table.
    /// </remarks>
    public static bool? IsFemale(uint? gender) => gender switch {
        1 or 0x82 => false,
        2 or 0x80 => true,
        _ => null,
    };
}
