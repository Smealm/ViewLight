using System.Net;
using System.Net.Sockets;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Google's sign-in, checked against what it sends and what it says rather than against Google.
/// </summary>
/// <remarks>
/// <para>
/// Three things here are the ones a user meets: a client id pasted out of the Cloud console with a space on the
/// end, which Google answers with a 401 that reads like a sign-in that did not last; a Web-application client,
/// which Google will not refresh without its secret; and a sign-in whose browser window was closed, which used
/// to leave the launcher waiting for something that was never going to arrive.
/// </para>
/// <para>
/// The loopback is a real socket in these tests, because that is where the waiting happens: the tests open
/// connections to the port the launcher names and send what a browser sends. Only the very last step - the
/// exchange of the code with Google - is left to the network, and what is asserted about it is that the code
/// was received at all.
/// </para>
/// </remarks>
public class GoogleSignInTests {

    /// <summary>The id as it was actually stored on this machine: a real one, with a real trailing space.</summary>
    private const string PastedId = "311793391491-1us50odnkem8l35lo5922e3rjqsa9a0e.apps.googleusercontent.com ";

    [Fact]
    public void APastedClientIdLosesTheWhitespaceGoogleRefuses() {
        // The space is invisible in the settings box and Google's lookup treats it as part of the id, so the
        // client is not found and the answer is the same 401 an expired sign-in gets.
        Assert.Equal(PastedId.TrimEnd(), GoogleOAuth.Normalize(PastedId));
        Assert.NotEqual(PastedId, GoogleOAuth.Normalize(PastedId));
        Assert.DoesNotContain(' ', GoogleOAuth.Normalize(PastedId));

        // A newline, the quotes a copy can pick up, a non-breaking space and a zero-width space: all of them
        // are invisible and all of them are refused the same way.
        Assert.Equal("client.apps.googleusercontent.com",
            GoogleOAuth.Normalize("\r\nclient.apps.googleusercontent.com\u00A0\u200B"));

        Assert.Equal("secret", GoogleOAuth.Normalize("\"secret\"\n"));
        Assert.Equal("secret", GoogleOAuth.Normalize(" secret "));

        // And nothing of the id itself is lost: a real one has no spaces in it to lose.
        Assert.Equal("311793391491-1us50odnkem8l35lo5922e3rjqsa9a0e.apps.googleusercontent.com",
            GoogleOAuth.Normalize(PastedId));
    }

    [Fact]
    public void AClientThatIsOnlyWhitespaceIsNoClientAtAll() {
        // What the button and the head of the sign-in both read: a box holding a space is an empty box, so the
        // sign-in is refused with a sentence rather than sent off to Google to fail.
        Assert.Equal(string.Empty, GoogleOAuth.Normalize("   "));
        Assert.Equal(string.Empty, GoogleOAuth.Normalize(null));
        Assert.False(GoogleOAuth.HasBuiltInClient);
    }

    [Fact]
    public void TheAuthorizationUrlCarriesTheCleanedIdRatherThanThePastedOne() {
        var url = GoogleOAuth.AuthorizationUrl(PastedId, "http://127.0.0.1:51234/", "a-challenge");

        Assert.Contains("client_id=311793391491-1us50odnkem8l35lo5922e3rjqsa9a0e.apps.googleusercontent.com",
            url);
        Assert.DoesNotContain("%20", url);
        Assert.DoesNotContain("+ ", url);
    }

    [Fact]
    public void AMissingSecretIsSaidToBeAWebClientsSecret() {
        // Google's own words for the refusal a Web-application client gets without its secret. The error code
        // alone - invalid_request, 400 - says nothing a user can act on, so the description is what is read.
        var answer = GoogleOAuth.Refused(
            HttpStatusCode.BadRequest,
            """{"error": "invalid_request", "error_description": "client_secret is missing."}""",
            refreshing: true);

        Assert.Null(answer.Tokens);
        Assert.Contains("client secret", answer.Problem);
        Assert.Contains("Web-application client", answer.Problem);
        Assert.Contains("Settings", answer.Problem);

        // Not a sign-in that has to be redone: the sign-in is fine, the credentials it is sent with are not.
        Assert.False(answer.SignInAgain);
    }

    [Fact]
    public void AnUnknownClientIsNotASignInThatHasToBeRedone() {
        // The 401 a trailing space earns, which is the one that used to send the user back to sign in again -
        // storing the same id with the same space and failing the same way next time.
        var answer = GoogleOAuth.Refused(
            HttpStatusCode.Unauthorized,
            """{"error": "invalid_client", "error_description": "The OAuth client was not found."}""",
            refreshing: true);

        Assert.Null(answer.Tokens);
        Assert.Contains("did not recognise this client", answer.Problem);
        Assert.Contains("Cloud console", answer.Problem);
        Assert.False(answer.SignInAgain);
    }

    [Fact]
    public void AnExpiredSignInIsTheOneThingThatHasToBeRedone() {
        // A refresh token Google has stopped honouring. It is the only refusal a retry cannot fix, which is what
        // makes this the state the card has to stop claiming to be signed in for.
        var answer = GoogleOAuth.Refused(
            HttpStatusCode.BadRequest,
            """{"error": "invalid_grant", "error_description": "Token has been expired or revoked."}""",
            refreshing: true);

        Assert.Null(answer.Tokens);
        Assert.True(answer.SignInAgain);
        Assert.Contains("expired", answer.Problem);
        Assert.Contains("Sign in again", answer.Problem);
        Assert.Contains("Token has been expired or revoked.", answer.Problem);
    }

    [Fact]
    public void ACodeThatIsRefusedIsAskedForAgainRatherThanSignedInAgain() {
        // The same error code, one step earlier: the code the browser brought back has been used or has run out,
        // so the answer is to start the sign-in again - not to replace a sign-in that is perfectly good.
        var answer = GoogleOAuth.Refused(
            HttpStatusCode.BadRequest,
            """{"error": "invalid_grant", "error_description": "Bad Request"}""",
            refreshing: false);

        Assert.False(answer.SignInAgain);
        Assert.Contains("start over", answer.Problem);
    }

    [Fact]
    public void ARefusalInWordsNobodyRecognisesKeepsGoogleSaid() {
        // A proxy's page, or an error this build has not seen: the raw line is kept in the sentence rather than
        // swallowed, so the Log has something to go on.
        var answer = GoogleOAuth.Refused(HttpStatusCode.InternalServerError, "upstream is unwell", refreshing: true);

        Assert.Contains("500", answer.Problem);
        Assert.Contains("upstream is unwell", answer.Problem);
    }

    [Fact]
    public async Task TheWaitEndsWhenItIsCancelledRatherThanRunningOutTheClock() {
        // The softlock: the browser window is closed, nothing arrives on the loopback, and the launcher sat
        // waiting for a sign-in that was never going to finish - with no way back to the button that started it.
        // Cancelling is that way back, and it has to end the wait at once rather than at the timeout.
        using var cancel = new CancellationTokenSource();

        var signIn = GoogleOAuth.SignInAsync(
            "client.apps.googleusercontent.com", null, _ => { }, TimeSpan.FromMinutes(5), cancel.Token);

        // A moment for the socket to be listening, and then the user gives up on it.
        await Task.Delay(200);
        cancel.Cancel();

        var answer = await signIn;

        Assert.Null(answer.Tokens);
        Assert.Contains("cancelled", answer.Problem);
        Assert.Contains("Sign in to Google Drive", answer.Problem);
        Assert.False(answer.SignInAgain);
    }

    [Fact]
    public async Task AWaitNobodyFinishesEndsOnItsOwnAndSavesNothing() {
        var answer = await GoogleOAuth.SignInAsync(
            "client.apps.googleusercontent.com", null, _ => { }, TimeSpan.FromMilliseconds(300));

        Assert.Null(answer.Tokens);
        Assert.Contains("not finished in time", answer.Problem);
        Assert.Contains("nothing was saved", answer.Problem);

        // And it says how to start again, which is the only thing left to do at that point.
        Assert.Contains("Sign in to Google Drive", answer.Problem);
    }

    [Fact]
    public async Task AStrayRequestFromTheBrowserDoesNotTakeThePlaceOfTheRedirect() {
        // Browsers open connections of their own beside the page: a preconnect, a probe, and the request for the
        // icon. A listener that answers the first one and stops finds no code in it and then waits for a consent
        // that has already been given - which is the other half of the same softlock, and the half that arrives
        // while the user has done nothing wrong.
        var answer = await GoogleOAuth.SignInAsync(
            "client.apps.googleusercontent.com", null,
            url => {
                var port = new Uri(RedirectUriOf(url)).Port;

                _ = Task.Run(async () => {
                    // The icon the page asks for, which arrives first and carries no code.
                    await ConnectAndAskAsync(port, "GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                    await Task.Delay(50);

                    // The redirect itself, on a connection of its own.
                    await ConnectAndAskAsync(port, "GET /?code=a-real-code HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                });
            },
            TimeSpan.FromSeconds(30));

        // The code was taken - so what came back is Google's answer to the exchange or a network that is not
        // there, and never the loopback giving up on a request that was not the redirect.
        Assert.Null(answer.Tokens);
        Assert.NotNull(answer.Problem);
        Assert.DoesNotContain("not finished in time", answer.Problem);
        Assert.DoesNotContain("could not be received", answer.Problem);
    }

    /// <summary>The address the browser is told to come back to, out of the URL the launcher opened.</summary>
    private static string RedirectUriOf(string authorizationUrl) {
        foreach (var pair in authorizationUrl[(authorizationUrl.IndexOf('?') + 1)..].Split('&')) {
            if (pair.StartsWith("redirect_uri=", StringComparison.Ordinal)) {
                return Uri.UnescapeDataString(pair["redirect_uri=".Length..]);
            }
        }

        throw new InvalidOperationException("the sign-in url names no redirect");
    }

    /// <summary>Opens one connection the way a browser would and reads the answer back.</summary>
    private static async Task ConnectAndAskAsync(int port, string request) {
        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();

        // Read whatever comes back so the write above is not left half-sent, and at least one byte rather than
        // an exact count: the answer is a page the launcher writes in two pieces, and its size is not the point.
        var buffer = new byte[512];
        await stream.ReadAtLeastAsync(buffer, minimumBytes: 1);
    }
}
