using System.Text.Json;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Persists the launcher's own profiles to <c>profiles.json</c> in the launcher's data root.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <c>launcher.json</c> on purpose: the settings file is small and read at startup, while
/// this one grows with every session and every badge, and a corrupt badge file must not cost the user
/// their folders. The two also fail in opposite directions - a settings file that will not load is
/// recreated from the defaults, while this one is recreated empty and simply starts collecting again.
/// </para>
/// <para>
/// Nothing here is ever sent anywhere. A profile is what a Wizard101 account is to this machine, and the
/// achievement system it feeds is a local one by design - see <see cref="ProfileLedger"/>.
/// </para>
/// </remarks>
public sealed class ProfileStore {

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true,
    };

    private LauncherProfiles _file = new();

    public string FilePath { get; }

    public ProfileStore(string? filePath = null) {
        FilePath = filePath ?? LauncherPaths.ProfilesFile;
        _file = Load();
    }

    /// <summary>Every profile, in the order they were first created.</summary>
    public IReadOnlyList<LauncherProfile> Profiles => _file.Profiles;

    /// <summary>
    /// Which world each zone belongs to, as learned from the server's log and kept between runs.
    /// </summary>
    /// <remarks>
    /// Written through rather than passed around, because it is the same kind of thing as the profiles: the
    /// launcher's own note about a server, learned by watching it and useful for as long as that server is
    /// the one being played on.
    /// </remarks>
    public Dictionary<string, string> ZoneWorlds => _file.ZoneWorlds;

    /// <summary>
    /// The profile for an account, created if this is the first time the account has been seen.
    /// </summary>
    /// <remarks>
    /// The account id is the key rather than the username, because the id is what Play signs in with and
    /// what the server's own records - the command log, the online list - are keyed by. A renamed account
    /// is the same profile with a new username, not a new profile.
    /// </remarks>
    public LauncherProfile GetOrCreate(ulong accountId, string username) {
        var profile = _file.Profiles.FirstOrDefault(p => p.AccountId == accountId);

        if (profile is null) {
            profile = new LauncherProfile {
                AccountId = accountId,
                Username = username,
            };

            _file.Profiles.Add(profile);
            return profile;
        }

        // The username is only ever presented, and the server is the authority on it.
        if (!string.IsNullOrWhiteSpace(username)) {
            profile.Username = username;
        }

        return profile;
    }

    public bool Has(ulong accountId) => _file.Profiles.Any(p => p.AccountId == accountId);

    /// <summary>
    /// The absolute path of an image a profile uses, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Only the file name is stored, and it is resolved against the profile's folder inside the launcher's
    /// data root. That is what keeps a portable launcher portable: move the folder and the pictures come
    /// with it, because nothing wrote down where the folder was.
    /// </remarks>
    public static string? ImagePath(ulong accountId, string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(LauncherPaths.ProfileDirectory(accountId), fileName);

    /// <summary>
    /// Copies an image into the profile's own folder and records it, so the profile keeps working when the
    /// file it was picked from is moved or deleted.
    /// </summary>
    /// <returns>The recorded file name, or null when the file could not be read.</returns>
    public string? AdoptImage(LauncherProfile profile, string sourcePath, bool banner) {
        try {
            var directory = LauncherPaths.ProfileDirectory(profile.AccountId);
            Directory.CreateDirectory(directory);

            // Kept under a name of the launcher's own choosing, so a file called the same as another
            // profile's picture cannot end up as both. The kind is in the name so replacing a banner
            // cannot overwrite an avatar.
            var kind = banner ? "banner" : "avatar";
            var name = $"{kind}{Path.GetExtension(sourcePath)}";
            var destination = Path.Combine(directory, name);

            File.Copy(sourcePath, destination, overwrite: true);

            if (banner) {
                profile.BannerFile = name;
            }
            else {
                profile.AvatarFile = name;
            }

            Save();
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>Forgets a profile's picture, leaving the file where it is - it may be another profile's.</summary>
    public void ClearImage(LauncherProfile profile, bool banner) {
        if (banner) {
            profile.BannerFile = null;
        }
        else {
            profile.AvatarFile = null;
        }

        Save();
    }

    public void Save() {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(_file, s_options));
    }

    private LauncherProfiles Load() {
        try {
            if (!File.Exists(FilePath)) {
                return new LauncherProfiles();
            }

            var json = File.ReadAllText(FilePath);

            // Written from the launcher, read back by it: a file that will not parse is one this launcher
            // did not write, or one that was cut short. Either way an empty set that collects from here is
            // better than refusing to start, and never worse than acting on half a file.
            return JsonSerializer.Deserialize<LauncherProfiles>(json, s_options) ?? new LauncherProfiles();
        }
        catch (Exception) {
            return new LauncherProfiles();
        }
    }
}
