using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ViewLight.Services;

/// <summary>The three things a signed-in Google session is: a live access token, and the way back to one.</summary>
/// <param name="AccessToken">The short-lived token that is sent on every Drive call.</param>
/// <param name="RefreshToken">The long-lived token that buys a new access token while nobody is watching.</param>
/// <param name="ExpiresAt">When the access token stops working, so it can be renewed before it does.</param>
public sealed record GoogleTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>A Google OAuth client, and the refresh token that stands for the signed-in account.</summary>
/// <param name="ClientId">The OAuth client's id.</param>
/// <param name="ClientSecret">
/// Its secret. Required for a Web-application client, which Google refuses every refresh from without one, and
/// not needed by a Desktop-app client, which binds the redirect to itself with a PKCE verifier instead.
/// </param>
/// <param name="RefreshToken">The account's refresh token, bought once by signing in.</param>
public sealed record GoogleCredentials(string ClientId, string? ClientSecret, string RefreshToken);

/// <summary>
/// What a token call answered: the tokens, or the sentence saying why not.
/// </summary>
/// <param name="Tokens">The tokens, or <c>null</c> when Google would not give any.</param>
/// <param name="Problem">Why not, in words, or <c>null</c> when there was no problem.</param>
/// <param name="SignInAgain">
/// Whether the stored sign-in is the thing that is wrong - an expired or revoked refresh token - which is the
/// one refusal that retrying cannot fix and that only signing in again can.
/// </param>
public sealed record GoogleAuth(GoogleTokens? Tokens, string? Problem, bool SignInAgain) {

    /// <summary>Google handed back tokens.</summary>
    public static GoogleAuth Granted(GoogleTokens tokens) => new(tokens, null, false);

    /// <summary>Google refused, with the reason to show and whether a new sign-in is what it takes.</summary>
    public static GoogleAuth Refused(string problem, bool signInAgain = false) => new(null, problem, signInAgain);
}

/// <summary>
/// Google's sign-in, done the way an installed application is meant to do it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The password never reaches the launcher.</b> Sign-in is Google's own page in the user's browser: the
/// launcher opens it, a loopback socket on this machine receives the one redirect, and the short-lived code in
/// that redirect is exchanged for tokens. Nothing the user types is seen here.
/// </para>
/// <para>
/// <b>The loopback is a raw socket, not <c>HttpListener</c>.</b> <c>HttpListener</c> goes through the
/// operating system's HTTP stack, which on Windows wants a URL reservation the launcher has no business
/// asking for; a plain <see cref="TcpListener"/> on <c>127.0.0.1</c> needs nothing. It answers what the browser
/// asks and stops at the one request that is the answer.
/// </para>
/// <para>
/// <b>PKCE, because the redirect is on a loopback.</b> Any process on this machine can reach a loopback port,
/// so the code in the redirect is not by itself proof that the right program got it. A verifier known only to
/// this process is sent with the exchange, and Google checks it - so a code intercepted by something else
/// cannot be traded for tokens. It is also why a Desktop-app client needs no secret: the verifier is what
/// binds the redirect to this process.
/// </para>
/// <para>
/// <b>Offline is the point.</b> <c>access_type=offline</c> is what makes Google return a refresh token, and a
/// refresh token is what lets the sync run while nobody is watching: an upload on a machine that was signed in
/// a month ago has no browser to send the user to.
/// </para>
/// <para>
/// <b>What is pasted is cleaned up before it is used.</b> A client id copied out of the Cloud console arrives
/// with a trailing space often enough to matter, and Google answers that with a flat 401 <c>invalid_client</c>
/// that reads like a sign-in that did not last rather than like a stray character - see <see cref="Normalize"/>.
/// And when Google does refuse, the refusal is turned into the sentence that names the cause, because the raw
/// body is a JSON fragment that says nothing a user can act on - see <see cref="Refused"/>.
/// </para>
/// </remarks>
public static class GoogleOAuth {

    /// <summary>
    /// The only scope the launcher ever asks for: a folder of its own inside the user's Drive.
    /// </summary>
    /// <remarks>
    /// <c>drive.appdata</c> gives the launcher a folder it can create, read, write and delete in that the user
    /// cannot see in the Drive interface and that cannot reach a single file outside it. It is deliberately
    /// not <c>drive.file</c> - which needs a file picker and grants per-file access - and not full
    /// <c>drive</c>, which is the user's whole Drive.
    /// </remarks>
    public const string Scope = "https://www.googleapis.com/auth/drive.appdata";

    /// <summary>
    /// The OAuth client id ViewLight signs in with when the built-in sign-in is chosen, or blank when this
    /// build ships none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blank until there is a client to name, and the built-in option is greyed out while it is.</b> The
    /// workflow below is complete and works today: a desktop OAuth client needs only its id, because the PKCE
    /// verifier stands in for a secret and nothing secret has to be shipped inside the application. What is
    /// missing is not code but the registration - a client id belongs to a Google Cloud project somebody owns,
    /// and this build has none. Putting a real id here is the whole of switching it on.
    /// </para>
    /// <para>
    /// <b>The redirect is a loopback port, which is what a desktop client is allowed.</b> There is no client
    /// secret in that flow by design: Google's own documentation for installed apps says a secret shipped in
    /// one is not secret, and PKCE - the verifier below - is what binds the redirect to this process instead.
    /// </para>
    /// </remarks>
    public const string BuiltInClientId = "";

    /// <summary>Whether this build has a built-in client to sign in with.</summary>
    public static bool HasBuiltInClient => Normalize(BuiltInClientId).Length > 0;

    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>How long the user is given to finish signing in before the wait gives up on its own.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// A pasted client id or secret with the whitespace a copy out of the Cloud console brings with it removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A trailing space is invisible in the settings box and is a 401 in Google's answer.</b> The id is sent
    /// inside a form body, where a space is part of the value as far as Google's lookup is concerned: the client
    /// is not found, and the answer is <c>invalid_client</c> - which is the same answer an expired sign-in gets,
    /// so it reads as "the sign-in did not last" and sends the user back to sign in again, which stores the same
    /// id with the same space and fails the same way next time. Stripping it here is what ends that loop.
    /// </para>
    /// <para>
    /// Quotation marks and the zero-width characters a web page can smuggle into a copy are removed for the same
    /// reason, and a non-breaking space is already whitespace as far as <see cref="char.IsWhiteSpace(char)"/> is
    /// concerned. No id or secret contains a space of its own, so nothing legitimate is lost.
    /// </para>
    /// </remarks>
    public static string Normalize(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        var cleaned = new StringBuilder(value.Length);

        foreach (var character in value) {
            if (char.IsWhiteSpace(character) || character is '"' or '\'' or '\u200B' or '\u200C' or '\u200D'
                or '\uFEFF') {
                continue;
            }

            cleaned.Append(character);
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Signs in: opens the browser, takes the one redirect on a loopback socket, and trades the code in.
    /// </summary>
    /// <param name="clientId">The OAuth client's id: the user's own, or the built-in one.</param>
    /// <param name="clientSecret">
    /// Its secret. A Web-application client cannot be refreshed without it and Google says so; a Desktop-app
    /// client has none to send.
    /// </param>
    /// <param name="openBrowser">
    /// How to send the user to the page. Passed in rather than done here, so this can be driven without a
    /// browser and so the view model decides how the launcher opens things.
    /// </param>
    /// <param name="timeout">How long to wait for the user to finish signing in.</param>
    /// <param name="token">
    /// What stops the wait. Cancelling it is how the user gets out of a sign-in they are no longer doing - a
    /// browser window closed without consenting leaves nothing to receive, and waiting out the clock is not an
    /// answer a person can be given.
    /// </param>
    /// <returns>
    /// The tokens, or <see cref="GoogleAuth.Refused"/> with the reason - so a caller that gets no tokens has a
    /// sentence to show rather than a failed sign-in to explain.
    /// </returns>
    public static async Task<GoogleAuth> SignInAsync(
        string? clientId, string? clientSecret, Action<string> openBrowser,
        TimeSpan? timeout = null, CancellationToken token = default) {

        var id = Normalize(clientId);
        var secret = Normalize(clientSecret);

        if (id.Length == 0) {
            return GoogleAuth.Refused("No Google client id is set, so there is nothing to sign in as.");
        }

        var verifier = CreateVerifier();
        var challenge = ChallengeFor(verifier);

        using var redirect = new LoopbackRedirect();

        var url = AuthorizationUrl(id, redirect.RedirectUri, challenge);

        try {
            openBrowser(url);
        }
        catch (Exception ex) {
            return GoogleAuth.Refused($"The sign-in page could not be opened ({ex.Message}).");
        }

        var code = await redirect.WaitForCodeAsync(timeout ?? DefaultTimeout, token).ConfigureAwait(false);

        if (code is null) {
            return GoogleAuth.Refused(redirect.Problem is { Length: > 0 } problem
                ? problem
                : "Signing in was not finished in time.");
        }

        return await ExchangeAsync(id, secret, code, verifier, redirect.RedirectUri, token)
            .ConfigureAwait(false);
    }

    /// <summary>The URL the browser is sent to.</summary>
    public static string AuthorizationUrl(string clientId, string redirectUri, string codeChallenge) {
        var query = new Dictionary<string, string> {
            ["client_id"] = Normalize(clientId),
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,

            // Offline is what makes Google hand back a refresh token, and the launcher has to work while
            // nobody is watching the browser.
            ["access_type"] = "offline",

            // Consent is asked for every time on purpose: signing in again has to produce a refresh token,
            // and Google omits one from a repeat sign-in that skips the consent screen.
            ["prompt"] = "consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        var pairs = query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        return $"{AuthorizationEndpoint}?{string.Join('&', pairs)}";
    }

    /// <summary>
    /// Trades a refresh token for a fresh access token, which is what an unattended sync runs on.
    /// </summary>
    /// <remarks>
    /// The client id and secret are cleaned up on the way in for the same reason the sign-in cleans them: they
    /// come back from the settings file exactly as they were pasted, and Google refuses a client id with a stray
    /// space as flatly as it refuses one that does not exist.
    /// </remarks>
    public static async Task<GoogleAuth> RefreshAsync(
        GoogleCredentials credentials, CancellationToken token = default) {

        var clientSecret = Normalize(credentials.ClientSecret);

        var form = new Dictionary<string, string> {
            ["client_id"] = Normalize(credentials.ClientId),
            ["refresh_token"] = credentials.RefreshToken,
            ["grant_type"] = "refresh_token",
        };

        // Sent when there is one, and absent rather than empty when there is not: a Web-application client has
        // to send its secret and Google refuses the refresh without it, while a Desktop-app client that sent an
        // empty one would be sending a secret Google never issued it.
        if (clientSecret.Length > 0) {
            form["client_secret"] = clientSecret;
        }

        var answer = await PostAsync(form, refreshing: true, token).ConfigureAwait(false);

        // Google does not repeat the refresh token on a refresh; the one that was used is still the one.
        return answer.Tokens is null
            ? answer
            : answer with { Tokens = answer.Tokens with { RefreshToken = credentials.RefreshToken } };
    }

    /// <summary>
    /// Google's refusal of a token call, said in a sentence that names the cause.
    /// </summary>
    /// <param name="status">The status Google answered with.</param>
    /// <param name="body">Google's JSON error body, which is where the useful part of the reason is.</param>
    /// <param name="refreshing">
    /// Whether this was a refresh of a stored sign-in rather than the exchange of one that just happened, which
    /// is what tells the two kinds of <c>invalid_grant</c> apart: a refresh token Google will not take has to be
    /// replaced by signing in, while a code that has already been used only has to be asked for again.
    /// </param>
    /// <remarks>
    /// The status alone is not enough to say anything useful: <c>invalid_client</c> is both "the client id has a
    /// space in it" and "the secret is missing", and both are 401s a user can do nothing with. The error code
    /// and its description are what separate them, so those are what this reads - and when neither says anything
    /// this build recognises, the raw line is kept in the sentence rather than dropped.
    /// </remarks>
    public static GoogleAuth Refused(HttpStatusCode status, string body, bool refreshing) {
        var code = ErrorValue(body, "error");
        var description = ErrorValue(body, "error_description");
        var detail = description.Length > 0 ? description : FirstLine(body);

        // The one refusal no retry and no correction fixes: Google has stopped honouring this account's sign-in.
        // It is the only case where the launcher is right to say the sign-in is not there any more.
        if (code == "invalid_grant") {
            return refreshing
                ? GoogleAuth.Refused(
                    "Google no longer accepts the sign-in kept for this account: it has expired, or it was "
                    + "revoked in the account's own security settings. Sign in again to keep backing up "
                    + $"({detail}).", signInAgain: true)
                : GoogleAuth.Refused(
                    $"Google refused the sign-in at the last step ({detail}). Press Sign in to Google Drive to "
                    + "start over.");
        }

        // A Web-application client has to send its secret on every token call, and Google names the missing
        // secret in words rather than in the code, so the words are what this reads.
        if (description.Contains("client_secret", StringComparison.OrdinalIgnoreCase)) {
            return GoogleAuth.Refused(
                "Google refused: this OAuth client needs its client secret. A Web-application client has to send "
                + "the secret on every call, and one that is missing or wrong is refused - so paste it into "
                + "Settings → Cloud backup beside the id. (A Desktop-app client would need none; that type is "
                + $"bound to the redirect by the PKCE verifier instead.) ({detail})");
        }

        if (code == "invalid_client") {
            return GoogleAuth.Refused(
                "Google did not recognise this client, so nothing could be signed in with it "
                + $"({detail}). Check the id against the one in the Cloud console - ViewLight strips the spaces "
                + "around what is pasted, but a client that was deleted has to be replaced with a new one.");
        }

        if (code == "redirect_uri_mismatch") {
            return GoogleAuth.Refused(
                "Google refused the address the browser was sent back to. A Web-application client only allows "
                + "the redirect addresses registered for it, so a loopback address has to be one of them "
                + $"({detail}).");
        }

        if (code == "access_denied") {
            return GoogleAuth.Refused("Signing in was cancelled at Google's own page, so nothing was saved.");
        }

        return GoogleAuth.Refused($"Google refused the sign-in ({(int) status}: {detail}).");
    }

    private static async Task<GoogleAuth> ExchangeAsync(
        string clientId, string clientSecret, string code, string verifier, string redirectUri,
        CancellationToken token) {

        var form = new Dictionary<string, string> {
            ["client_id"] = Normalize(clientId),
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        // Absent rather than empty for a client without one: the verifier is what proves this is the program
        // that started the sign-in, and a secret Google did not issue is a refused exchange.
        if (clientSecret.Length > 0) {
            form["client_secret"] = clientSecret;
        }

        return await PostAsync(form, refreshing: false, token).ConfigureAwait(false);
    }

    private static async Task<GoogleAuth> PostAsync(
        Dictionary<string, string> form, bool refreshing, CancellationToken token) {

        try {
            using var body = new FormUrlEncodedContent(form);
            using var response = await s_http.PostAsync(TokenEndpoint, body, token).ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                return Refused(response.StatusCode, text, refreshing);
            }

            using var json = JsonDocument.Parse(text);

            if (!json.RootElement.TryGetProperty("access_token", out var access)
                || access.ValueKind != JsonValueKind.String) {
                return GoogleAuth.Refused("Google answered without an access token.");
            }

            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var seconds)
                            && seconds.TryGetInt32(out var value)
                ? value
                : 3600;

            var refresh = json.RootElement.TryGetProperty("refresh_token", out var refreshToken)
                          && refreshToken.ValueKind == JsonValueKind.String
                ? refreshToken.GetString()
                : null;

            // Renewed a minute early so a token is never used right at the edge of its life.
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));

            return GoogleAuth.Granted(new GoogleTokens(access.GetString()!, refresh, expiresAt));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException) {
            return GoogleAuth.Refused($"Google could not be reached ({ex.Message}).");
        }
    }

    /// <summary>One string out of Google's error body, or an empty one when it is not there to read.</summary>
    private static string ErrorValue(string body, string name) {
        try {
            using var json = JsonDocument.Parse(body);

            return json.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException) {
            // Not JSON at all: a proxy's page, or an empty body. The caller falls back to the raw line.
            return string.Empty;
        }
    }

    /// <summary>The first line of an OAuth error body, which is a JSON object with one useful line in it.</summary>
    private static string FirstLine(string body) {
        var oneLine = string.Join(' ', body.Split('\n', '\r').Select(line => line.Trim())
            .Where(line => line.Length > 0));

        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "…";
    }

    /// <summary>A fresh PKCE verifier: 43+ characters of cryptographic randomness.</summary>
    private static string CreateVerifier()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The challenge: the verifier's SHA-256, base64url-encoded.</summary>
    private static string ChallengeFor(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A loopback socket that catches the redirect Google sends and ignores everything else the browser asks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bound to <c>127.0.0.1</c> on a port the operating system picks, so nothing is exposed off this machine
    /// and no fixed port has to be kept free.
    /// </para>
    /// <para>
    /// <b>More than one connection arrives, and only one of them is the answer.</b> A browser opens sockets it
    /// has nothing to say on - a preconnect, a probe, and the request for the icon beside the page - and a
    /// listener that treats the first connection as the one redirect takes one of those, finds no code in it,
    /// and then sits waiting for a consent that has already been given. So the listener stays open and reads one
    /// connection at a time until a request actually carries a code or an error: those other requests are
    /// answered so the browser is not left hanging, and dropped. A connection that never says anything is
    /// dropped too, rather than being waited on for the rest of the window.
    /// </para>
    /// <para>
    /// <b>And the whole wait can be given up on.</b> Closing the browser window leaves nothing to arrive and no
    /// way for the launcher to notice, so the caller's token is what ends it - the launcher offers a way out
    /// rather than making the user wait for a clock.
    /// </para>
    /// </remarks>
    private sealed class LoopbackRedirect : IDisposable {

        /// <summary>How long one connection is given to say something before it is dropped.</summary>
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

        private readonly TcpListener _listener;

        public LoopbackRedirect() {
            // Port 0: the operating system hands back a free one, which is what the redirect URL then names.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint) _listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public string RedirectUri => $"http://127.0.0.1:{Port}/";

        /// <summary>Why the wait ended without a code, when it did.</summary>
        public string? Problem { get; private set; }

        public async Task<string?> WaitForCodeAsync(TimeSpan timeout, CancellationToken token) {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);

            try {
                while (true) {
                    using var client = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                    await using var stream = client.GetStream();

                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    quiet.CancelAfter(Quiet);

                    string request;
                    try {
                        request = await ReadRequestLineAsync(stream, quiet.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) {
                        // A connection that opened and said nothing for ten seconds: a preconnect, or a probe.
                        // Dropping it is the point - the browser is still on its way to the real redirect.
                        continue;
                    }

                    var (code, error) = ParseRedirect(request);

                    if (code is null && error is null) {
                        // Not the answer: this is the icon the page asked for, or a request without a query.
                        // Answered politely and forgotten, so the redirect still on its way is not missed.
                        await WriteResponseAsync(stream, NotFound).ConfigureAwait(false);
                        continue;
                    }

                    await WriteResponseAsync(stream, code is not null ? SignedIn : Failed).ConfigureAwait(false);

                    if (error is { Length: > 0 }) {
                        Problem = DescribeRedirectError(error);
                        return null;
                    }

                    return code;
                }
            }
            catch (OperationCanceledException) {
                // Two ways to get here, and they are not the same thing to say: the user stopped the wait, or
                // the wait ran out on them.
                Problem = token.IsCancellationRequested
                    ? "Signing in was cancelled, so nothing was saved. Press Sign in to Google Drive to start "
                      + "again."
                    : "Signing in was not finished in time, so nothing was saved. Press Sign in to Google Drive "
                      + "to try again.";

                return null;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) {
                Problem = $"The sign-in redirect could not be received ({ex.Message}).";
                return null;
            }
        }

        /// <summary>What Google said when it sent the browser back without a code.</summary>
        private static string DescribeRedirectError(string error)
            => error switch {
                "access_denied" => "Signing in was cancelled at Google's own page, so nothing was saved.",
                "redirect_uri_mismatch" => "Google refused the address it was asked to send the browser back to: "
                                           + "this client does not allow a loopback address.",
                _ => $"Google refused the sign-in ({error}), so nothing was saved.",
            };

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken token) {
            var buffer = new byte[4096];
            var total = 0;

            // The first line ends at the first CRLF; the request line is all this needs.
            while (total < buffer.Length) {
                var read = await stream.ReadAsync(buffer.AsMemory(total, 1), token).ConfigureAwait(false);
                if (read == 0) {
                    break;
                }

                total++;

                if (total >= 2 && buffer[total - 2] == (byte) '\r' && buffer[total - 1] == (byte) '\n') {
                    break;
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        /// <summary>Pulls the code, or the error, out of <c>GET /?code=... HTTP/1.1</c>.</summary>
        private static (string? Code, string? Error) ParseRedirect(string requestLine) {
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) {
                return (null, null);
            }

            var path = parts[1];
            var queryStart = path.IndexOf('?');
            if (queryStart < 0) {
                return (null, null);
            }

            string? code = null;
            string? error = null;

            foreach (var pair in path[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)) {
                var equals = pair.IndexOf('=');
                if (equals <= 0) {
                    continue;
                }

                var key = Uri.UnescapeDataString(pair[..equals]);
                var value = Uri.UnescapeDataString(pair[(equals + 1)..]);

                if (key == "code") {
                    code = value;
                }
                else if (key == "error") {
                    error = value;
                }
            }

            return (code, error);
        }

        private const string SignedIn =
            "<!doctype html><meta charset=\"utf-8\"><title>ViewLight</title>"
            + "<p>ViewLight is signed in to Google Drive. You can close this tab and go back to the launcher.</p>";

        private const string Failed =
            "<!doctype html><meta charset=\"utf-8\"><title>ViewLight</title>"
            + "<p>Signing in did not finish. Go back to ViewLight and press Sign in to Google Drive again.</p>";

        /// <summary>A page for a request that is not the redirect, so the browser's own probe is answered.</summary>
        private const string NotFound =
            "<!doctype html><meta charset=\"utf-8\"><title>ViewLight</title><p>Nothing here.</p>";

        private static async Task WriteResponseAsync(NetworkStream stream, string body) {
            var bytes = Encoding.UTF8.GetBytes(body);

            var headers = "HTTP/1.1 200 OK\r\n"
                          + "Content-Type: text/html; charset=utf-8\r\n"
                          + $"Content-Length: {bytes.Length}\r\n"
                          + "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose() {
            try {
                _listener.Stop();
            }
            catch (Exception) {
                // A listener that will not stop is one that is already gone.
            }
        }
    }
}
