namespace ViewLight.Services;

/// <summary>
/// Whether a running patch server can serve this session's revision yet.
/// </summary>
/// <remarks>
/// <para>
/// The one question the launcher asks a patch server about its files, and it asks it because a patch
/// server that has just started cannot answer it. Aurorium writes the revision and its whole asset list
/// into its database <i>before</i> it downloads a single asset, so between "the port is open" and "the
/// files are on disk" every request for a file is a <c>404</c> - and Imlight asks for
/// <c>Data/GameData/Root.wad</c> during its own boot, before anything else. That 404 is what stops a
/// server; it is not a patch server that is broken, it is a first fetch that has not finished.
/// </para>
/// <para>
/// <b>The file Imlight asks for first is the question.</b> Asking about <c>LatestFileList</c> would answer
/// nothing useful, because Aurorium serves it straight from the requested revision's folder without
/// consulting the database - it is there while the assets are not. Asking about a revision's own
/// <c>Root.wad</c> is the same request Imlight makes, so an answer of "yes" means the boot the launcher is
/// about to start will get its files. The body is never read: the status is the whole answer, and it is
/// 281 MB.
/// </para>
/// <para>
/// <b>Nothing is repointed on the strength of it.</b> The answer is reported and remembered - it is what
/// tells the launcher whether the patch server may be stopped, since stopping a fetching one stops the
/// only thing that will ever make it useful - but Imlight keeps the address the launcher was given.
/// </para>
/// </remarks>
public sealed class PatchServerServing {

    /// <summary>The file Imlight loads first, and the one a session cannot start without.</summary>
    public const string BootFile = "Data/GameData/Root.wad";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public PatchServerServing(HttpClient? http = null, TimeSpan? timeout = null) {
        // Http.Client asks for GitHub's media type, which has no business being sent to a patch server.
        _http = http ?? Http.Files;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Whether the patch server at <paramref name="endpoint"/> answers for
    /// <paramref name="gameRevision"/>'s <c>Root.wad</c>.
    /// </summary>
    /// <returns>
    /// <c>true</c> only for an answer that carries the file. A patch server that is not up, not on this
    /// address, holding another revision, or still fetching this one, all answer "not yet" - and the caller
    /// has no use for a sentence about which of those it was.
    /// </returns>
    public async Task<bool> IsServingAsync(
        string? endpoint, string? gameRevision, CancellationToken cancellationToken = default) {

        var url = BootFileUrlFor(endpoint, gameRevision);
        if (url is null) {
            return false;
        }

        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            // Headers only: the answer is the status, and reading the rest would be 281 MB of archive.
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception) {
            // Refused, unreachable, timed out, or an answer that is not HTTP. All mean "not serving yet".
            return false;
        }
    }

    /// <summary>
    /// The URL of the file Imlight loads first: the one it builds itself, from the address it was given
    /// and the revision in <c>GameRevision</c>, or <c>null</c> when either of those is missing.
    /// </summary>
    /// <remarks>
    /// The revision segment is <c>V_</c> and the game revision, the way Imlight builds it - a value that
    /// already carries the prefix is not given a second one.
    /// </remarks>
    public static string? BootFileUrlFor(string? endpoint, string? gameRevision) {
        var baseUrl = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        var revision = (gameRevision ?? string.Empty).Trim();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _) || revision.Length == 0) {
            return null;
        }

        var name = revision.StartsWith("V_", StringComparison.OrdinalIgnoreCase) ? revision : $"V_{revision}";

        return $"{baseUrl}/{name}/{BootFile}";
    }
}
