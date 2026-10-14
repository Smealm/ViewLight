using System.Text.Json;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Finds the newest installable Imlight build on GitHub.
/// </summary>
/// <remarks>
/// Every Imlight release is currently published as a <i>prerelease</i>, which means GitHub's
/// <c>/releases/latest</c> endpoint returns 404 for this repository. So we list releases and sort by
/// publish date ourselves.
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
        var releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);

        var best = releases
            .Select(TryCreateRelease)
            .Where(r => r is not null)
            .OrderByDescending(r => r!.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        return best ?? throw new InvalidOperationException(
            $"None of the {releases.Count} releases in {_repository} contain a '{WindowsAssetPrefix}*.zip' asset.");
    }

    private async Task<List<GitHubReleaseDto>> GetReleasesAsync(CancellationToken cancellationToken) {
        using var response = await _http
            .GetAsync($"https://api.github.com/repos/{_repository}/releases?per_page=30",
                HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            var hint = (int)response.StatusCode switch {
                403 or 429 => " GitHub rate-limited this machine; wait a few minutes and try again.",
                404 => $" Repository '{_repository}' was not found.",
                _ => string.Empty,
            };

            throw new InvalidOperationException(
                $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} while listing releases.{hint}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var result = new List<GitHubReleaseDto>();
        foreach (var element in document.RootElement.EnumerateArray()) {
            result.Add(ParseRelease(element));
        }

        return result;
    }

    private static GitHubReleaseDto ParseRelease(JsonElement element) {
        var assets = new List<GitHubAssetDto>();
        if (element.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array) {
            foreach (var asset in assetsElement.EnumerateArray()) {
                assets.Add(new GitHubAssetDto(
                    asset.GetProperty("name").GetString() ?? string.Empty,
                    asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
                    asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
            }
        }

        DateTimeOffset? published = null;
        if (element.TryGetProperty("published_at", out var publishedElement)
            && publishedElement.ValueKind == JsonValueKind.String
            && publishedElement.TryGetDateTimeOffset(out var parsed)) {
            published = parsed;
        }

        var tag = element.GetProperty("tag_name").GetString() ?? string.Empty;

        return new GitHubReleaseDto(
            Tag: tag,
            Name: element.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? tag
                : tag,
            PublishedAt: published,
            Prerelease: element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
            Assets: assets);
    }

    private static ImlightRelease? TryCreateRelease(GitHubReleaseDto dto) {
        // Prefer the exact, unsuffixed asset name; older releases append the revision.
        var asset = dto.Assets.FirstOrDefault(a =>
                        a.Name.Equals($"{WindowsAssetPrefix}.zip", StringComparison.OrdinalIgnoreCase))
                    ?? dto.Assets.FirstOrDefault(a =>
                        a.Name.StartsWith(WindowsAssetPrefix, StringComparison.OrdinalIgnoreCase)
                        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset is null) {
            return null;
        }

        return new ImlightRelease {
            Tag = dto.Tag,
            Name = dto.Name,
            PublishedAt = dto.PublishedAt,
            Prerelease = dto.Prerelease,
            AssetName = asset.Name,
            AssetUrl = asset.Url,
            AssetSize = asset.Size,
        };
    }

    private sealed record GitHubReleaseDto(
        string Tag,
        string Name,
        DateTimeOffset? PublishedAt,
        bool Prerelease,
        List<GitHubAssetDto> Assets);

    private sealed record GitHubAssetDto(string Name, string Url, long Size);
}
