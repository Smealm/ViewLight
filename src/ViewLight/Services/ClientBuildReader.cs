using System.Text.RegularExpressions;

namespace ViewLight.Services;

/// <summary>
/// The build of the Wizard101 client in the game folder, read from the client itself.
/// </summary>
/// <remarks>
/// <para>
/// The client records the revision it was built from in <c>bin\revision.dat</c> - one line, e.g.
/// <c>r806919.Wizard_1_610</c>. That is read first because it is the client's own statement of what it
/// is, and it is there before the client has ever been started.
/// </para>
/// <para>
/// When that file is missing - an older or half-patched install - the same revision is taken from the
/// line the client writes into <c>bin\WizardClient.log</c> the first time it runs:
/// <c>CORE_SEER Wizard Graphical Client version 'W.1.610.21'; revision 'r806919.Wizard_1_610'.</c>
/// </para>
/// </remarks>
public static partial class ClientBuildReader {

    /// <summary>Where the client states its own revision, relative to the game folder.</summary>
    public const string RelativeRevisionFile = @"bin\revision.dat";

    /// <summary>The client's log, relative to the game folder; the fallback source.</summary>
    public const string RelativeClientLog = @"bin\WizardClient.log";

    /// <summary>
    /// The revision in the game folder, or null when the folder holds neither source. Never throws: a
    /// file that cannot be read means the build is simply unknown.
    /// </summary>
    public static string? Read(string? gameFolder) {
        if (string.IsNullOrWhiteSpace(gameFolder)) {
            return null;
        }

        return FromRevisionFile(Path.Combine(gameFolder, RelativeRevisionFile))
               ?? FromClientLog(Path.Combine(gameFolder, RelativeClientLog));
    }

    /// <summary>Reads <c>bin\revision.dat</c>: one line, and nothing else.</summary>
    public static string? FromRevisionFile(string revisionFilePath) {
        try {
            return File.Exists(revisionFilePath)
                ? Clean(File.ReadLines(revisionFilePath).FirstOrDefault())
                : null;
        }
        catch (IOException) {
            return null;
        }
        catch (UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// Reads the revision out of the client's log. Lines are streamed and the scan stops at the first
    /// match, because a log that has been appended to for hours is megabytes long.
    /// </summary>
    public static string? FromClientLog(string logFilePath) {
        if (!File.Exists(logFilePath)) {
            return null;
        }

        try {
            foreach (var line in File.ReadLines(logFilePath)) {
                var match = RevisionInLogLine().Match(line);
                if (match.Success) {
                    return Clean(match.Groups["revision"].Value);
                }
            }
        }
        catch (IOException) {
            return null;
        }
        catch (UnauthorizedAccessException) {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Whether two build strings name the same revision. Case and surrounding space are ignored; a
    /// missing build never matches anything, so an unreadable client is never reported as agreeing.
    /// </summary>
    public static bool AreSame(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Orders two builds by the revision number they carry, so "older" and "newer" can be said plainly
    /// instead of telling the user to update whichever of the two happens to be older.
    /// </summary>
    /// <returns>
    /// Negative when <paramref name="left"/> is the older build, positive when it is the newer one, zero
    /// when they are the same revision, and <c>null</c> when either cannot be read - which is not a
    /// comparison, so nothing is claimed from it.
    /// </returns>
    public static int? Compare(string? left, string? right) {
        if (!TryRevisionNumber(left, out var first) || !TryRevisionNumber(right, out var second)) {
            return null;
        }

        return first.CompareTo(second);
    }

    /// <summary>The number in <c>r806919.Wizard_1_610</c>, which is what puts one build before another.</summary>
    private static bool TryRevisionNumber(string? build, out long number) {
        number = 0;

        if (string.IsNullOrWhiteSpace(build)) {
            return false;
        }

        var match = RevisionNumberInBuild().Match(build);

        return match.Success
               && long.TryParse(match.Groups["number"].Value, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// The <c>r</c> number a revision starts with. Four digits minimum, so a stray <c>r1</c> in some other
    /// part of the string is not mistaken for a revision.
    /// </summary>
    [GeneratedRegex(@"\br(?<number>\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionNumberInBuild();

    private static string? Clean(string? value) {
        var trimmed = value?.Trim().Trim('\uFEFF', '"', '\'').Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// <c>revision 'r806919.Wizard_1_610'</c>. Anchored on the word so the many other lines that mention
    /// a revision differently - <c>Revision: 512.00</c>, the patch URL carrying the same string - are not
    /// mistaken for it.
    /// </summary>
    [GeneratedRegex(@"\brevision\s+'(?<revision>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionInLogLine();
}
