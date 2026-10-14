using System.Globalization;

namespace ViewLight.Services;

/// <summary>
/// The seven schools a wizard can be, as Imlight stores them.
/// </summary>
/// <remarks>
/// <para>
/// A wizard's school is persisted as a number - <c>MagicSchoolBehavior.MagicSchool</c> - and the numbers
/// are template IDs rather than a running count, so they cannot be read as an ordinal. They are listed
/// here so the launcher can name a school without asking the server anything.
/// </para>
/// <para>
/// Only the seven a player can actually be are listed. Imlight's own enum also carries <c>None</c> and the
/// four schools that belong to creatures and cards (Sun, Star, Moon, Shadow); none of those is something a
/// wizard graduates from, so nothing here invents a name for one, and an unrecognised number reads as no
/// school at all rather than as whatever happened to sit in that slot.
/// </para>
/// </remarks>
public static class WizardSchool {

    /// <summary>An id and the name of the school it is.</summary>
    private static readonly (uint Id, string Name)[] Schools = [
        (2343174u, "Fire"),
        (72777u, "Ice"),
        (83375795u, "Storm"),
        (2448141u, "Myth"),
        (2330892u, "Life"),
        (78318724u, "Death"),
        (1027491821u, "Balance"),
    ];

    /// <summary>
    /// The schools a wizard can be, in the game's own order, which is the order they are shown in.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = [.. Schools.Select(s => s.Name)];

    /// <summary>
    /// The name of a school, or an empty string for a number that is not one of the seven.
    /// </summary>
    public static string NameOf(uint id)
        => Schools.FirstOrDefault(s => s.Id == id).Name ?? string.Empty;

    /// <summary>
    /// The name of a school from a stored value, or an empty string when it is not a school.
    /// </summary>
    /// <remarks>
    /// The document's value is a number, but a value written as text is accepted too: both spellings appear
    /// in the same family of documents, and refusing the readable one would turn a school badge off over
    /// punctuation.
    /// </remarks>
    public static string NameOf(string? stored) {
        if (string.IsNullOrWhiteSpace(stored)) {
            return string.Empty;
        }

        return uint.TryParse(stored.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? NameOf(id)
            : string.Empty;
    }
}
