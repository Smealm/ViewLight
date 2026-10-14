using System.Net.Http.Headers;

namespace ViewLight.Services;

/// <summary>Shared HTTP plumbing. GitHub's API rejects requests without a User-Agent.</summary>
internal static class Http {

    public static HttpClient Client { get; } = Create();

    /// <summary>
    /// For plain file downloads, such as the game art. Deliberately not <see cref="Client"/>: that one
    /// asks for GitHub's JSON media type, which has no business being sent to a CDN.
    /// </summary>
    public static HttpClient Files { get; } = CreateFiles();

    private static HttpClient CreateFiles() {
        var client = new HttpClient {
            // A few megabytes of art, or a slow connection to fetch it over.
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ViewLight-Launcher/1.0");
        return client;
    }

    private static HttpClient Create() {
        var client = new HttpClient {
            // Imlight builds are ~260 MB; give a slow connection room to finish.
            Timeout = TimeSpan.FromMinutes(45),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ViewLight-Launcher/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
