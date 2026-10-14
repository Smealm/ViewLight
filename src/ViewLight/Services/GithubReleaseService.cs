using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Finds the newest installable Imlight build on GitHub.
/// </summary>
/// <remarks>
/// Every Imlight release is currently published as a <i>prerelease</i>, which means GitHub's
/// <c>/releases/latest</c> endpoint returns 404 for this repository. So we list releases and sort by
/// publish date ourselves - see <see cref="GitHubReleaseFeed"/>.
/// </remarks>
public sealed class GithubReleaseService {

    public const string DefaultRepository = "Revive101/Imlight";

    /// <summary>Asset name prefix for the Windows x64 self-contained build.</summary>
    private const string WindowsAssetPrefix = "Imlight-win-x64";

    private readonly HttpClient _http;
    private readonly string _repository;

    public GithubReleaseService(HttpClient? http = null, string repository = DefaultRepository) {
        _http = http ?? Http.Client;
        _repository = repository;
    }

    /// <summary>Returns the most recently published release that ships a Windows asset.</summary>
    /// <exception cref="InvalidOperationException">No usable release was found, or GitHub refused us.</exception>
    public async Task<ImlightRelease> GetLatestWindowsReleaseAsync(CancellationToken cancellationToken = default) {
        var releases = await GitHubReleaseFeed.ListAsync(_http, _repository, cancellationToken).ConfigureAwait(false);

        var best = releases
            .Select(TryCreateRelease)
            .Where(r => r is not null)
            .OrderByDescending(r => r!.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        return best ?? throw new InvalidOperationException(
            $"None of the {releases.Count} releases in {_repository} contain a '{WindowsAssetPrefix}*.zip' asset.");
    }

    private static ImlightRelease? TryCreateRelease(GitHubReleaseInfo release) {
        // Prefer the exact, unsuffixed asset name; older releases append the revision.
        var asset = release.Assets.FirstOrDefault(a =>
                        a.Name.Equals($"{WindowsAssetPrefix}.zip", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a =>
                        a.Name.StartsWith(WindowsAssetPrefix, StringComparison.OrdinalIgnoreCase)
                        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset is null) {
            return null;
        }

        return new ImlightRelease {
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
