using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The Log is a document of blocks rather than a row per line, and every rule that decides what is in it -
/// what folds, what the filters take out, what a full Log drops - is decided here rather than in the view.
/// </summary>
public class LogDocumentTests {

    private static readonly DateTimeOffset Noon = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static LogEntry Line(
        string message,
        LogSource source = LogSource.Launcher,
        LogLevel level = LogLevel.Info,
        int second = 0,
        bool derived = false)
        => new(Noon.AddSeconds(second), level, message, source, derived);

    private static LogEntry ServerLine(string message, int second = 0)
        => Line(message, LogSource.Server, LogLevel.Server, second);

    /// <summary>
    /// What the view puts on screen: a folded run is its header line, and everything else is drawn.
    /// </summary>
    private static IReadOnlyList<string> Screen(LogDocument log) {
        var lines = new List<string>();

        foreach (var block in log.Blocks) {
            if (block.FoldLine is { } fold) {
                lines.Add(fold);
            }

            if (block.HasBody) {
                lines.AddRange(block.Body.Split('\n'));
            }
        }

        return lines;
    }

    // ------------------------------------------------------------------ folding a run of the same line

    [Fact]
    public void FoldsTheSameLineArrivingAgainIntoOneCountedLine() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));
        log.Append(ServerLine("ping timed out", 2));

        var fold = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.Equal(3, fold.Count);
        Assert.Equal(LogSource.Server, fold.Source);
        Assert.True(fold.IsFolded);

        // One line on screen, with the count where the severity tag would be, so the column still reads.
        Assert.Equal(["12:00:00  SRV  +3     ping timed out"], Screen(log));
        Assert.Equal(1, log.FoldedRunCount);
        Assert.Equal(3, log.FoldedLineCount);
        Assert.Equal(1, log.DrawnLineCount);
    }

    [Fact]
    public void KeepsCountingWhileTheSameLineGoesOn() {
        var log = new LogDocument();

        for (var i = 0; i < 120; i++) {
            log.Append(ServerLine("ping timed out", i));
        }

        var fold = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.Equal(120, fold.Count);
        Assert.Equal("12:00:00  SRV  +120   ping timed out", fold.FoldLine);
    }

    [Fact]
    public void ARunLeftOpenKeepsTakingTheLinesThatArrive() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));
        log.Toggle(log.Blocks[0]);

        log.Append(ServerLine("ping timed out", 2));

        var repeat = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.True(repeat.Expanded);
        Assert.Equal(3, repeat.Count);

        // The header stays above the lines it holds, so the run can be folded back up from the same place.
        Assert.Equal(4, Screen(log).Count);
    }

    [Fact]
    public void OnlyFoldsTheSameLineArrivingInARow() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("the zone loaded", 1));
        log.Append(ServerLine("ping timed out", 2));

        // Three lines, none of them next to a line they match: the run is what folds, and there is no run.
        var block = Assert.IsType<LogTextBlock>(Assert.Single(log.Blocks));

        Assert.Equal(3, block.LineCount);
        Assert.Equal(0, log.FoldedRunCount);
        Assert.Equal(3, log.DrawnLineCount);
    }

    [Fact]
    public void OpensAFoldedRunForReadingAndFoldsItBackUp() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));
        log.Append(ServerLine("ping timed out", 2));

        log.Toggle(log.Blocks[0]);

        var opened = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        // Every line is there, with the time it arrived at, exactly as the terminal would have drawn them,
        // under the same header with the count now reading as the way back up.
        Assert.Equal(
            [
                "12:00:00  SRV  -3     ping timed out",
                "12:00:00  SRV  OUT    ping timed out",
                "12:00:01  SRV  OUT    ping timed out",
                "12:00:02  SRV  OUT    ping timed out",
            ],
            Screen(log));

        log.Toggle(log.Blocks[0]);

        var folded = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.True(folded.IsFolded);
        Assert.False(folded.HasBody);
        Assert.Equal(["12:00:00  SRV  +3     ping timed out"], Screen(log));
    }

    [Fact]
    public void PressingSomethingThatIsNotThereAnyMoreDoesNothing() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));

        var fold = log.Blocks[0];
        var plain = Line("from the launcher");

        log.Append(plain);
        var text = log.Blocks[^1];

        log.Clear();
        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));

        var reopened = log.Blocks[0];

        log.Toggle(fold);       // The one that was on screen before the Log was emptied.
        log.Toggle(text);       // A run of ordinary lines, which has nothing to open.
        log.Toggle(reopened);   // The one that is on screen now.

        var repeat = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.True(repeat.Expanded);
    }

    // ------------------------------------------------------------------ the filters

    [Fact]
    public void ShowsTheThreeSourcesTheBoxesAskFor() {
        var log = new LogDocument();

        log.Append(Line("ViewLight ready."));
        log.Append(ServerLine("the zone loaded", 1));
        log.Append(Line("the patch server has it", LogSource.Patcher, LogLevel.Info, 2));

        var everything = Screen(log);

        Assert.Equal(3, everything.Count);

        log.SetVisible(LogSource.Server, false);

        Assert.False(log.IsVisible(LogSource.Server));
        Assert.Equal(1, log.HiddenLineCount);
        Assert.DoesNotContain(Screen(log), line => line.Contains("the zone loaded"));
        Assert.Equal(2, log.DrawnLineCount);
        Assert.Contains("1 hidden by the filters", log.Summary);

        // Ticking it again brings back what the Log is still holding, in the place it was in.
        log.SetVisible(LogSource.Server, true);

        Assert.Equal(everything, Screen(log));
        Assert.Equal(0, log.HiddenLineCount);

        // Each box is its own: hiding two of the three leaves the third.
        log.SetVisible(LogSource.Launcher, false);
        log.SetVisible(LogSource.Patcher, false);

        Assert.Equal(["12:00:01  SRV  OUT    the zone loaded"], Screen(log));
        Assert.Equal(2, log.HiddenLineCount);
    }

    [Fact]
    public void ARunLeftOpenStaysOpenWhenTheBoxesChange() {
        var log = new LogDocument();

        log.Append(ServerLine("ping timed out", 0));
        log.Append(ServerLine("ping timed out", 1));
        log.Append(ServerLine("ping timed out", 2));
        log.Toggle(log.Blocks[0]);

        // Changing a box rebuilds the whole Log, which is exactly when a run that was opened for copying
        // would fold itself back up under somebody.
        log.SetVisible(LogSource.Launcher, false);
        log.SetVisible(LogSource.Launcher, true);

        var repeat = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.True(repeat.Expanded);
        Assert.Equal(4, Screen(log).Count);
    }

    [Fact]
    public void TheWholeLogIsTheSameHoweverItWasBuilt() {
        var log = new LogDocument();

        foreach (var entry in Filler(512)) {
            log.Append(entry);
        }

        var arrived = Screen(log);

        // Built again from the lines it is holding rather than as they arrived.
        log.SetVisible(LogSource.Server, false);
        log.SetVisible(LogSource.Server, true);

        Assert.Equal(arrived, Screen(log));
    }

    [Fact]
    public void HiddenLinesAreStillCountedAgainstTheCap() {
        var log = new LogDocument();

        for (var i = 0; i < LogDocument.MaxLines + 10; i++) {
            log.Append(ServerLine($"line {i}", i));
        }

        log.SetVisible(LogSource.Server, false);

        // Nothing is drawn, and the Log is still holding - not growing past - what it always was.
        Assert.Empty(log.Blocks);
        Assert.Equal(LogDocument.MaxLines, log.LineCount);
        Assert.Equal(LogDocument.MaxLines, log.HiddenLineCount);
    }

    // ------------------------------------------------------------------ one block at a time

    [Fact]
    public void NoBlockHoldsMoreThanTwoHundredLines() {
        var log = new LogDocument();

        foreach (var entry in Filler(500)) {
            log.Append(entry);
        }

        // Every block is one selectable region and one piece of text to lay out, so they are kept short -
        // which is what stops the whole Log being laid out again every time a line arrives.
        Assert.All(log.Blocks, block => Assert.True(
            block.LineCount <= LogDocument.MaxBlockLines,
            $"A block of {block.LineCount} lines was drawn."));

        Assert.Equal(3, log.Blocks.Count);
        Assert.Equal(500, log.DrawnLineCount);
    }

    [Fact]
    public void FiveThousandLinesIsWhereTheOldestStartGoing() {
        var log = new LogDocument();

        foreach (var entry in Filler(LogDocument.MaxLines + 120)) {
            log.Append(entry);
        }

        Assert.Equal(LogDocument.MaxLines, log.LineCount);
        Assert.Equal(LogDocument.MaxLines, log.DrawnLineCount);

        var drawn = Screen(log);

        Assert.Equal(LogDocument.MaxLines, drawn.Count);
        Assert.Contains("line 120", drawn[0]);
        Assert.Contains($"line {LogDocument.MaxLines + 119}", drawn[^1]);
    }

    [Fact]
    public void ADroppedRunCountsDownInsteadOfDisappearing() {
        var log = new LogDocument();

        // A run of the same line long enough to still be a run after the oldest of it has been dropped.
        for (var i = 0; i < LogDocument.MaxLines + 3; i++) {
            log.Append(ServerLine("ping timed out", i));
        }

        var fold = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.Equal(LogDocument.MaxLines, fold.Count);
        Assert.Equal(LogDocument.MaxLines, log.FoldedLineCount);
        Assert.Equal(1, log.DrawnLineCount);
    }

    [Fact]
    public void ARebuiltLogFoldsTheSameRunsTheLinesArrivedIn() {
        var log = new LogDocument();
        var second = 0;

        for (var i = 0; i < 40; i++) {
            log.Append(ServerLine("waiting for the zone", second++));
            log.Append(ServerLine("waiting for the zone", second++));
            log.Append(ServerLine("waiting for the zone", second++));
            log.Append(ServerLine($"zone {i} loaded", second++));
        }

        var arrived = Screen(log);

        log.SetVisible(LogSource.Patcher, false);
        log.SetVisible(LogSource.Patcher, true);

        Assert.Equal(arrived, Screen(log));
        Assert.Equal(40, log.FoldedRunCount);
        Assert.Equal(80, log.DrawnLineCount);
    }

    // ------------------------------------------------------------------ what the terminal draws

    [Fact]
    public void KeepsTheShapeOfWhatAProcessPrinted() {
        var log = new LogDocument();
        var art = "     ___      ____          ";

        log.Append(Line(art, LogSource.Server, LogLevel.Server));

        // Spaces matter to a picture, so nothing is trimmed and nothing is re-wrapped: what the process
        // wrote is what is drawn.
        Assert.Equal($"12:00:00  SRV  OUT    {art}", Assert.Single(Screen(log)));
    }

    [Fact]
    public void PutsEveryMessageInTheSameColumn() {
        var entries = new[] {
            Line("ViewLight ready."),
            Line("Saved settings.", level: LogLevel.Success),
            Line("No server here yet.", level: LogLevel.Warning),
            Line("Install failed.", level: LogLevel.Error),
            ServerLine("the zone loaded"),
        };

        var columns = entries.Select(entry => entry.Line.IndexOf(entry.Message, StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.Equal([22], columns);
        Assert.Equal("12:00:00  APP  ERROR  Install failed.", entries[3].Line);

        // Raw output from a process is tagged as what it is, and the column beside it says whose it is.
        Assert.Equal("12:00:00  SRV  OUT    the zone loaded", entries[4].Line);

        // The folded run's count goes in the severity column, so its message lines up with the others.
        var log = new LogDocument();
        log.Append(ServerLine("the zone loaded", 0));
        log.Append(ServerLine("the zone loaded", 1));

        Assert.Equal(22, log.Blocks[0].FoldLine!.IndexOf("the zone loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void SaysWhatIsOnTheLog() {
        Assert.Equal("1 line", LogDocument.Describe(1, 0, 0, 0));
        Assert.Equal("1,204 lines", LogDocument.Describe(1204, 0, 0, 0));
        Assert.Equal("12 lines · 9 folded into 1 run", LogDocument.Describe(12, 9, 1, 0));
        Assert.Equal("12 lines · 9 folded into 2 runs", LogDocument.Describe(12, 9, 2, 0));
        Assert.Equal("12 lines · 3 hidden by the filters", LogDocument.Describe(12, 0, 0, 3));
        Assert.Equal("12 lines · 9 folded into 2 runs · 3 hidden by the filters",
            LogDocument.Describe(12, 9, 2, 3));
    }

    [Fact]
    public void AnEmptiedLogHasNothingLeftInIt() {
        var log = new LogDocument();

        log.Append(Line("ViewLight ready."));
        log.Append(ServerLine("ping timed out", 1));
        log.Append(ServerLine("ping timed out", 2));
        log.Toggle(log.Blocks[1]);

        log.Clear();

        Assert.Empty(log.Blocks);
        Assert.Equal(0, log.LineCount);
        Assert.Equal(0, log.HiddenLineCount);
        Assert.Equal("0 lines", log.Summary);
    }

    [Fact]
    public void FoldsAWarningTheServerRepeatedWithABetterClockInFrontOfIt() {
        var log = new LogDocument();

        // The shape a real server writes: its own timestamp inside the message, a few milliseconds apart.
        // The words are the same sentence every time, and that is what folding is for.
        log.Append(ServerLine("2026-10-15 23:26:55.031 WRN No handler found for requirement type: ReqMagicLevel"));
        log.Append(ServerLine("2026-10-15 23:26:55.033 WRN No handler found for requirement type: ReqMagicLevel", 1));
        log.Append(ServerLine("2026-10-15 23:26:55.036 WRN No handler found for requirement type: ReqMagicLevel", 2));

        var fold = Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks));

        Assert.Equal(3, fold.Count);
        Assert.True(fold.IsFolded);

        // Opening it shows all three exactly as the server wrote them: nothing was rewritten to fold them.
        log.Toggle(fold);

        Assert.False(Assert.IsType<LogRepeatBlock>(Assert.Single(log.Blocks)).IsFolded);

        // The header line, then the three the server wrote, each with its own millisecond - which is the
        // point: nothing was rewritten to fold them, they were only counted.
        Assert.Contains("23:26:55.033", Screen(log)[2]);
    }

    [Fact]
    public void LinesThatSaySomethingDifferentAreStillTwoLines() {
        var log = new LogDocument();

        // A clock is not the only way two lines differ, and the ones that mean different things stay apart:
        // the requirement each names is what makes them different events rather than one event repeated.
        log.Append(ServerLine("2026-10-15 23:26:55.031 WRN No handler found for requirement type: ReqMagicLevel"));
        log.Append(ServerLine("2026-10-15 23:26:56.031 WRN No handler found for requirement type: ReqHealth"));
        log.Append(ServerLine("2026-10-15 23:26:57.031 WRN No handler found for requirement type: ReqMana"));

        Assert.Equal(3, log.LineCount);
        Assert.Equal(3, log.DrawnLineCount);
        Assert.Equal(0, log.FoldedRunCount);

        // One of them arriving again, and that pair is a run - which is what the run of a real server's
        // warning is made of, rather than the three different sentences above.
        log.Append(ServerLine("2026-10-15 23:26:58.031 WRN No handler found for requirement type: ReqMana", 4));

        Assert.Equal(4, log.LineCount);
        Assert.Equal(1, log.FoldedRunCount);
        Assert.Equal(3, log.DrawnLineCount);
    }

    [Fact]
    public void TheClockIsTakenOutOfADateAndATimeAndNothingElse() {
        // Dates and times of day, and the fractional parts they carry.
        Assert.Equal("<time> WRN doing something",
            LogLines.Essence("2026-10-15 23:26:55.031 WRN doing something"));
        Assert.Equal("<time> [DBG] entered", LogLines.Essence("23:26:55 [DBG] entered"));
        Assert.Equal("at <time>", LogLines.Essence("at 2026-10-15T23:26:55"));

        // Numbers that merely look like a clock are left where they are: a time of day is hours under 24 and
        // minutes and seconds under 60, so a plain count of colons is not something to fold on.
        Assert.Equal("[INF] zone 99:88:77 loaded", LogLines.Essence("[INF] zone 99:88:77 loaded"));
        Assert.Equal("[INF] 12:99:00 done", LogLines.Essence("[INF] 12:99:00 done"));

        // A date is a date by its shape, and what a machine calls its own timestamps is not this rule's to
        // judge: a year-month-day is taken out whatever the three numbers are.
        Assert.Equal("[INF] <time> shipped", LogLines.Essence("[INF] 2026-13-45 shipped"));
        Assert.Equal(string.Empty, LogLines.Essence(string.Empty));
    }

    // ------------------------------------------------------------------ the launcher's own notes

    [Fact]
    public void TheLaunchersNotesOnAProgramAreFiledUnderItAndCanBeHiddenOnTheirOwn() {
        var log = new LogDocument();

        // The patch server's own line, and the launcher's note about it: one box for the patch server,
        // because that is what somebody reading about the patch server wants together.
        log.Append(Line("the fetch moved on to Equipment-WorldData.wad", LogSource.Patcher, LogLevel.Info, 0, derived: true));
        log.Append(ServerLine("a line the server printed", 1));

        Assert.Equal(2, log.DrawnLineCount);
        Assert.Equal(LogSource.Patcher, log.Blocks[0].Source);

        // Hidden, the launcher's note goes and the program's own lines stay.
        log.SetDerivedVisible(false);

        Assert.False(log.DerivedLinesAreVisible);
        Assert.Equal(1, log.HiddenLineCount);
        Assert.DoesNotContain(Screen(log), line => line.Contains("Equipment-WorldData"));
        Assert.Contains(Screen(log), line => line.Contains("a line the server printed"));
        Assert.Equal(1, log.DrawnLineCount);
        Assert.Contains("1 hidden by the filters", log.Summary);

        // And having both filters on is the two of them together.
        log.SetVisible(LogSource.Server, false);

        Assert.Empty(log.Blocks);
        Assert.Equal(2, log.HiddenLineCount);

        // Ticking it again brings back what the Log is still holding.
        log.SetDerivedVisible(true);
        log.SetVisible(LogSource.Server, true);

        Assert.Equal(2, log.DrawnLineCount);
        Assert.Equal(0, log.HiddenLineCount);
    }

    // ------------------------------------------------------------------ the boxes on the view

    [Fact]
    public void TheBoxesOnTheLogPageAreTheFilters() {
        using var workspace = new TempWorkspace();
        var launcher = new MainWindowViewModel(
            new LauncherSettingsStore(workspace.Path("launcher.json")), fetchArt: false);

        launcher.ClearLogsCommand.Execute(null);

        launcher.AppendLog(LogLevel.Info, "from the launcher");
        launcher.AppendLog(new LogEntry(Noon, LogLevel.Server, "from the server", LogSource.Server));

        Assert.Equal(2, launcher.Log.LineCount);
        Assert.Equal("2 lines", launcher.LogSummary);

        // Unticking one box leaves the other's lines, and the line underneath says where the rest went.
        launcher.ShowServerLog = false;

        Assert.Single(launcher.Log.Blocks);
        Assert.Contains("from the launcher", launcher.Log.Blocks[0].Body);
        Assert.Contains("1 hidden by the filters", launcher.LogSummary);

        launcher.ShowServerLog = true;

        Assert.Equal(2, launcher.Log.DrawnLineCount);
        Assert.Equal("2 lines", launcher.LogSummary);

        // And opening a folded run from the view is the same decision as opening it here.
        launcher.AppendLog(new LogEntry(Noon, LogLevel.Server, "from the server", LogSource.Server));

        var fold = Assert.IsType<LogRepeatBlock>(launcher.Log.Blocks[^1]);

        launcher.ToggleLogRepeat(fold);

        Assert.True(Assert.IsType<LogRepeatBlock>(launcher.Log.Blocks[^1]).Expanded);

        // The fourth box is the launcher's own notes: they are the patch server's lines as far as the boxes
        // above are concerned, and this is what takes them out of a log being pasted to somebody else.
        launcher.AppendDerivedLog(LogLevel.Info, "Fetching Root.wad", LogSource.Patcher);

        // Four lines held, four drawn: the run above was opened to be read, so its two lines count.
        Assert.Equal(4, launcher.Log.LineCount);
        Assert.Equal(4, launcher.Log.DrawnLineCount);

        launcher.ShowLauncherNotes = false;

        Assert.Equal(3, launcher.Log.DrawnLineCount);
        Assert.Equal(1, launcher.Log.HiddenLineCount);
        Assert.DoesNotContain(launcher.Log.Blocks, block => block.Body.Contains("Fetching Root.wad"));

        // The patch server's box is the other way of taking that line out, and it takes the patch server's
        // own lines with it - so between the two boxes the whole of one process's log can be handed over.
        // The line was already hidden by the fourth box, and hiding one line twice is still one line gone.
        launcher.ShowPatchServerLog = false;

        Assert.Equal(3, launcher.Log.DrawnLineCount);
        Assert.Equal(1, launcher.Log.HiddenLineCount);
    }

    /// <summary>Distinct lines, all of them the launcher's, to fill a Log with.</summary>
    private static IEnumerable<LogEntry> Filler(int count) {
        for (var i = 0; i < count; i++) {
            yield return Line($"line {i}", second: i);
        }
    }
}
