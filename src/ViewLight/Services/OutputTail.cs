namespace ViewLight.Services;

/// <summary>
/// What a background process said on its console, newest last, kept in a bounded amount.
/// </summary>
/// <remarks>
/// <para>
/// A process the launcher starts is drained rather than left to fill its pipe - a database that cannot write
/// its console stalls - and draining it into nothing is why a database that never came up never said why. So
/// the tail is kept: the last <see cref="Capacity"/> lines, which is the part that is about how the process
/// ended rather than the banner it printed on the way in.
/// </para>
/// <para>
/// Bounded and thread-safe, because this is written from the process's own threads while the screen is
/// reading it, and a process that talks a great deal must not grow the launcher's memory with it. Blank
/// lines are dropped: they are the console's spacing, not something anybody reads.
/// </para>
/// </remarks>
public sealed class OutputTail {

    /// <summary>How many lines are kept by default: enough for a stack trace, small enough to paste.</summary>
    public const int Capacity = 80;

    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly int _capacity;

    public OutputTail(int capacity = Capacity) => _capacity = Math.Max(1, capacity);

    /// <summary>Takes one line off the process's console, or drops it when there is nothing to keep.</summary>
    public void Add(string? line) {
        var text = line?.Trim() ?? string.Empty;

        if (text.Length == 0) {
            return;
        }

        lock (_gate) {
            _lines.Add(text);

            if (_lines.Count > _capacity) {
                _lines.RemoveAt(0);
            }
        }
    }

    public void Clear() {
        lock (_gate) {
            _lines.Clear();
        }
    }

    /// <summary>What has been kept, oldest of the kept lines first.</summary>
    public IReadOnlyList<string> Lines {
        get {
            lock (_gate) {
                return [.. _lines];
            }
        }
    }

    public bool IsEmpty => Lines.Count == 0;
}
