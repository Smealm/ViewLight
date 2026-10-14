using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// The launcher's own profiles: what a Wizard101 account is to <i>this machine</i>.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the launcher's notes about a server - the profiles, their playtime and badges, and which
/// world each zone belongs to - reached through one surface so nothing above has to know where they are kept.
/// They used to be <c>profiles.json</c>; they are the launcher's own database now, which is one document per
/// profile rather than one file holding everything
/// (see <see cref="DatabaseProfilePersistence"/>). <see cref="IProfilePersistence"/> is where that difference
/// lives, and this class is the same surface it always was.
/// </para>
/// <para>
/// <b>A store with nothing attached yet holds nothing.</b> That is not a half-built state to paper over: the
/// launcher's database takes tens of seconds to come up, and until it does there is nowhere for a note to go.
/// Saving before then is refused and said so rather than written somewhere else - a launcher writing its notes
/// to a file and to a database would have two answers to every question and no way to know which is newer.
/// Nothing is lost by it in practice, because the notes are only ever added to while the server is running,
/// and the server is only started once the database is up.
/// </para>
/// </remarks>
public sealed class ProfileStore {

    private LauncherProfiles _file = new();
    private IProfilePersistence? _persistence;

    /// <summary>A store with nowhere to keep anything yet: the launcher's own database has not come up.</summary>
    public ProfileStore() {
    }

    /// <summary>A store over a file. The suite's store, and what an old launcher's notes are read from.</summary>
    public ProfileStore(string filePath) => Attach(new JsonProfilePersistence(filePath));

    public ProfileStore(IProfilePersistence persistence) => Attach(persistence);

    /// <summary>
    /// Why the notes could not be read or written, or <c>null</c> when everything went through.
    /// </summary>
    /// <remarks>
    /// Kept rather than thrown: a database that has stopped answering must not take the profile screen down
    /// with it, and the same field carries the smaller news of a note or two that had to be skipped.
    /// </remarks>
    public string? Problem { get; private set; }

    /// <summary>Whether there is anywhere to keep notes at all yet.</summary>
    public bool IsAttached => _persistence is not null;

    /// <summary>Every profile, in the order they were first seen.</summary>
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
    /// Points the store at where the notes are kept, and reads what is there.
    /// </summary>
    /// <remarks>
    /// Called when the launcher's own database comes up, which is after this is built: the store has to exist
    /// before anything asks it for a profile, and the database takes tens of seconds to answer.
    /// </remarks>
    /// <returns>Whether anything was read. Nothing is lost when nothing was: the launcher simply has no
    /// profiles yet, which is what a first run looks like too.</returns>
    public bool Attach(IProfilePersistence persistence) {
        _persistence = persistence;
        return Reload();
    }

    /// <summary>
    /// Re-reads everything from where it is kept.
    /// </summary>
    /// <remarks>
    /// What is already held is left alone when the read fails: an empty set in its place would be written back
    /// over the database the moment anything changed, which is how a database would lose everything it holds
    /// to a moment of the server not answering.
    /// </remarks>
    public bool Reload() {
        if (_persistence is null) {
            return false;
        }

        var loaded = _persistence.Load(out var problem);
        Problem = problem;

        if (loaded is null) {
            return false;
        }

        _file = loaded;
        return true;
    }

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
    /// Keeps an image for a profile: into the profile's own document, so it is part of what the launcher's
    /// database holds and part of any backup of it.
    /// </summary>
    /// <returns>The recorded file name, or null when the picture could not be kept.</returns>
    /// <remarks>
    /// Kept under a name of the launcher's own choosing, so a file called the same as another profile's
    /// picture cannot end up as both. The kind is in the name so replacing a banner cannot overwrite an
    /// avatar.
    /// </remarks>
    public async Task<string?> AdoptImageAsync(
        LauncherProfile profile, string sourcePath, bool banner, CancellationToken token = default) {

        if (_persistence is null) {
            Problem = "The launcher's own database is not up yet, so the picture was not kept.";
            return null;
        }

        try {
            var content = await File.ReadAllBytesAsync(sourcePath, token).ConfigureAwait(true);

            var kind = banner ? "banner" : "avatar";
            var name = $"{kind}{Path.GetExtension(sourcePath)}";

            if (!await _persistence.WriteImageAsync(profile.AccountId, name, content, token).ConfigureAwait(true)) {
                return null;
            }

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

    /// <summary>
    /// A profile's picture, as the bytes to show, or null when it has none - or when it cannot be read just
    /// now, which the profile screen answers by showing the initial-letter circle instead.
    /// </summary>
    public async Task<byte[]?> ReadImageAsync(ulong accountId, string? fileName, CancellationToken token = default) {
        if (_persistence is null || string.IsNullOrWhiteSpace(fileName)) {
            return null;
        }

        return await _persistence.ReadImageAsync(accountId, fileName, token).ConfigureAwait(true);
    }

    /// <summary>Forgets a profile's picture, and takes it out of what is kept.</summary>
    public async Task ClearImageAsync(LauncherProfile profile, bool banner, CancellationToken token = default) {
        var name = banner ? profile.BannerFile : profile.AvatarFile;

        if (banner) {
            profile.BannerFile = null;
        }
        else {
            profile.AvatarFile = null;
        }

        if (_persistence is not null && !string.IsNullOrWhiteSpace(name)) {
            await _persistence.DeleteImageAsync(profile.AccountId, name, token).ConfigureAwait(true);
        }

        Save();
    }

    /// <summary>Writes everything that is held back to where it is kept.</summary>
    public void Save() {
        if (_persistence is null) {
            Problem = "The launcher's own database is not up yet, so this was not kept anywhere.";
            return;
        }

        if (_persistence.Save(_file, out var problem)) {
            Problem = problem;
            return;
        }

        Problem = problem ?? "The launcher's own notes could not be saved.";
    }
}
