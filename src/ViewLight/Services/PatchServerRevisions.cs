using System.Text.Json;

namespace ViewLight.Services;

/// <summary>
/// The revisions a running patch server holds, asked of its own API.
/// </summary>
/// <remarks>
/// <para>
/// One question, and one request: <c>GET /revisions</c>, answered by Aurorium with a JSON array of names
/// like <c>V_r806919.Wizard_1_610</c>. The only thing the launcher does with the answer is decide which
/// revision a session should ask for - see <see cref="PatchServerConfig.NewestGameRevision"/> and
/// <c>MainWindowViewModel.RevisionToMirror</c>.
/// </para>
/// <para>
/// <b>Nothing here judges what is behind the address.</b> Whether the files for a revision have finished
/// arriving is the patch server's business, not the launcher's: the launcher gives the address it was told
/// to give, and a fetch that is still running shows up as the patch server failing to load <c>Root.wad</c>
/// rather than as the launcher second-guessing the address it wrote.
/// </para>
/// </remarks>
public sealed class PatchServerRevisions {

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public PatchServerRevisions(HttpClient? http = null, TimeSpan? timeout = null) {
        // Http.Client asks for GitHub's media type, which has no business being sent to a patch server.
        _http = http ?? Http.Files;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// The revisions it holds, in the order it lists them, or an empty list when it did not answer.
    /// </summary>
    /// <returns>
    /// The names, or nothing at all. A patch server that is not up has nothing to say about a revision,
    /// and the caller has no use for a sentence about why - so an unreachable or unreadable answer is an
    /// empty list rather than an error.
    /// </returns>
    public async Task<IReadOnlyList<string>> ListAsync(
        string? endpoint, CancellationToken cancellationToken = default) {

        var baseUrl = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) {
            return [];
        }

        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            using var response = await _http
                .GetAsync($"{baseUrl}/revisions", HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array) {
                return [];
            }

            var revisions = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray()) {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } name) {
                    revisions.Add(name);
                }
            }

            return revisions;
        }
        catch (Exception) {
            // Refused, unreachable, timed out, or not the JSON this expects. All mean "not answering".
            return [];
        }
    }
}
