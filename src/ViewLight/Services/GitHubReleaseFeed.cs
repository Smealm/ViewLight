using System.Text.Json;

namespace ViewLight.Services;

/// <summary>A release, as GitHub's REST API reports it.</summary>
/// <param name="Tag">Release tag, e.g. <c>r806919.Wizard_1_610</c> or <c>v4.0.2</c>.</param>
/// <param name="Name">Release display name (falls back to the tag).</param>
/// <param name="PublishedAt">When it was published, or <c>null</c> when GitHub did not say.</param>
/// <param name="Prerelease">Whether GitHub marks it as a prerelease.</param>
/// <param name="Assets">Everything attached to it.</param>
internal sealed record GitHubReleaseInfo(
    string Tag,
    string Name,
    DateTimeOffset? PublishedAt,
    bool Prerelease,
    IReadOnlyList<GitHubAssetInfo> Assets);

/// <summary>One downloadable file on a release.</summary>
internal sealed record GitHubAssetInfo(string Name, string Url, long Size);

/// <summary>
/// Lists the releases of a GitHub repository, with the error handling every release lookup needs.
/// </summary>
/// <remarks>
/// The launcher looks up two repositories - Imlight for the server and Aurorium for the patch server -
/// and both are published as <i>prereleases</i>, so GitHub's <c>/releases/latest</c> endpoint is no use
/// for either: it 404s. The list is fetched and sorted by publish date instead, and the refusal messages
/// are the same for both, so they live here once.
/// </remarks>
internal static class GitHubReleaseFeed {

    /// <summary>How many releases to ask for. Enough to find a build even when a few are unusable.</summary>
    private const int PerPage = 30;

    /// <exception cref="InvalidOperationException">GitHub refused the request.</exception>
    public static async Task<IReadOnlyList<GitHubReleaseInfo>> ListAsync(
        HttpClient http, string repository, CancellationToken cancellationToken) {

        using var response = await http
            .GetAsync($"https://api.github.com/repos/{repository}/releases?per_page={PerPage}",
                HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            var hint = (int)response.StatusCode switch {
                403 or 429 => " GitHub rate-limited this machine; wait a few minutes and try again.",
                404 => $" Repository '{repository}' was not found.",
                _ => string.Empty,
            };

            throw new InvalidOperationException(
                $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} while listing releases.{hint}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var result = new List<GitHubReleaseInfo>();
        foreach (var element in document.RootElement.EnumerateArray()) {
            result.Add(ParseRelease(element));
        }

        return result;
    }

    private static GitHubReleaseInfo ParseRelease(JsonElement element) {
        var assets = new List<GitHubAssetInfo>();
        if (element.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array) {
            foreach (var asset in assetsElement.EnumerateArray()) {
                assets.Add(new GitHubAssetInfo(
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

        return new GitHubReleaseInfo(
            Tag: tag,
            Name: element.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? tag
                : tag,
            PublishedAt: published,
            Prerelease: element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
            Assets: assets);
    }
}
