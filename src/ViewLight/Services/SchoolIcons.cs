using Avalonia.Media.Imaging;

namespace ViewLight.Services;

/// <summary>
/// The game's own school element logos - Fire, Ice, Storm, Myth, Life, Death, Balance - as the Wizard101
/// wiki publishes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The school's own emblem, fetched rather than shipped.</b> Each one is the school's element inside the
/// shield KingsIsle draws it in - the flame, the snowflake, the bolt, the scales, the skull - and it is what
/// says at a glance which school a wizard is, which is the whole point of putting one on the wizard's card.
/// ViewLight redistributes nothing of KingsIsle's, the same rule <see cref="GameArtCache"/> follows for the
/// wordmark and the box art, so they are downloaded once into the user's own machine and read from there
/// afterwards.
/// </para>
/// <para>
/// <b>Why these files and not the wiki's smaller ones.</b> The wiki keeps two sets: a 25×25 marker it puts
/// in the school column of its own tables, and the school's logo, at 150×150, that each school's page is
/// headed with. The marker is a few pixels of metal and colour at card size - a smudge where a school should
/// be, which is exactly what a logo drawn to stand in for the real one looks like. The logo is the emblem
/// itself, so that is what is fetched. Nothing is invented here: a school with no address has no logo, and a
/// wizard whose school was not read keeps the plain card rather than being given somebody else's symbol.
/// </para>
/// <para>
/// <b>Why this host.</b> The Wizard101 Central wiki has the same logos and refuses every request that does
/// not come from a browser - a plain one gets a 403 - so it cannot be a source a launcher uses. The Wizard101
/// Fandom wiki's file CDN serves them and answers a plain request, and <c>?format=original</c> asks for the
/// PNG as it was uploaded rather than the WebP the CDN would otherwise negotiate.
/// </para>
/// </remarks>
public static class SchoolIcons {

    /// <summary>Where each school's element logo is published, keyed by the school's own name.</summary>
    /// <remarks>
    /// The seven a player can be - <see cref="WizardSchool.Names"/> - and nothing else. A creature's Sun,
    /// Moon, Star or Shadow is not a wizard's school, so no card ever asks for one.
    /// </remarks>
    private static readonly Dictionary<string, string> s_urls = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = Wiki("e/e5/FireLogo.png"),
        ["Ice"] = Wiki("6/64/IceLogo.png"),
        ["Storm"] = Wiki("4/40/StormLogo.png"),
        ["Myth"] = Wiki("a/a8/MythLogo.png"),
        ["Life"] = Wiki("0/0d/LifeLogo.png"),
        ["Death"] = Wiki("8/80/DeathLogo.png"),
        ["Balance"] = Wiki("4/4f/BalanceLogo.png"),
    };

    /// <summary>The published address of one of the wiki's files, asked for as the PNG it was uploaded as.</summary>
    private static string Wiki(string path)
        => $"https://static.wikia.nocookie.net/wizard101/images/{path}/revision/latest?format=original";

    /// <summary>Every school that has an element logo, which is every school a wizard can be.</summary>
    public static IReadOnlyList<string> Names { get; } = WizardSchool.Names;

    /// <summary>
    /// The address of a school's element logo, or <c>null</c> for anything that is not one of the seven.
    /// </summary>
    /// <param name="school">A school's name, as <see cref="WizardSchool"/> spells it.</param>
    public static string? UrlFor(string? school)
        => string.IsNullOrWhiteSpace(school) ? null : s_urls.GetValueOrDefault(school.Trim());
}

/// <summary>
/// The school logos already fetched, as pictures the screen can draw.
/// </summary>
/// <remarks>
/// <para>
/// Every wizard card on the profile asks for its school's logo, so the decode is done once per school and
/// kept: there are seven of them, they never change, and a profile with six wizards in one school would
/// otherwise decode the same PNG six times on every refresh - which is twenty times a minute while
/// the launcher sits on the profile screen.
/// </para>
/// <para>
/// A school that cannot be fetched is remembered as having no picture for this run, so an offline launcher
/// tries seven times rather than once per wizard per poll. The file itself is what survives a restart: the
/// next run reads it out of the cache and never asks the network at all.
/// </para>
/// </remarks>
public sealed class SchoolIconCache {

    private readonly ImageCache _files;
    private readonly Dictionary<string, Bitmap?> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public SchoolIconCache(HttpClient? http = null, string? directory = null) {
        _files = new ImageCache(directory ?? LauncherPaths.SchoolIconsDirectory, http);
    }

    /// <summary>Where a school's logo is kept once it has been fetched.</summary>
    public string PathFor(string school)
        => _files.PathFor(FileNameOf(school));

    /// <summary>
    /// The name a school's logo is kept under, which is the one <see cref="ImageCache"/> makes out of the
    /// address it came from.
    /// </summary>
    /// <remarks>
    /// Not the school's name: a cached file has to belong to the address it was fetched from, or a launcher
    /// that has already run would keep handing back the art it fetched from the address before this one and
    /// never go and get the right logo at all.
    /// </remarks>
    private static string FileNameOf(string school)
        => SchoolIcons.UrlFor(school) is { } url
            ? ImageCache.FileNameFor(url)
            : $"{school.ToLowerInvariant()}.png";

    /// <summary>
    /// The logo for a school, out of memory, then off disk, then off the network - or <c>null</c>.
    /// </summary>
    /// <param name="school">A school's name, or anything else, in which case there is nothing to return.</param>
    /// <remarks>
    /// Never throws and never reports: a card without its school's symbol is still a card, and the profile
    /// screen has no line to put a fetch failure on - the Log gets one when the cache is warmed instead.
    /// </remarks>
    public async Task<Bitmap?> LoadAsync(string? school, CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(school)) {
            return null;
        }

        var name = school.Trim();

        if (_decoded.TryGetValue(name, out var known)) {
            return known;
        }

        var url = SchoolIcons.UrlFor(name);
        if (url is null) {
            return _decoded[name] = null;
        }

        var path = await _files.LoadAsync(FileNameOf(name), url, token).ConfigureAwait(false);

        var bitmap = path is null ? null : Decode(path);

        // Only a picture is remembered as a picture: a fetch that failed is remembered as "nothing for this
        // run", because a school's logo does not exist on the network one moment and not the next.
        _decoded[name] = bitmap;

        return bitmap;
    }

    /// <summary>Whether a school's logo has been fetched and kept already.</summary>
    public bool IsCached(string school) => _files.Cached(FileNameOf(school)) is not null;

    /// <summary>
    /// A picture from a file, or <c>null</c> when it will not decode.
    /// </summary>
    /// <remarks>
    /// Reads the bytes rather than opening the file, so nothing keeps a handle on the cache: the file may
    /// be replaced under a running launcher, and a locked cache file is a download that can never land.
    /// </remarks>
    public static Bitmap? Decode(string path) {
        try {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception) {
            return null;
        }
    }
}
