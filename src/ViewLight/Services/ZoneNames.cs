using System.Text;

namespace ViewLight.Services;

/// <summary>
/// The server's zone names, as the game says them.
/// </summary>
/// <remarks>
/// <para>
/// Imlight writes a zone the way its own code names the thing - <c>WizardZone_UnicornWay</c> - which is
/// precise and is not something to put in front of a player. The name is one word with the capitals in it,
/// so saying it properly is a matter of splitting it up, and that is all this does: nothing here invents a
/// name, and a name it cannot make sense of is left exactly as the server wrote it.
/// </para>
/// <para>
/// A zone the server names by an id - <c>WizardZone_00000878</c> - is an instance rather than a place: a
/// house, a gauntlet run, a dungeon being stood up. It is a real zone somebody was in, and nothing about it
/// can be named, so <see cref="IsNamed"/> says so and the rest of the launcher leaves it out of anything it
/// would have to name.
/// </para>
/// <para>
/// The world a zone is in is not in the zone's name at all. It is in the path the server loads the zone
/// from - <c>WizardCity/WC_Streets/WC_Unicorn</c> - and the two are paired from the server's own log; see
/// <see cref="ProfileLedger"/>. A world label is a path segment, run together the same way a zone name is,
/// so it is split up the same way too.
/// </para>
/// </remarks>
public static class ZoneNames {

    /// <summary>What the server puts in front of every zone name it writes.</summary>
    public const string Prefix = "WizardZone_";

    /// <summary>
    /// Whether a zone the server names is a place with a name rather than an instance with an id.
    /// </summary>
    public static bool IsNamed(string? zone)
        => zone is not null
           && zone.Length > Prefix.Length
           && zone.StartsWith(Prefix, StringComparison.Ordinal)
           && !zone[Prefix.Length..].All(char.IsDigit);

    /// <summary>
    /// The name of a zone as a player would say it, or empty for one the server only numbered.
    /// </summary>
    public static string NameOf(string? zone)
        => IsNamed(zone) ? Spelled(zone![Prefix.Length..]) : string.Empty;

    /// <summary>
    /// A run-together name split into words: <c>UnicornWay</c> becomes <c>Unicorn Way</c>.
    /// </summary>
    /// <remarks>
    /// A capital after a lowercase letter or a digit starts a word, and an underscore is a space - which is
    /// how the game's own names are built. Nothing is title-cased or lower-cased: the server's capitals are
    /// the name.
    /// </remarks>
    public static string Spelled(string runTogether) {
        var words = new StringBuilder(runTogether.Length + 4);

        for (var i = 0; i < runTogether.Length; i++) {
            var character = runTogether[i];

            if (character == '_') {
                if (words.Length > 0 && words[^1] != ' ') {
                    words.Append(' ');
                }

                continue;
            }

            if (i > 0
                && char.IsUpper(character)
                && (char.IsLower(runTogether[i - 1]) || char.IsDigit(runTogether[i - 1]))) {
                words.Append(' ');
            }

            words.Append(character);
        }

        return words.ToString();
    }
}
