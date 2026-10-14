using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Finds the newest installable Aurorium build on GitHub.
/// </summary>
/// <remarks>
/// Aurorium is the Revive101 project's file and backend server: it fetches the game assets the
/// Wizard101 client needs and serves them over HTTP, which is exactly what Imlight asks a "patch
/// server" for. Running one locally is what takes the community's patch server out of the middle of a
/// session.
/// </remarks>
public sealed class PatchServerReleaseService {

    public const string DefaultRepository = "Revive101/Aurorium";

    /// <summary>
    /// The Windows build's asset name. Aurorium publishes one executable per platform under its plain
    /// name, so the Windows one is the only asset that ends in <c>.exe</c>.
    /// </summary>
    public const string WindowsAssetName = "aurorium.exe";

    private readonly HttpClient _http;
    private readonly string _repository;

    public PatchServerReleaseService(HttpClient? http = null, string repository = DefaultRepository) {
        _http = http ?? Http.Client;
        _repository = repository;
    }

    /// <summary>Returns the most recently published release that ships the Windows patch server.</summary>
    /// <exception cref="InvalidOperationException">No usable release was found, or GitHub refused us.</exception>
    public async Task<PatchServerRelease> GetLatestWindowsReleaseAsync(CancellationToken cancellationToken = default) {
        var releases = await GitHubReleaseFeed.ListAsync(_http, _repository, cancellationToken).ConfigureAwait(false);

        var best = releases
            .Select(TryCreateRelease)
            .Where(r => r is not null)
            .OrderByDescending(r => r!.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        return best ?? throw new InvalidOperationException(
            $"None of the {releases.Count} releases in {_repository} contain a '{WindowsAssetName}' asset.");
    }

    private static PatchServerRelease? TryCreateRelease(GitHubReleaseInfo release) {
        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.Equals(WindowsAssetName, StringComparison.OrdinalIgnoreCase));

        if (asset is null) {
            return null;
        }

        return new PatchServerRelease {
            Tag = release.Tag,
            Name = release.Name,
            PublishedAt = release.PublishedAt,
            Prerelease = release.Prerelease,
            AssetName = asset.Name,
            AssetUrl = asset.Url,
            AssetSize = asset.Size,
        };
    }
}
