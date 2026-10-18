using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewLight.Services;

/// <summary>
/// The launcher's own database over HTTP: the documents its notes are kept in, and the pictures that are
/// stored with them.
/// </summary>
/// <remarks>
/// <para>
/// The same REST surface Imlight's player database is read and written through - a database is made with a
/// <c>PUT</c>, a document is written by id, and a listing comes back wrapped in <c>Results</c> - with
/// attachments on top of it, because a profile's picture belongs to the profile rather than to a folder
/// beside it.
/// </para>
/// <para>
/// Nothing here throws for an answer the server gave: a database that is down, a write it refused and a
/// picture that is not there are all answers, and every one of them leaves the launcher working. What happened
/// is kept in <see cref="Problem"/> for a caller that wants to say so, and the read is <c>null</c>, rather
/// than an exception that would have to be caught at every call site.
/// </para>
/// </remarks>
public sealed class LauncherDatabaseClient : IDisposable {

    /// <summary>Documents are read a page at a time, this many at a time - the same page the player database is read with.</summary>
    private const int PageSize = 512;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _port;
    private readonly string _databaseName;

    public LauncherDatabaseClient(int port, string? databaseName = null, HttpClient? http = null) {
        _ownsHttp = http is null;

        // A short timeout on purpose: this client is written to and read from while the launcher is holding
        // the UI, and a database that has stopped answering must not hold it - and the loopback client, for
        // the same reason the player database uses one: this database is started on a free port of this
        // machine's own loopback, so the system proxy has no business in the conversation and a machine with
        // no network at all must still be able to read its own notes.
        _http = http ?? LoopbackHttp.Client(TimeSpan.FromSeconds(8));
        _port = port;
        _databaseName = databaseName ?? LauncherDatabase.DatabaseName;
    }

    /// <summary>Why the last call did not do what it was asked to, or <c>null</c> when it did.</summary>
    public string? Problem { get; private set; }

    /// <summary>The database's name in the instance the launcher runs.</summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Makes sure the launcher's database exists, creating it if it does not.
    /// </summary>
    /// <remarks>
    /// Raven makes a database the first time a document store asks for it, which is how Imlight's own database
    /// appears. Asking here is what lets the launcher's notes be written before anything else has touched it.
    /// </remarks>
    public async Task<bool> EnsureDatabaseAsync(CancellationToken token = default) {
        Problem = null;

        try {
            if (await ExistsAsync(token).ConfigureAwait(false)) {
                return true;
            }

            var body = new JsonObject { ["DatabaseName"] = _databaseName };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http
                .PutAsync($"{BaseUrl}/admin/databases?name={Uri.EscapeDataString(_databaseName)}", content, token)
                .ConfigureAwait(false);

            // 201 is a database made; 409 is one that appeared between the two calls, which is the same thing.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict) {
                return true;
            }

            Problem = $"The launcher's own database could not be created ({response.StatusCode}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            Problem = $"{BaseUrl} did not answer as a database server ({ex.Message}).";
            return false;
        }
    }

    /// <summary>
    /// Every document the database holds, as read - including the database's own bookkeeping, which the
    /// caller tells apart by collection.
    /// </summary>
    /// <returns>The documents, or <c>null</c> when the database could not be read.</returns>
    public async Task<IReadOnlyList<JsonElement>?> ReadDocumentsAsync(CancellationToken token = default) {
        Problem = null;

        var documents = new List<JsonElement>();

        try {
            for (var start = 0; ; start += PageSize) {
                using var response = await _http
                    .GetAsync($"{BaseUrl}/databases/{_databaseName}/docs?start={start}&pageSize={PageSize}", token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) {
                    Problem = $"The launcher's own database could not be read ({response.StatusCode}).";
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

                if (!json.RootElement.TryGetProperty("Results", out var results)
                    || results.ValueKind != JsonValueKind.Array) {
                    Problem = "The launcher's own database answered with something that is not a document listing.";
                    return null;
                }

                var read = 0;
                foreach (var document in results.EnumerateArray()) {
                    // The document outlives the JsonDocument it was parsed from, so it is cloned.
                    documents.Add(document.Clone());
                    read++;
                }

                if (read < PageSize) {
                    return documents;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            Problem = $"{BaseUrl} did not answer as a database server ({ex.Message}).";
            return null;
        }
    }

    /// <summary>One document by id, without reading every other document - or <c>null</c> when it is not there.</summary>
    public async Task<JsonElement?> ReadDocumentAsync(string documentId, CancellationToken token = default) {
        var single = await ReadOneAsync(documentId, token).ConfigureAwait(false);
        return single;
    }

    /// <summary>Writes a document, replacing whatever was there.</summary>
    public async Task<bool> WriteDocumentAsync(
        string documentId, JsonObject document, CancellationToken token = default) {

        Problem = null;

        try {
            using var content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http
                .PutAsync($"{BaseUrl}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(documentId)}",
                    content, token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) {
                return true;
            }

            // The database's own reason rather than the status alone, clipped to the line that says what is
            // wrong: Raven answers a refused write with a paragraph of its own diagnostics.
            var reason = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Problem = $"The launcher's own database refused a write ({(int) response.StatusCode}: {Clipped(reason)}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            Problem = $"The launcher's own database refused a write ({ex.Message}).";
            return false;
        }
    }

    /// <summary>Deletes a document. One that is already gone is the outcome asked for.</summary>
    public async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken token = default) {
        Problem = null;

        try {
            using var response = await _http
                .DeleteAsync($"{BaseUrl}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(documentId)}", token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.OK) {
                return true;
            }

            Problem = $"The launcher's own database refused a deletion ({response.StatusCode}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            Problem = $"The launcher's own database refused a deletion ({ex.Message}).";
            return false;
        }
    }

    /// <summary>
    /// Stores an attachment on a document - a profile's picture, kept with the profile rather than beside it.
    /// </summary>
    /// <remarks>
    /// An attachment rather than a file in a folder, because a backup of the launcher's data is the database's
    /// directory and nothing else: a picture left outside it is a picture the backup does not have, and the
    /// one thing that cannot be fetched again from anywhere.
    /// </remarks>
    public async Task<bool> PutAttachmentAsync(
        string documentId, string name, byte[] content, CancellationToken token = default) {

        Problem = null;

        try {
            using var body = new ByteArrayContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _http
                .PutAsync($"{BaseUrl}/databases/{_databaseName}/attachments?id={Uri.EscapeDataString(documentId)}"
                          + $"&name={Uri.EscapeDataString(name)}", body, token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) {
                return true;
            }

            var reason = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Problem = $"The launcher's own database refused a picture ({(int) response.StatusCode}: {Clipped(reason)}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            Problem = $"The launcher's own database refused a picture ({ex.Message}).";
            return false;
        }
    }

    /// <summary>A document's attachment, or <c>null</c> when there is not one.</summary>
    public async Task<byte[]?> ReadAttachmentAsync(
        string documentId, string name, CancellationToken token = default) {

        Problem = null;

        try {
            using var response = await _http
                .GetAsync($"{BaseUrl}/databases/{_databaseName}/attachments?id={Uri.EscapeDataString(documentId)}"
                          + $"&name={Uri.EscapeDataString(name)}", token)
                .ConfigureAwait(false);

            // Nothing there is not a problem: a profile that has never had a picture is the ordinary case.
            if (response.StatusCode == HttpStatusCode.NotFound) {
                return null;
            }

            if (!response.IsSuccessStatusCode) {
                Problem = $"The launcher's own database could not hand back a picture ({response.StatusCode}).";
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            Problem = $"The launcher's own database could not hand back a picture ({ex.Message}).";
            return null;
        }
    }

    /// <summary>Removes a document's attachment. One that is not there is the outcome asked for.</summary>
    public async Task<bool> DeleteAttachmentAsync(
        string documentId, string name, CancellationToken token = default) {

        Problem = null;

        try {
            using var response = await _http
                .DeleteAsync($"{BaseUrl}/databases/{_databaseName}/attachments?id={Uri.EscapeDataString(documentId)}"
                            + $"&name={Uri.EscapeDataString(name)}", token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.OK) {
                return true;
            }

            Problem = $"The launcher's own database refused to remove a picture ({response.StatusCode}).";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            Problem = $"The launcher's own database refused to remove a picture ({ex.Message}).";
            return false;
        }
    }

    private string BaseUrl => PlayerDatabaseClient.BaseUrl(_port);

    private async Task<bool> ExistsAsync(CancellationToken token) {
        try {
            using var response = await _http
                .GetAsync($"{BaseUrl}/databases/{_databaseName}/docs?pageSize=1", token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return false;
        }
    }

    private async Task<JsonElement?> ReadOneAsync(string documentId, CancellationToken token) {
        Problem = null;

        try {
            using var response = await _http
                .GetAsync($"{BaseUrl}/databases/{_databaseName}/docs?id={Uri.EscapeDataString(documentId)}", token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                Problem = $"The launcher's own database could not be read ({response.StatusCode}).";
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("Results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0) {
                return null;
            }

            return results.EnumerateArray().First().Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or NotSupportedException) {
            Problem = $"{BaseUrl} did not answer as a database server ({ex.Message}).";
            return null;
        }
    }

    /// <summary>The first line of an error body, for a sentence in the Log.</summary>
    private static string Clipped(string reason) {
        var oneLine = string.Join(' ', reason.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));

        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    public void Dispose() {
        if (_ownsHttp) {
            _http.Dispose();
        }
    }
}
