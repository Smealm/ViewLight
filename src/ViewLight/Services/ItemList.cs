namespace ViewLight.Services;

/// <summary>
/// Reads and writes the comma-separated template ID lists Imlight uses (such as
/// <c>Character.DefaultItems</c>).
/// </summary>
public static class ItemList {

    /// <summary>
    /// Parses a comma-separated list, dropping blanks, duplicates and anything that is not a number.
    /// Duplicates matter: Imlight counts the list against RavenDB's 16 item batch limit, so a repeated
    /// ID would waste one of those slots.
    /// </summary>
    public static IReadOnlyList<uint> Parse(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return [];
        }

        var seen = new HashSet<uint>();
        var ids = new List<uint>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (uint.TryParse(part, out var id) && seen.Add(id)) {
                ids.Add(id);
            }
        }

        return ids;
    }

    public static string Format(IEnumerable<uint> ids) => string.Join(',', ids);
}
