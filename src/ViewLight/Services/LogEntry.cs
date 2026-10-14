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
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    LogSource Source = LogSource.Launcher) {

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
public static class LogLines {

    /// <summary>How wide the severity column is: wide enough for the longest tag above.</summary>
    public const int LevelColumnWidth = 5;

    /// <summary>One line, with the time, the source and the severity in front of it.</summary>
    public static string Of(LogEntry entry)
        => $"{entry.TimeDisplay}  {entry.SourceTag}  {entry.LevelDisplay.PadRight(LevelColumnWidth)}  {entry.Message}";

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
