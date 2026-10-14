using System.Text;

namespace ViewLight.Services;

public enum IniLineKind {
    Blank,
    Comment,
    Section,
    KeyValue,
    Raw,
}

/// <summary>
/// A single physical line of an INI file, remembering enough structure that untouched lines can be
/// written back byte-for-byte and edited lines keep their original spacing, casing and inline comment.
/// </summary>
public sealed class IniLine {

    public IniLineKind Kind { get; set; }

    /// <summary>The line exactly as it appeared (without the line terminator).</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>Section this line lives in (empty before the first section header).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Key text for <see cref="IniLineKind.KeyValue"/>, whitespace trimmed.</summary>
    public string? Key { get; set; }

    /// <summary>Value text, right-trimmed but otherwise verbatim (may be empty).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Anything between the key and the value, e.g. <c>" = "</c>.</summary>
    public string Separator { get; set; } = " = ";

    /// <summary>Indentation before the key or section header.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Trailing <c>; comment</c> on a key/value line, if the file had one.</summary>
    public string InlineComment { get; set; } = string.Empty;

    public void RebuildRaw() {
        Raw = Kind switch {
            IniLineKind.KeyValue => $"{Prefix}{Key}{Separator}{Value}{InlineComment}",
            _ => Raw,
        };
    }
}

/// <summary>
/// A comment- and formatting-preserving INI editor.
/// </summary>
/// <remarks>
/// Imlight's <c>Config/Imlight.ini</c> is hand-written documentation as much as it is configuration:
/// nearly every key carries an explanatory comment above it. A naive "parse into a dictionary and
/// rewrite" would destroy all of that, and would silently drop any key a future Imlight release adds
/// that this launcher does not know about. So this type keeps the file as an ordered list of lines and
/// only ever rewrites the value of the specific keys it is asked to change.
/// </remarks>
public sealed class IniFile {

    private readonly List<IniLine> _lines = new();
    private string _eol = "\r\n";
    private bool _endsWithNewline = true;
    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Number of key/value pairs we recognised (useful for diagnostics).</summary>
    public int KeyCount => _lines.Count(l => l.Kind == IniLineKind.KeyValue);

    /// <summary>Distinct section names in file order.</summary>
    public IEnumerable<string> Sections =>
        _lines.Where(l => l.Kind == IniLineKind.Section).Select(l => l.Section).Distinct();

    public static IniFile Parse(string text) {
        var ini = new IniFile();

        // Preserve the dominant line ending so we do not rewrite the whole file's style.
        var firstNewline = text.IndexOf('\n');
        ini._eol = firstNewline > 0 && text[firstNewline - 1] == '\r' ? "\r\n" : "\n";
        ini._endsWithNewline = text.EndsWith('\n');

        var chunks = text.Split('\n');
        var limit = ini._endsWithNewline ? chunks.Length - 1 : chunks.Length;

        var currentSection = string.Empty;
        for (var i = 0; i < limit; i++) {
            var raw = chunks[i];
            if (raw.EndsWith('\r')) {
                raw = raw[..^1];
            }

            var line = ParseLine(raw, currentSection, i);
            if (line.Kind == IniLineKind.Section) {
                currentSection = line.Section;
            }

            ini._lines.Add(line);
        }

        return ini;
    }

    private static IniLine ParseLine(string raw, string section, int index) {
        var trimmed = raw.TrimStart();
        var prefix = raw[..(raw.Length - trimmed.Length)];

        if (raw.Length == 0) {
            return new IniLine { Kind = IniLineKind.Blank, Raw = raw, Section = section };
        }

        if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) {
            return new IniLine { Kind = IniLineKind.Comment, Raw = raw, Section = section };
        }

        if (trimmed.StartsWith('[')) {
            var close = trimmed.IndexOf(']');
            if (close > 0) {
                var name = trimmed[1..close].Trim();
                return new IniLine {
                    Kind = IniLineKind.Section,
                    Raw = raw,
                    Section = name,
                    Prefix = prefix,
                };
            }
        }

        var eq = trimmed.IndexOf('=');
        if (eq > 0) {
            var keyPart = trimmed[..eq];
            var rest = trimmed[(eq + 1)..];

            var keyText = keyPart.TrimEnd();
            var leading = rest.Length - rest.TrimStart().Length;
            var separator = keyPart[keyText.Length..] + "=" + rest[..leading];
            var valueAndComment = rest.TrimStart();

            var (value, inline) = SplitInlineComment(valueAndComment);

            return new IniLine {
                Kind = IniLineKind.KeyValue,
                Raw = raw,
                Section = section,
                Prefix = prefix,
                Key = keyText,
                Separator = separator,
                Value = value,
                InlineComment = inline,
            };
        }

        return new IniLine { Kind = IniLineKind.Raw, Raw = raw, Section = section };
    }

    /// <summary>
    /// Splits a trailing <c>; comment</c> off a value. Only treats <c>;</c> as a comment when it is
    /// preceded by whitespace, so values that legitimately contain a semicolon survive.
    /// </summary>
    private static (string Value, string InlineComment) SplitInlineComment(string valueAndComment) {
        for (var i = 1; i < valueAndComment.Length; i++) {
            if (valueAndComment[i] is ';' or '#' && char.IsWhiteSpace(valueAndComment[i - 1])) {
                // Walk back over the separating whitespace so re-writing keeps it verbatim.
                var start = i;
                while (start > 0 && char.IsWhiteSpace(valueAndComment[start - 1])) {
                    start--;
                }

                return (valueAndComment[..start].TrimEnd(), valueAndComment[start..]);
            }
        }

        return (valueAndComment.TrimEnd(), string.Empty);
    }

    /// <summary>Reads an INI file, falling back to Latin-1 for files that are not valid UTF-8.</summary>
    public static IniFile Load(string path) {
        var bytes = File.ReadAllBytes(path);
        var ini = Parse(DecodeBytes(bytes));
        ini._encoding = DetectEncoding(bytes);
        return ini;
    }

    /// <summary>
    /// Decodes bytes as UTF-8 when possible, otherwise as Latin-1. Latin-1 decoding is byte
    /// preserving, so a file with stray non-UTF-8 bytes still round-trips exactly.
    /// </summary>
    private static string DecodeBytes(byte[] bytes) {
        try {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException) {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static Encoding DetectEncoding(byte[] bytes) {
        try {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException) {
            return Encoding.Latin1;
        }
    }

    public string ToText() {
        var sb = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++) {
            sb.Append(_lines[i].Raw);
            if (i < _lines.Count - 1 || _endsWithNewline) {
                sb.Append(_eol);
            }
        }

        return sb.ToString();
    }

    public void Save(string path) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, _encoding.GetBytes(ToText()));
    }

    /// <summary>Gets a value, or <c>null</c> when the section or key is absent.</summary>
    public string? Get(string section, string key) {
        var line = Find(section, key);
        return line?.Value;
    }

    public bool TryGet(string section, string key, out string value) {
        var line = Find(section, key);
        value = line?.Value ?? string.Empty;
        return line is not null;
    }

    public string GetOrDefault(string section, string key, string fallback)
        => Find(section, key)?.Value ?? fallback;

    private IniLine? Find(string section, string key)
        => _lines.FirstOrDefault(l =>
            l.Kind == IniLineKind.KeyValue
            && l.Key is not null
            && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sets a value, appending the key (and, if needed, the section header) when it does not exist.
    /// Existing line formatting, casing and inline comments are preserved.
    /// </summary>
    public void Set(string section, string key, string value) {
        var line = Find(section, key);
        if (line is not null) {
            line.Value = value;
            line.RebuildRaw();
            return;
        }

        AppendKey(section, key, value);
    }

    private void AppendKey(string section, string key, string value) {
        var newLine = new IniLine {
            Kind = IniLineKind.KeyValue,
            Section = section,
            Key = key,
            Value = value,
            Separator = " = ",
        };
        newLine.RebuildRaw();

        var insertAt = _lines.FindLastIndex(l =>
            string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));
        if (insertAt < 0) {
            // Section missing entirely: create it at the end of the file.
            if (_lines.Count > 0 && _lines[^1].Kind != IniLineKind.Blank) {
                _lines.Add(new IniLine { Kind = IniLineKind.Blank, Raw = string.Empty });
            }

            var header = new IniLine {
                Kind = IniLineKind.Section,
                Section = section,
                Raw = $"[{section}]",
            };
            _lines.Add(header);
            _lines.Add(newLine);
            return;
        }

        // Walk past anything else that belongs to the section so we do not split a block apart.
        var end = insertAt;
        for (var i = insertAt + 1; i < _lines.Count; i++) {
            if (string.Equals(_lines[i].Section, section, StringComparison.OrdinalIgnoreCase)) {
                end = i;
            }
        }

        _lines.Insert(end + 1, newLine);
    }

    /// <summary>
    /// Replaces the block of full-line comments directly above a key. Used so launcher-written values
    /// can carry their own documentation instead of stale comments from a previous Imlight release.
    /// </summary>
    public void SetCommentAbove(string section, string key, IEnumerable<string> commentLines) {
        var target = _lines.FindIndex(l =>
            l.Kind == IniLineKind.KeyValue
            && l.Key is not null
            && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));
        if (target < 0) {
            return;
        }

        var start = target;
        while (start > 0 && _lines[start - 1].Kind == IniLineKind.Comment
                          && string.Equals(_lines[start - 1].Section, section, StringComparison.OrdinalIgnoreCase)) {
            start--;
        }

        var replacement = commentLines
            .Select(text => new IniLine {
                Kind = IniLineKind.Comment,
                Section = section,
                Raw = string.IsNullOrWhiteSpace(text) ? ";" : $"; {text.TrimStart(';', ' ')}",
            })
            .ToList();

        _lines.RemoveRange(start, target - start);
        _lines.InsertRange(start, replacement);
    }

    /// <summary>
    /// Copies every key/value from <paramref name="donor"/> into this file. Keys that exist in both
    /// keep the donor's value; keys only present in the donor are appended to their section.
    /// </summary>
    /// <returns>The number of settings that were carried over.</returns>
    public int MergeFrom(IniFile donor) {
        var applied = 0;
        foreach (var line in donor._lines.Where(l => l.Kind == IniLineKind.KeyValue && l.Key is not null)) {
            Set(line.Section, line.Key!, line.Value);
            applied++;
        }

        return applied;
    }

    /// <summary>Snapshot of every key/value, keyed by section then key.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
        => _lines
            .Where(l => l.Kind == IniLineKind.KeyValue && l.Key is not null)
            .GroupBy(l => $"{l.Section}\u001f{l.Key}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key/value pair inside a section, in file order.</summary>
    public IEnumerable<(string Key, string Value)> GetSection(string section)
        => _lines
            .Where(l => l.Kind == IniLineKind.KeyValue
                        && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.Key!, l.Value));
}
