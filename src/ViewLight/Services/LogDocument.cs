using System.Collections.ObjectModel;

namespace ViewLight.Services;

/// <summary>One block of the Log: the unit it is drawn in, and the unit a copy takes.</summary>
/// <remarks>
/// The Log used to be a row per line. That made every line its own little selectable box - a copy took one
/// line at a time - and it re-laid the columns out per row, which is what wrapped and broke the pictures
/// the server prints of itself. A block is instead a run of lines drawn as one piece of monospace text:
/// the terminal reads as a terminal, art keeps the shape it was printed in, and a drag across it copies
/// the whole region at once.
/// </remarks>
public abstract record LogBlock(IReadOnlyList<LogEntry> Lines) {

    public int LineCount => Lines.Count;

    /// <summary>The severity the whole block is coloured by: every line in one has the same one.</summary>
    public LogLevel Level => Lines[0].Level;

    /// <summary>Which of the three things wrote it: a block is never a mix of two of them.</summary>
    public LogSource Source => Lines[0].Source;

    /// <summary>Whether the lines here are a run of the same line with their tail folded away.</summary>
    public virtual bool IsFolded => false;

    /// <summary>The line that stands in for the ones hidden here, or null when nothing is hidden.</summary>
    public virtual string? FoldLine => null;

    /// <summary>Whether pressing the block's header would do anything.</summary>
    public bool HasFoldLine => FoldLine is not null;

    /// <summary>The lines as the terminal draws them, or nothing at all while they are folded away.</summary>
    public string Body => IsFolded ? string.Empty : string.Join('\n', Lines.Select(line => line.Line));

    public bool HasBody => !IsFolded;
}

/// <summary>A run of lines that are not the same line arriving again, drawn as they were written.</summary>
public sealed record LogTextBlock(IReadOnlyList<LogEntry> Lines) : LogBlock(Lines);

/// <summary>A run of the same line arriving over and over: one line on screen, with the count.</summary>
public sealed record LogRepeatBlock(IReadOnlyList<LogEntry> Lines, bool Expanded) : LogBlock(Lines) {

    public int Count => Lines.Count;

    public LogEntry First => Lines[0];

    public override bool IsFolded => !Expanded;

    public override string? FoldLine => LogLines.Folded(First, Count, Expanded);

    /// <summary>Whether these lines are all the same one, which is what makes them worth folding.</summary>
    public static bool AreOneMessage(IReadOnlyList<LogEntry> lines)
        => lines.Count > 1 && lines.All(line => Same(line, lines[0]));

    /// <summary>
    /// Whether two lines are the same line arriving again.
    /// </summary>
    /// <remarks>
    /// Neither the launcher's own timestamp nor a clock a process printed inside the message is part of it:
    /// a line that comes back a second later, or a warning the server repeats with a later millisecond in
    /// front of it, is still the same line - which is the whole of what folding is for. See
    /// <see cref="LogLines.Essence"/> for what is taken out and what is deliberately left in.
    /// </remarks>
    public static bool Same(LogEntry line, LogEntry other)
        => line.Level == other.Level && line.Source == other.Source
           && LogLines.Essence(line.Message) == LogLines.Essence(other.Message);
}

/// <summary>
/// The Log: every line the launcher, the server and the patch server have said, arranged into blocks.
/// </summary>
/// <remarks>
/// Kept out of the view model so that all of it - which runs of duplicates are folded, which sources are
/// being shown, what happens when the Log is full - is a decision that can be read and tested on its own.
///
/// Appending a line only ever touches the block at the bottom, except when the Log is full and the oldest
/// line has to go, which takes the first line off the block at the top. That is what keeps a busy server's
/// output from costing a rebuild of five thousand lines per line.
/// </remarks>
public sealed class LogDocument {

    /// <summary>How many lines the Log holds before the oldest ones start going.</summary>
    public const int MaxLines = 5000;

    /// <summary>
    /// The most lines one block is allowed. A block is one selectable region and one piece of text to lay
    /// out, so a couple of hundred lines is the compromise in the middle: long enough to copy a reading
    /// out of, short enough that the whole Log is not re-laid out every time a line arrives.
    /// </summary>
    public const int MaxBlockLines = 200;

    private readonly List<LogEntry> _entries = [];
    private readonly HashSet<LogSource> _hidden = [];

    /// <summary>
    /// Whether the launcher's own lines about the programs are being hidden.
    /// </summary>
    /// <remarks>
    /// Off by default, because they are part of the running account the Log is for. On is the reading a
    /// line is being pasted for: what the launcher noticed about the patch server is the launcher's, and a
    /// report to that program's own developers should be their lines and nobody else's.
    /// </remarks>
    private bool _hiddenDerived;

    /// <summary>
    /// The runs the reader has opened, by the first line of the run they opened. Held here rather than on
    /// the blocks so that changing a filter - which rebuilds the whole Log - does not fold them back up
    /// under somebody halfway through copying one out.
    /// </summary>
    private readonly HashSet<LogEntry> _opened = [];

    private int _hiddenLines;

    public ObservableCollection<LogBlock> Blocks { get; } = [];

    /// <summary>Every line the Log holds, which is at most <see cref="MaxLines"/> of them.</summary>
    public int LineCount => _entries.Count;

    /// <summary>
    /// How many lines are on screen: everything kept, less the sources filtered out and the tails of the
    /// runs folded away. A folded run still counts as the one line it is drawn as.
    /// </summary>
    public int DrawnLineCount => Tally().Drawn;

    /// <summary>How many lines are hidden behind the folded runs' headers.</summary>
    public int FoldedLineCount => Tally().Folded;

    public int FoldedRunCount => Tally().Runs;

    /// <summary>How many held lines the filters are keeping off the screen.</summary>
    public int HiddenLineCount => _hiddenLines;

    /// <summary>What the line under the Log says about what is on it.</summary>
    public string Summary {
        get {
            var (_, folded, runs) = Tally();
            return Describe(LineCount, folded, runs, HiddenLineCount);
        }
    }

    public bool IsVisible(LogSource source) => !_hidden.Contains(source);

    /// <summary>Whether the launcher's own lines about the programs are being shown.</summary>
    public bool DerivedLinesAreVisible => !_hiddenDerived;

    /// <summary>
    /// Shows or hides every line the launcher wrote about a program rather than taking from it.
    /// </summary>
    /// <remarks>
    /// The same deal as the source boxes, for the same reason: hiding is not throwing away, and unticking - 
    /// or ticking again, here - brings back what the Log is still holding. What is on one of the program's
    /// own screens stays: this is about who a line came from, not about what it is filed under.
    /// </remarks>
    public void SetDerivedVisible(bool visible) {
        if (_hiddenDerived == !visible) {
            return;
        }

        _hiddenDerived = !visible;
        _hiddenLines = _entries.Count(entry => !IsDrawn(entry));
        Rebuild();
    }

    /// <summary>Whether a line has a place on screen: neither its source nor its author is filtered out.</summary>
    private bool IsDrawn(LogEntry entry) => !_hidden.Contains(entry.Source) && !(_hiddenDerived && entry.Derived);

    /// <summary>Shows or hides everything one of the three writes.</summary>
    /// <remarks>
    /// Hidden lines are still held: unchecking a box is asking not to look at something, not asking for it
    /// to be thrown away, and so ticking it again brings back what is still in the Log. What it cannot bring
    /// back is what the <see cref="MaxLines"/> cap has dropped in the meantime.
    /// </remarks>
    public void SetVisible(LogSource source, bool visible) {
        var changed = visible ? _hidden.Remove(source) : _hidden.Add(source);

        if (!changed) {
            return;
        }

        _hiddenLines = _entries.Count(entry => !IsDrawn(entry));
        Rebuild();
    }

    public void Append(LogEntry entry) {
        _entries.Add(entry);

        if (IsDrawn(entry)) {
            AppendToLastBlock(entry);
        }
        else {
            _hiddenLines++;
        }

        DropOldest();
    }

    public void Clear() {
        _entries.Clear();
        Blocks.Clear();
        _hiddenLines = 0;
        _opened.Clear();
    }

    /// <summary>Opens a folded run so its lines can be read and copied, or folds it back up again.</summary>
    public void Toggle(LogBlock block) {
        if (block is not LogRepeatBlock repeat) {
            return;
        }

        var index = Blocks.IndexOf(repeat);

        if (index < 0) {
            // Gone since it was drawn: cleared, filtered out, or dropped off the top of a full Log.
            return;
        }

        if (repeat.Expanded) {
            _opened.Remove(repeat.First);
        }
        else {
            _opened.Add(repeat.First);
        }

        Blocks[index] = repeat with { Expanded = !repeat.Expanded };
    }

    /// <summary>What the line under the Log reads, given what it is holding.</summary>
    public static string Describe(int lines, int foldedLines, int foldedRuns, int hiddenLines) {
        var parts = new List<string> { $"{lines:N0} {(lines == 1 ? "line" : "lines")}" };

        if (foldedRuns > 0) {
            parts.Add($"{foldedLines:N0} folded into {foldedRuns:N0} {(foldedRuns == 1 ? "run" : "runs")}");
        }

        if (hiddenLines > 0) {
            parts.Add($"{hiddenLines:N0} hidden by the filters");
        }

        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- building

    private void AppendToLastBlock(LogEntry entry) {
        if (Blocks.Count == 0) {
            Blocks.Add(new LogTextBlock([entry]));
            return;
        }

        var last = Blocks[^1];

        if (last is LogRepeatBlock repeat && LogRepeatBlock.Same(repeat.First, entry)) {
            Blocks[^1] = BlockFor([.. repeat.Lines, entry]);
            return;
        }

        if (last is LogTextBlock text) {
            if (LogRepeatBlock.Same(text.Lines[^1], entry)) {
                // The line that has just arrived is the one before it again. That line comes back out of the
                // run it was in, so the two of them fold together the way a longer run of it would.
                var held = text.Lines[^1];
                var rest = text.Lines.Take(text.LineCount - 1).ToList();

                if (rest.Count == 0) {
                    Blocks.RemoveAt(Blocks.Count - 1);
                }
                else {
                    Blocks[^1] = BlockFor(rest);
                }

                Blocks.Add(BlockFor([held, entry]));
                return;
            }

            if (text.Level == entry.Level && text.Source == entry.Source && text.LineCount < MaxBlockLines) {
                Blocks[^1] = new LogTextBlock([.. text.Lines, entry]);
                return;
            }
        }

        Blocks.Add(new LogTextBlock([entry]));
    }

    private void Rebuild() {
        Blocks.Clear();

        foreach (var entry in _entries) {
            if (IsDrawn(entry)) {
                AppendToLastBlock(entry);
            }
        }
    }

    /// <summary>The block a run of lines makes: folded when they are all the same line arriving again.</summary>
    private LogBlock BlockFor(IReadOnlyList<LogEntry> lines)
        => LogRepeatBlock.AreOneMessage(lines)
            ? new LogRepeatBlock(lines, _opened.Contains(lines[0]))
            : new LogTextBlock(lines);

    private void DropOldest() {
        while (_entries.Count > MaxLines) {
            var dropped = _entries[0];
            _entries.RemoveAt(0);

            if (!IsDrawn(dropped)) {
                _hiddenLines--;
                continue;
            }

            DropTheFirstDrawnLine();
        }
    }

    private void DropTheFirstDrawnLine() {
        if (Blocks.Count == 0) {
            // Everything the Log is holding is behind a filter, so there is nothing on screen to take it off.
            return;
        }

        var front = Blocks[0];
        var rest = front.Lines.Skip(1).ToList();

        if (front is LogRepeatBlock) {
            // A fold is remembered by the run's first line, so when the run loses that line the memory moves
            // to the line that is now first - or goes, if there is no run left to fold.
            if (_opened.Remove(front.Lines[0]) && rest.Count > 1) {
                _opened.Add(rest[0]);
            }
        }

        if (rest.Count == 0) {
            Blocks.RemoveAt(0);
        }
        else {
            Blocks[0] = BlockFor(rest);
        }
    }

    private (int Drawn, int Folded, int Runs) Tally() {
        var drawn = 0;
        var folded = 0;
        var runs = 0;

        foreach (var block in Blocks) {
            if (block is LogRepeatBlock { Expanded: false } repeat) {
                drawn++;
                folded += repeat.Count;
                runs++;
            }
            else {
                drawn += block.LineCount;
            }
        }

        return (drawn, folded, runs);
    }
}
