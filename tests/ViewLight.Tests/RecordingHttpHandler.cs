using System.Net;
using System.Text;

namespace ViewLight.Tests;

/// <summary>
/// An HTTP handler that writes down what was asked for and answers with what the test says.
/// </summary>
/// <remarks>
/// <see cref="FakeHttpHandler"/> answers without looking at the request, which is all most tests need. These
/// ones are about what the client asked for - the url, the method, the body - and about answers that depend on
/// which document was asked for, so the request is recorded whole and the body is read the way a real handler
/// would: asynchronously, without blocking anything.
/// </remarks>
internal sealed class RecordingHttpHandler : HttpMessageHandler {

    private readonly Func<RecordedRequest, int, HttpResponseMessage> _responder;

    public RecordingHttpHandler(Func<RecordedRequest, int, HttpResponseMessage> responder)
        => _responder = responder;

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpClient Client(Func<RecordedRequest, int, HttpResponseMessage> responder)
        => new(new RecordingHttpHandler(responder));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {

        // Read as bytes rather than as text: half of what goes through here is a picture, and decoding it as
        // a string to write it down would be a recording that no longer holds what was sent.
        var content = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            Encoding.UTF8.GetString(content),
            content);

        Requests.Add(recorded);

        return _responder(recorded, Requests.Count);
    }
}

/// <summary>One request, as it reached the handler.</summary>
internal sealed record RecordedRequest(string Method, string Url, string Body, byte[] Content) {

    /// <summary>Where the request was pointed, without the host - which is what the assertions are about.</summary>
    public string Path => Url[(Url.IndexOf('/', Url.IndexOf("//", StringComparison.Ordinal) + 2))..];

    /// <summary>The value of one query parameter, un-escaped, or null when it was not asked for.</summary>
    public string? Query(string name) {
        foreach (var pair in new Uri(Url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var at = pair.IndexOf('=');
            var key = at < 0 ? pair : pair[..at];

            if (key == name) {
                return Uri.UnescapeDataString(at < 0 ? pair : pair[(at + 1)..]);
            }
        }

        return null;
    }
}
