namespace ViewLight.Services;

/// <summary>
/// One gamerpic in the archive: what it belongs to, and where the picture is.
/// </summary>
/// <param name="TitleId">The Xbox 360 title id the gamerpic belongs to, as the archive records it.</param>
/// <param name="Title">The game or pack it came from, cleaned up - the archive's own display name.</param>
/// <param name="Category">The archive's own grouping: Games, Promotional Content, Magazines and so on.</param>
/// <param name="Url">Where the picture is fetched from.</param>
public sealed record Gamerpic(string TitleId, string Title, string Category, string Url) {

    /// <summary>
    /// What the browser calls it: the title, and the pack name it lives in when there is one.
    /// </summary>
    /// <remarks>
    /// Most entries are named after the file the picture was dumped as - <c>28002</c>, <c>2040a</c> - which
    /// says nothing, so it is left out. The ones that are named are worth reading, and they are the ones the
    /// search should find.
    /// </remarks>
    public string SearchText { get; } = $"{Title} {Category} {TitleId}";

    /// <summary>What a grid cell shows: the title, trimmed so a long one does not run off the card.</summary>
    public string Caption => Title;
}

/// <summary>
/// The Xbox 360 gamerpic archive this launcher browses for profile pictures.
/// </summary>
/// <remarks>
/// <para>
/// <b>An index, then the pictures.</b> The archive is a repository of around 32,500 gamerpics and it
/// publishes a CSV naming every one of them, so browsing is one small download rather than a walk of the
/// repository's tree: it is read once, kept beside the launcher's other fetched art, and searched in
/// memory from then on. Only the pictures actually looked at are fetched, and each of those is kept too.
/// </para>
/// <para>
/// <b>Pictures come from the repository, not from the site the CSV points at.</b> The CSV records the
/// address the pictures were originally published at, and that host no longer resolves - so the file's own
/// path inside the repository is what is used, which is the same picture and is still served. Nothing is
/// guessed: the path is the one the CSV itself holds, with its separators turned the right way round.
/// </para>
/// <para>
/// <b>The archive's own names are ugly and that is not repaired here.</b> Folders carry trailing
/// underscores and doubled spaces where the site being backed up mangled a title, and rewriting them would
/// mean inventing a name for somebody else's file. The display name is the CSV's own title field, which is
/// clean; the path is used exactly as given.
/// </para>
/// </remarks>
public sealed class GamerpicArchive {

    /// <summary>The repository's listing of every gamerpic it holds.</summary>
    public const string IndexUrl = "https://raw.githubusercontent.com/Smealm/xbox-stuff/main/images.csv";

    /// <summary>Where the pictures are served from, once the path inside the repository is known.</summary>
    public const string RepositoryRawRoot = "https://raw.githubusercontent.com/Smealm/xbox-stuff/main/";

    /// <summary>The column names the index starts with, which is what says it is the file it should be.</summary>
    private static readonly string[] s_header = ["title_id", "title_name", "image_url", "category", "local_path"];

    private readonly HttpClient _http;
    private readonly string _indexPath;
    private readonly ImageCache _pictures;

    public GamerpicArchive(HttpClient? http = null, string? indexPath = null, string? pictureDirectory = null) {
        _http = http ?? Http.Files;
        _indexPath = indexPath ?? LauncherPaths.GamerpicIndexFile;
        _pictures = new ImageCache(pictureDirectory ?? LauncherPaths.GamerpicDirectory, http);
    }

    /// <summary>Why the last read did not work, or <c>null</c> when it did.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// The whole archive, read from the copy on this machine or fetched and kept if there is not one.
    /// </summary>
    /// <returns>
    /// The entries, or an empty list with the reason in <see cref="Problem"/>. The copy on disk is used
    /// whether or not the network answers, so a launcher that has browsed once can browse offline.
    /// </returns>
    public async Task<IReadOnlyList<Gamerpic>> LoadAsync(CancellationToken token = default) {
        Problem = null;

        var text = ReadCached();

        if (text is null) {
            text = await DownloadIndexAsync(token).ConfigureAwait(false);

            if (text is null) {
                Problem = "The gamerpic archive could not be read. Check the connection and try again.";
                return [];
            }

            WriteCached(text);
        }

        var entries = Parse(text);

        if (entries.Count == 0) {
            Problem = "The gamerpic archive listed nothing this launcher could read.";
        }

        return entries;
    }

    /// <summary>Where a gamerpic is fetched from, from the path the index records.</summary>
    public static string UrlFor(string localPath)
        => RepositoryRawRoot + string.Join('/', localPath.Replace('\\', '/').Split('/', StringSplitOptions.None)
            .Select(Uri.EscapeDataString));

    /// <summary>
    /// The archive's listing, parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the right rather than naively, because a title is free text: it can hold a comma, and one
    /// does. The last three fields - the address, the category and the path - cannot, so the line is split
    /// on the first field and the last three, and whatever is in between is the title. A line with too few
    /// fields is skipped rather than half-read.
    /// </para>
    /// <para>
    /// Quotes are stripped where the file uses them, and nothing else is cleaned: the title is presented as
    /// the archive spells it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Gamerpic> Parse(string csv) {
        var entries = new List<Gamerpic>();
        var lines = csv.Split('\n');

        for (var index = 0; index < lines.Length; index++) {
            var line = lines[index].TrimEnd('\r').Trim();

            if (line.Length == 0) {
                continue;
            }

            var fields = SplitFields(line);

            if (index == 0 && IsHeader(fields)) {
                continue;
            }

            if (fields.Count < 5) {
                continue;
            }

            var titleId = fields[0];
            var category = fields[^2];
            var path = fields[^1];

            if (titleId.Length == 0 || path.Length == 0) {
                continue;
            }

            // Everything between the id and the three fixed fields at the end is the title.
            var title = string.Join(",", fields.Skip(1).Take(fields.Count - 4)).Trim();

            entries.Add(new Gamerpic(
                titleId,
                title.Length == 0 ? titleId : title,
                category,
                UrlFor(path)));
        }

        return entries;
    }

    /// <summary>One line split into its fields, with the quotes a CSV may carry taken off.</summary>
    private static List<string> SplitFields(string line)
        => [.. SplitOutsideQuotes(line).Select(field => field.Trim().Trim('"').Trim())];

    /// <summary>
    /// Splits on commas that are not inside quotes.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full CSV reader: this file is generated, its quoting is simple, and a title that
    /// contains a comma inside quotes is the only thing that could go wrong - which this handles. Anything
    /// more elaborate would be writing a parser for a format that is not actually being used.
    /// </remarks>
    private static IEnumerable<string> SplitOutsideQuotes(string line) {
        var field = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var character in line) {
            switch (character) {
                case '"':
                    quoted = !quoted;
                    field.Append(character);
                    break;
                case ',' when !quoted:
                    yield return field.ToString();
                    field.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        yield return field.ToString();
    }

    /// <summary>Whether the first line is the file's own header rather than an entry.</summary>
    private static bool IsHeader(IReadOnlyList<string> fields)
        => fields.Count >= 5
           && string.Equals(fields[0], s_header[0], StringComparison.OrdinalIgnoreCase)
           && string.Equals(fields[^1], s_header[^1], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The picture for one entry, fetched and kept on first sight.
    /// </summary>
    /// <returns>The local file, or <c>null</c> when it could not be fetched.</returns>
    public Task<string?> PictureAsync(Gamerpic gamerpic, CancellationToken token = default)
        => _pictures.LoadAsync(ImageCache.FileNameFor(gamerpic.Url), gamerpic.Url, token);

    /// <summary>The cached copy of the index, or <c>null</c> when there is not one yet.</summary>
    private string? ReadCached() {
        try {
            return File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private void WriteCached(string text) {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_indexPath)!);
            File.WriteAllText(_indexPath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            // Not kept is not a failure: the archive is read again next time, and the screen still works.
        }
    }

    private async Task<string?> DownloadIndexAsync(CancellationToken token) {
        try {
            using var response = await _http.GetAsync(IndexUrl, token).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException) {
            return null;
        }
    }
}
