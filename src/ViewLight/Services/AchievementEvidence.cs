using System.Globalization;

namespace ViewLight.Services;

/// <summary>
/// What a badge says it was earned for, in words that can be read.
/// </summary>
/// <remarks>
/// <para>
/// The evidence a badge keeps is deliberately the server's own line, because that is what makes a badge a
/// fact rather than an assertion. A server log line, though, is written to be a log line: it opens with a
/// timestamp, a level and often the calling class, and it names things by the server's own ids. Quoting all
/// of that under a badge is what made earned badges unreadable.
/// </para>
/// <para>
/// So the preamble is taken off and nothing else is touched: what is left is the sentence the server wrote,
/// which is the part that says what happened. Nothing is summarised, reworded, shortened or dropped beyond
/// that, so the badge still quotes the server rather than paraphrasing it.
/// </para>
/// </remarks>
public static class AchievementEvidence {

    /// <summary>How long a timestamp carrying a UTC offset is, "yyyy-MM-dd HH:mm:ss.fff zzz".</summary>
    private const int TimestampWithOffsetLength = 30;

    /// <summary>How long the timestamp is on its own, which every Imlight log format begins with.</summary>
    private const int TimestampLength = 23;

    /// <summary>
    /// The log levels, longest-written form first so <c>[DBG]</c> is not read as <c>DBG</c> and left holding
    /// a bracket.
    /// </summary>
    private static readonly string[] LevelTags = [
        "[TRC]", "[VRB]", "[DBG]", "[INF]", "[WRN]", "[ERR]", "[FTL]",
        "TRC", "VRB", "DBG", "INF", "WRN", "ERR", "FTL",
    ];

    /// <summary>
    /// The half-sentences an earlier launcher left in profiles, one of which was written onto every badge
    /// it swept rather than onto the badge it was about.
    /// </summary>
    private static readonly string[] SweepSummaries = [
        "The player database:",
        "From your player database:",
        "The server's own creature list:",
    ];

    /// <summary>
    /// Whether a stored line is one of the launcher's own sweep summaries rather than evidence about the
    /// badge it is stored on.
    /// </summary>
    /// <remarks>
    /// Badges earned in one pass used to be given the same sentence - a summary of the whole account - so a
    /// badge for finishing a quest said exactly what one for a hundred hours of play said. Profiles written
    /// then still hold those lines. Showing one under a badge would be presenting it as evidence about that
    /// badge, which it never was, so it is recognised and treated as no evidence at all - and the badge
    /// says what it takes instead, which is at least true of it.
    /// </remarks>
    public static bool IsSweepSummary(string? evidence)
        => evidence is not null
           && SweepSummaries.Any(prefix => evidence.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// The server's sentence, without the log preamble in front of it.
    /// </summary>
    /// <returns>
    /// The sentence, or an empty string when there is no evidence to show. Empty is a real answer: a badge
    /// with nothing to quote says its requirement instead rather than a placeholder.
    /// </returns>
    public static string ForDisplay(string? evidence) {
        if (string.IsNullOrWhiteSpace(evidence)) {
            return string.Empty;
        }

        var text = evidence.Trim();

        // Trimmed between the steps, not only at the end: taking the timestamp off a line leaves the space
        // that separated it from the level, and the level is matched from the start of what is left.
        text = DropTimestamp(text).TrimStart();
        text = DropLevel(text).TrimStart();
        text = DropCaller(text).TrimStart();

        return text;
    }

    /// <summary>Takes off the "<c>2026-10-13 16:33:36.083 -04:00 </c>" a line starts with.</summary>
    private static string DropTimestamp(string text) {
        if (text.Length < TimestampLength
            || !DateTime.TryParseExact(text[..TimestampLength], "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) {
            return text;
        }

        var withOffset = text.Length >= TimestampWithOffsetLength
                         && text[TimestampLength] == ' '
                         && DateTimeOffset.TryParseExact(text[..TimestampWithOffsetLength],
                             "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal, out _);

        return withOffset ? text[TimestampWithOffsetLength..] : text[TimestampLength..];
    }

    /// <summary>Takes off the "<c>[DBG] </c>" - or the bare "<c>DBG </c>" - that the level is written as.</summary>
    private static string DropLevel(string text) {
        foreach (var tag in LevelTags) {
            if (!text.StartsWith(tag, StringComparison.Ordinal)) {
                continue;
            }

            // Only when the level is a tag of its own: "WRN" at the start of a word is that word.
            var rest = text[tag.Length..];
            if (rest.Length > 0 && rest[0] == ' ') {
                return rest.TrimStart();
            }
        }

        return text;
    }

    /// <summary>
    /// Takes off the "<c>WizardCollection.UpdateCharacterLevel@L279 : </c>" that some log formats put
    /// between the level and the message.
    /// </summary>
    /// <remarks>
    /// Recognised by its shape rather than by a list of names, because the caller is whichever method
    /// happened to log the line and there are hundreds of them. A colon in a sentence that has spaces in
    /// front of it - "admin Uses command: account create" - is the message's own punctuation and stays.
    /// </remarks>
    private static string DropCaller(string text) {
        var colon = text.IndexOf(':');
        if (colon < 0) {
            return text;
        }

        var before = text[..colon];

        return before.Contains('@') && !before.Contains(' ')
            ? text[(colon + 1)..].TrimStart()
            : text;
    }
}
