namespace ViewLight.Services;

public enum LogLevel {
    Info,
    Success,
    Warning,
    Error,

    /// <summary>A line piped straight out of a process.</summary>
    Server,
}

/// <summary>Which of the three things the Log is made of a line came from.</summary>
/// <remarks>
/// Set where the line is written rather than worked out from the message. The launcher's own lines are
/// launcher lines, and everything a runner reports - the process's own output and the runner's own
/// narration about it - carries the process it belongs to: somebody quietening the server down wants the
/// line that says the server stopped as much as they want the line the server printed.
/// </remarks>
public enum LogSource {
    Launcher,
    Server,
    Patcher,
}

/// <summary>A single line in the launcher's log.</summary>
/// <param name="Derived">
/// Whether the launcher wrote this line <i>about</i> the program it is filed under rather than the program
/// saying it itself. The two are worth telling apart even though they belong in the same box: the patch
/// server's own developers can be handed the patch server's lines, and the launcher's commentary on them is
/// the launcher's rather than theirs. See <see cref="LogDocument.SetDerivedVisible"/>.
/// </param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    LogSource Source = LogSource.Launcher,
    bool Derived = false) {

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    /// <summary>The three letters that say who said this line.</summary>
    public string SourceTag => Source switch {
        LogSource.Server => "SRV",
        LogSource.Patcher => "PAT",
        _ => "APP",
    };

    /// <summary>
    /// What the severity reads as. Raw output from a process is neither good nor bad news, so it is tagged
    /// as what it is; which process it came from is the column beside it.
    /// </summary>
    public string LevelDisplay => Level switch {
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Server => "OUT",
        _ => "INFO",
    };

    /// <summary>
    /// The line as the Log draws it: fixed columns, so a reading or a piece of ASCII art a process printed
    /// keeps the shape it was written in.
    /// </summary>
    public string Line => LogLines.Of(this);

    public override string ToString() => Line;
}

/// <summary>The one place a log line is written out, so the columns line up wherever they are written.</summary>
public static partial class LogLines {

    /// <summary>How wide the severity column is: wide enough for the longest tag above.</summary>
    public const int LevelColumnWidth = 5;

    /// <summary>One line, with the time, the source and the severity in front of it.</summary>
    public static string Of(LogEntry entry)
        => $"{entry.TimeDisplay}  {entry.SourceTag}  {entry.LevelDisplay.PadRight(LevelColumnWidth)}  {entry.Message}";

    /// <summary>
    /// A message with the clock taken out of it, so two readings of the same thing are the same line again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Processes timestamp their own lines - <c>2026-10-15 23:26:55.031 WRN ... : No handler found for
    /// requirement type: ReqMagicLevel</c> - and a warning repeated for a quarter of an hour differs only in
    /// that. Folding on the whole message meant the one warning a reader most wants summarised was the one
    /// that never folded: forty lines of the same sentence, each with a later millisecond in front of it.
    /// </para>
    /// <para>
    /// What is replaced is a date, a time, or a date and a time together - and a time has to look like one,
    /// with hours under 24 and minutes and seconds under 60, so an ordinary number that happens to contain
    /// colons is left alone. Nothing is lost by folding on this: the lines themselves are unchanged, they
    /// are one click away, and the count says how many of them there were.
    /// </para>
    /// </remarks>
    public static string Essence(string message) {
        if (string.IsNullOrEmpty(message)) {
            return message ?? string.Empty;
        }

        // The date first, so the time inside it is not masked twice over and left half-masked.
        return TimeInMessage().Replace(DateInMessage().Replace(message, Mask), Mask);
    }

    /// <summary>What the clock is replaced with, so the masked line still reads as a sentence.</summary>
    private const string Mask = "<time>";

    /// <summary>A date, with the time beside it when it has one.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\d{4}-\d{2}-\d{2}(?:[ T]\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?)?")]
    private static partial System.Text.RegularExpressions.Regex DateInMessage();

    /// <summary>A time of day, with a fractional part when it has one. The ranges are what keep this from
    /// matching something that merely looks like a time.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:[.,]\d+)?")]
    private static partial System.Text.RegularExpressions.Regex TimeInMessage();

    /// <summary>
    /// The line that stands in for a run of the same line arriving over and over. The severity column is
    /// where the count goes, so the run reads as one line of the same shape as the ones around it.
    /// </summary>
    /// <param name="first">The first line of the run, which is the time the run started.</param>
    /// <param name="count">How many lines the run is.</param>
    /// <param name="expanded">Whether the lines behind it are currently being shown.</param>
    public static string Folded(LogEntry first, int count, bool expanded)
        => $"{first.TimeDisplay}  {first.SourceTag}  "
           + $"{(expanded ? "-" : "+")}{count}".PadRight(LevelColumnWidth)
           + $"  {first.Message}";
}
