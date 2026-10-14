namespace ViewLight.Services;

/// <summary>
/// Whether this copy of the launcher keeps everything inside its own folder.
/// </summary>
/// <remarks>
/// <para>
/// Portable mode is switched on by a file beside the executable rather than by a setting, because it
/// decides <i>where the settings are</i>: a toggle stored in <c>launcher.json</c> could not be read
/// before the launcher had decided which <c>launcher.json</c> to read. The marker is the whole of the
/// switch, so the folder can be carried to another drive and still be portable on arrival.
/// </para>
/// <para>
/// With it on, <see cref="LauncherPaths.DataRoot"/> is the executable's own folder, so the settings, the
/// downloaded server, the cached art and the game all live underneath it and are addressed relative to
/// it. Nothing outside that folder is assumed to exist.
/// </para>
/// </remarks>
public static class PortableMode {

    /// <summary>
    /// The file beside <c>ViewLight.exe</c> whose presence turns portable mode on. An empty file is
    /// enough: it exists to be seen and documented, not to hold anything.
    /// </summary>
    public const string MarkerFileName = "portable.txt";

    private static bool s_isOn;

    /// <summary>True when this run is portable. Decided once by <see cref="Apply"/>, which runs first.</summary>
    public static bool IsOn => s_isOn;

    /// <summary>The folder the executable lives in: the portable root, and the data root when portable.</summary>
    public static string ExecutableDirectory
        => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>The marker file itself.</summary>
    public static string MarkerPath => Path.Combine(ExecutableDirectory, MarkerFileName);

    /// <summary>
    /// Decides the mode and points the launcher at the right data root.
    /// </summary>
    /// <remarks>
    /// Must run before anything reads <see cref="LauncherPaths"/>, because the first read fixes every
    /// path the launcher uses. Nothing here may depend on the data root itself.
    /// </remarks>
    /// <param name="executableDirectory">
    /// Where to look instead of beside the running executable. Only tests pass this: in the launcher the
    /// folder is the one the application was started from, which is the whole point of the mode.
    /// </param>
    /// <returns>Whether portable mode is on.</returns>
    public static bool Apply(string? executableDirectory = null) {
        var directory = string.IsNullOrWhiteSpace(executableDirectory)
            ? ExecutableDirectory
            : Path.TrimEndingDirectorySeparator(executableDirectory);

        s_isOn = MarkerExistsIn(directory);

        if (s_isOn) {
            LauncherPaths.DataRoot = directory;
        }

        return s_isOn;
    }

    /// <summary>Whether the marker is beside the running executable, without changing anything.</summary>
    public static bool MarkerExists() => MarkerExistsIn(ExecutableDirectory);

    private static bool MarkerExistsIn(string directory) {
        try {
            return File.Exists(Path.Combine(directory, MarkerFileName));
        }
        catch (Exception) {
            // An unreadable folder is not a portable one.
            return false;
        }
    }

    /// <summary>
    /// Writes or removes the marker.
    /// </summary>
    /// <remarks>
    /// The mode only takes effect on the next start: the paths in use were settled when this one began,
    /// so a running launcher cannot migrate itself. The UI says so rather than pretending otherwise.
    /// </remarks>
    /// <returns>Whether the marker could be changed; a read-only folder refuses.</returns>
    public static bool SetMarker(bool on) {
        try {
            if (on) {
                File.WriteAllText(MarkerPath,
                    "Portable mode is on because this file exists.\r\n"
                    + $"Delete it to keep ViewLight's settings in {LauncherPaths.NormalDataRoot} again.\r\n");
            }
            else if (File.Exists(MarkerPath)) {
                File.Delete(MarkerPath);
            }

            return true;
        }
        catch (Exception) {
            return false;
        }
    }
}
