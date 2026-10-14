using System.Net;

namespace ViewLight.Tests;

/// <summary>Lets tests drive <see cref="HttpClient"/> without touching the network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler {

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    public List<string> Requests { get; } = [];

    public static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new FakeHttpHandler(responder));

    public static HttpClient Serving(byte[] payload)
        => Client(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(payload),
        });

    public static HttpClient Returning(HttpStatusCode status)
        => Client(_ => new HttpResponseMessage(status) { Content = new StringContent(string.Empty) });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
        Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
        return Task.FromResult(_responder(request));
    }
}
