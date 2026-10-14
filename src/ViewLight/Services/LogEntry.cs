namespace ViewLight.Services;

public enum LogLevel {
    Info,
    Success,
    Warning,
    Error,

    /// <summary>A line piped straight out of the server process.</summary>
    Server,
}

/// <summary>A single line in the launcher's log pane.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message) {

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public string LevelDisplay => Level switch {
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Server => "SRV",
        _ => "INFO",
    };

    public override string ToString() => $"{TimeDisplay} [{LevelDisplay}] {Message}";
}
