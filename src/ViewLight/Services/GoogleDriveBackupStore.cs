using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ViewLight.Services;

/// <summary>
/// The backup, kept in the user's own Google Drive inside the app's private folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app's own folder, not the user's Drive.</b> Every call names <c>spaces=appDataFolder</c>: the folder
/// belongs to this application, the user cannot see it in the Drive interface, and nothing here can read or
/// write a single file outside it. That is the whole reason this scope was chosen over the broader ones - a
/// bug in this class cannot reach anything of the user's, however wrong it is.
/// </para>
/// <para>
/// <b>One file per name, found before it is written.</b> The Drive API has no "replace", so a write is a
/// search for the file of that name followed by an update of it - or a create when there is not one. The
/// search is by name inside the app folder, and there is only ever one of each, because this class is the
/// only thing that writes there.
/// </para>
/// <para>
/// <b>The access token is the caller's business.</b> How to get one is passed in rather than worked out here,
/// so this class never has to know whether the launcher is signed in, how its refresh token is kept, or how a
/// sign-in is started - and so it can be driven against a fake Drive with no Google in sight.
/// </para>
/// </remarks>
public sealed class GoogleDriveBackupStore : ICloudBackupStore {

    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";

    /// <summary>The folder every file goes in: the application's own, not the user's Drive.</summary>
    private const string AppDataFolder = "appDataFolder";

    private readonly Func<CancellationToken, Task<(GoogleTokens? Tokens, string? Problem)>> _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private GoogleTokens? _cached;

    /// <param name="tokens">
    /// How to get an access token: the launcher's sign-in, or a canned one in a test.
    /// </param>
    /// <param name="http">The client to call Drive with, or <c>null</c> for one of its own.</param>
    public GoogleDriveBackupStore(
        Func<CancellationToken, Task<(GoogleTokens? Tokens, string? Problem)>> tokens, HttpClient? http = null) {
        _tokens = tokens;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromHours(1) };
    }

    public string? Problem { get; private set; }

    /// <summary>Whether there is a working sign-in: an access token that can be had right now.</summary>
    public async Task<bool> IsSignedInAsync(CancellationToken token = default) {
        var access = await AccessTokenAsync(force: false, token).ConfigureAwait(false);
        return access is not null;
    }

    /// <summary>
    /// The named file's bytes, or <c>null</c> when it is not there.
    /// </summary>
    /// <remarks>
    /// Not there is not a problem, and <see cref="Problem"/> is left <c>null</c> for it: a Drive that has never
    /// been written to is the ordinary state of a first backup, and the sync reads the difference between
    /// "nothing up there" and "could not be reached" from that field.
    /// </remarks>
    public async Task<byte[]?> DownloadAsync(string name, CancellationToken token = default) {
        Problem = null;

        var access = await AccessTokenAsync(force: false, token).ConfigureAwait(false);
        if (access is null) {
            return null;
        }

        try {
            var id = await FindIdAsync(name, access, token).ConfigureAwait(false);
            if (id is null) {
                return null;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{FilesEndpoint}/{Uri.EscapeDataString(id)}?alt=media");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                Problem = $"Google Drive would not hand back {name} ({response.StatusCode}).";
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException) {
            Problem = $"Google Drive could not be reached ({ex.Message}).";
            return null;
        }
    }

    /// <summary>
    /// Whether the named file is there, asked of Drive's own index rather than by fetching it.
    /// </summary>
    /// <remarks>
    /// The search <see cref="FindIdAsync"/> already does, and nothing else, which is the point: the caller
    /// asks this about the archive, and answering it by downloading would cost tens of gigabytes to learn one
    /// bit. A search that failed sets <see cref="Problem"/> and answers <c>false</c> - and the caller reads
    /// that as "could not tell", because the one thing not to conclude from a Drive that would not answer is
    /// that there is no backup up there.
    /// </remarks>
    public async Task<bool> ExistsAsync(string name, CancellationToken token = default) {
        Problem = null;

        var access = await AccessTokenAsync(force: false, token).ConfigureAwait(false);
        if (access is null) {
            return false;
        }

        try {
            return await FindIdAsync(name, access, token).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException) {
            Problem = $"Google Drive could not be reached ({ex.Message}).";
            return false;
        }
    }

    /// <summary>Writes the named file, replacing it if it is already there.</summary>
    public async Task<bool> UploadAsync(string name, byte[] content, CancellationToken token = default) {
        Problem = null;

        var access = await AccessTokenAsync(force: false, token).ConfigureAwait(false);
        if (access is null) {
            return false;
        }

        try {
            var id = await FindIdAsync(name, access, token).ConfigureAwait(false);

            using var response = id is null
                ? await CreateAsync(name, content, access, token).ConfigureAwait(false)
                : await ReplaceAsync(id, content, access, token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) {
                return true;
            }

            Problem = $"Google Drive refused {name} ({(int) response.StatusCode}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException) {
            Problem = $"Google Drive could not be reached ({ex.Message}).";
            return false;
        }
    }

    /// <summary>Removes the named file. One that is not there is the outcome asked for.</summary>
    public async Task<bool> DeleteAsync(string name, CancellationToken token = default) {
        Problem = null;

        var access = await AccessTokenAsync(force: false, token).ConfigureAwait(false);
        if (access is null) {
            return false;
        }

        try {
            var id = await FindIdAsync(name, access, token).ConfigureAwait(false);
            if (id is null) {
                return true;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Delete, $"{FilesEndpoint}/{Uri.EscapeDataString(id)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound
                or HttpStatusCode.OK) {
                return true;
            }

            Problem = $"Google Drive refused to remove {name} ({(int) response.StatusCode}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException) {
            Problem = $"Google Drive could not be reached ({ex.Message}).";
            return false;
        }
    }

    /// <summary>
    /// The id of the named file in the app's folder, or <c>null</c> when there is not one.
    /// </summary>
    /// <remarks>
    /// The search is <c>spaces=appDataFolder</c> and a name, which is exact enough because only this
    /// application can put anything there. A name is escaped rather than interpolated, so a file called
    /// something with a quote in it cannot break the query.
    /// </remarks>
    private async Task<string?> FindIdAsync(string name, string access, CancellationToken token) {
        var query = $"name = '{EscapeForQuery(name)}' and trashed = false";
        var url = $"{FilesEndpoint}?spaces={AppDataFolder}&pageSize=1&fields=files(id,name)"
                  + $"&q={Uri.EscapeDataString(query)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            Problem = $"Google Drive could not be searched ({response.StatusCode}).";
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() == 0) {
            return null;
        }

        var first = files.EnumerateArray().First();

        return first.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    /// <summary>
    /// Makes a new file in the app's folder: its name and parent in one part, its bytes in the next.
    /// </summary>
    /// <remarks>
    /// One multipart request rather than a create followed by an upload, so a file is never briefly there with
    /// nothing in it - which the next sync would otherwise read as a backup of zero bytes.
    /// </remarks>
    private Task<HttpResponseMessage> CreateAsync(string name, byte[] content, string access, CancellationToken token) {
        const string boundary = "viewlight-backup-boundary";

        var metadata = new StringBuilder()
            .Append('{')
            .Append("\"name\":").Append(JsonSerializer.Serialize(name)).Append(',')
            .Append("\"parents\":[\"").Append(AppDataFolder).Append("\"]")
            .Append('}')
            .ToString();

        var body = new MemoryStream();

        void Write(string text) => body.Write(Encoding.UTF8.GetBytes(text));

        Write($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n");
        Write(metadata);
        Write($"\r\n--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\n");
        body.Write(content);
        Write($"\r\n--{boundary}--\r\n");

        body.Position = 0;

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{UploadEndpoint}?uploadType=multipart&fields=id");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content = new StreamContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/related") {
            Parameters = { new NameValueHeaderValue("boundary", boundary) },
        };

        return SendAndDisposeAsync(request, token);
    }

    /// <summary>Replaces an existing file's bytes, keeping its id and its place in the folder.</summary>
    private Task<HttpResponseMessage> ReplaceAsync(string id, byte[] content, string access, CancellationToken token) {
        var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{UploadEndpoint}/{Uri.EscapeDataString(id)}?uploadType=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return SendAndDisposeAsync(request, token);
    }

    /// <summary>Sends a request built with content and hands back just the response, disposing the request.</summary>
    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken token) {
        using (request) {
            return await _http.SendAsync(request, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An access token, refreshed if the one in hand has run out or a refresh was asked for.
    /// </summary>
    private async Task<string?> AccessTokenAsync(bool force, CancellationToken token) {
        if (!force && _cached is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow) {
            return cached.AccessToken;
        }

        var (tokens, problem) = await _tokens(token).ConfigureAwait(false);

        if (tokens is null) {
            Problem = problem ?? "Google Drive is not signed in.";
            return null;
        }

        _cached = tokens;
        return tokens.AccessToken;
    }

    /// <summary>A name with the two characters that would break a Drive query escaped.</summary>
    private static string EscapeForQuery(string name)
        => name.Replace("\\", "\\\\").Replace("'", "\\'");

    public void Dispose() {
        if (_ownsHttp) {
            _http.Dispose();
        }
    }
}
