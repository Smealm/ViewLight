using System.Globalization;

namespace ViewLight.Services;

/// <summary>
/// The seven schools a wizard can be, as Imlight stores them.
/// </summary>
/// <remarks>
/// <para>
/// A wizard's school is persisted as Imlight's <c>MagicSchool</c> enum - <c>MagicSchoolBehavior.MagicSchool</c> -
/// and the numbers are template IDs rather than a running count, so they cannot be read as an ordinal. They
/// are listed here so the launcher can name a school without asking the server anything.
/// </para>
/// <para>
/// <b>Both spellings are the same school, and the one that turns up depends on the database.</b> Read out of
/// a real player database, a stored enum arrives as its <i>name</i>: the admin account's own document says
/// <c>"AuthLevel": "Administrator"</c>, and <c>AuthLevel</c> is an enum in Imlight's <c>Account</c>. So
/// <c>MagicSchoolBehavior.MagicSchool</c> is <c>"Storm"</c> and not <c>83375795</c>, even though the two mean
/// the same thing here - which is why both are accepted rather than one being assumed.
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
    /// <para>
    /// Either spelling: the id as it is written in the game's own template tables, or the name Imlight's
    /// enum is written with when a database stores it as text. A value that is neither is not a school the
    /// launcher can name, and nothing is invented for it - a creature's Sun, Moon, Star or Shadow is left
    /// unnamed rather than being read as a wizard's school.
    /// </para>
    /// <para>
    /// Case is ignored, because a name is a name: the enum's spelling is what the server writes, and a
    /// document from somewhere else that says <c>"storm"</c> is the same school.
    /// </para>
    /// </remarks>
    public static string NameOf(string? stored) {
        if (string.IsNullOrWhiteSpace(stored)) {
            return string.Empty;
        }

        var value = stored.Trim();

        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? NameOf(id)
            : Schools.FirstOrDefault(s => string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase)).Name
              ?? string.Empty;
    }
}
