using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Finds the newest installable Imlight build on GitHub, for the channel that was asked for.
/// </summary>
/// <remarks>
/// <para>
/// Every Imlight release is currently published as a <i>prerelease</i>, which means GitHub's
/// <c>/releases/latest</c> endpoint returns 404 for this repository. So we list releases and sort by
/// publish date ourselves - see <see cref="GitHubReleaseFeed"/>.
/// </para>
/// <para>
/// The two published channels are answered differently on purpose. <b>Stable</b> is only releases GitHub
/// does not mark as a prerelease, and having none is a real answer rather than a reason to hand back the
/// prerelease nobody asked for - it is what the repository looks like today. <b>Prerelease</b> is the
/// newest of everything: the point of the channel is to be ahead of stable, and when nothing newer has
/// been published it must still be able to install the stable build rather than nothing at all.
/// </para>
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
    /// <param name="channel">
    /// <see cref="ServerChannelChoices.Stable"/> or <see cref="ServerChannelChoices.Prerelease"/>; the
    /// default is the prerelease channel, which is what the launcher installs unless asked otherwise.
    /// </param>
    /// <exception cref="InvalidOperationException">No usable release was found, or GitHub refused us.</exception>
    public async Task<ImlightRelease> GetLatestWindowsReleaseAsync(
        string channel = ServerChannelChoices.Prerelease,
        CancellationToken cancellationToken = default) {

        if (channel == ServerChannelChoices.BleedingEdge) {
            throw new InvalidOperationException(
                "Bleeding edge builds come from Imlight's source rather than a GitHub release.");
        }

        var releases = await GitHubReleaseFeed.ListAsync(_http, _repository, cancellationToken).ConfigureAwait(false);

        var candidates = releases
            .Select(TryCreateRelease)
            .Where(r => r is not null);

        // Stable means stable: a repository with only prereleases has no stable build to offer, and
        // quietly installing one would be the one thing the channel exists to avoid.
        if (channel == ServerChannelChoices.Stable) {
            candidates = candidates.Where(r => !r!.Prerelease);
        }

        var best = candidates
            .OrderByDescending(r => r!.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (best is not null) {
            return best;
        }

        if (channel == ServerChannelChoices.Stable) {
            throw new InvalidOperationException(
                $"No stable Imlight release has been published in {_repository} yet - every release there "
                + "is marked as a prerelease. Choose the Prerelease channel to install one, or Bleeding edge "
                + "to build the current source.");
        }

        throw new InvalidOperationException(
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
