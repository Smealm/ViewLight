using System.Xml;
using System.Xml.Linq;

namespace ViewLight.Services;

/// <summary>One file of a revision, as the patch server's own file list describes it.</summary>
/// <param name="RelativePath">
/// Where it lives in the revision, taken from the list verbatim and with forward slashes -
/// <c>Data/GameData/Root.wad</c> - which is also how it is asked for over HTTP, so it is never
/// re-spelled into a Windows path here.
/// </param>
/// <param name="Size">How many bytes the file is, which is what the file on disk can be checked against.</param>
public sealed record PatchServerManifestEntry(string RelativePath, long Size);

/// <summary>
/// A revision's own file list: <c>LatestFileList.xml</c>, the manifest the patch server downloads from
/// Wizard101's patch server before any of the files it names.
/// </summary>
/// <remarks>
/// <para>
/// This is the list of what a revision <b>is</b> - every file, with its size - so it is what makes
/// anything said about a revision's files a fact rather than a guess. The launcher reads it to know
/// which of a local client build's files a patch server is missing, and it reads it from the revision's
/// own folder rather than fetching one of its own: the patch server has already downloaded it, and the
/// only list that matters is the one that describes what this patch server is serving.
/// </para>
/// <para>
/// The shape is KingsIsle's, not a schema anybody published: one element per file, each holding a
/// <c>RECORD</c> whose <c>SrcFileName</c> and <c>Size</c> are the two fields that matter, and whose
/// CRC fields are the client's own checksum - which the launcher does not reproduce, and does not need
/// to: size is what tells one build's file from another's here.
/// </para>
/// </remarks>
public static class PatchServerManifest {

    /// <summary>The file list, named as Wizard101 names it, inside a revision's folder.</summary>
    public const string FileName = "LatestFileList.xml";

    public static string PathFor(string revisionFolder) => Path.Combine(revisionFolder, FileName);

    /// <summary>Whether the folder holds a file list at all.</summary>
    public static bool Exists(string? revisionFolder)
        => !string.IsNullOrWhiteSpace(revisionFolder) && File.Exists(PathFor(revisionFolder));

    /// <summary>
    /// Reads the file list a revision folder holds, or returns <c>null</c> when there is none to read or
    /// it cannot be made sense of.
    /// </summary>
    /// <remarks>
    /// Never throws. A patch server that has not been started yet has no folder to read, and one that is
    /// being read while Aurorium writes the list leaves half an XML document behind - both of which mean
    /// the same thing to a caller: there is nothing to plan against yet.
    /// </remarks>
    public static IReadOnlyList<PatchServerManifestEntry>? TryRead(string? revisionFolder) {
        if (!Exists(revisionFolder)) {
            return null;
        }

        try {
            return Read(PathFor(revisionFolder!));
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Reads a file list from a path, in the order it lists them.</summary>
    public static IReadOnlyList<PatchServerManifestEntry> Read(string manifestPath) {
        var document = XDocument.Load(manifestPath, LoadOptions.None);
        var entries = new List<PatchServerManifestEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in document.Descendants("RECORD")) {
            var path = Clean(record.Element("SrcFileName")?.Value);
            if (path is null || !seen.Add(path)) {
                continue;
            }

            // A size the list does not carry, or carries as something other than a number, is a file
            // nothing can be said about - so it is left out rather than counted as zero bytes.
            if (!long.TryParse(Clean(record.Element("Size")?.Value), out var size) || size < 0) {
                continue;
            }

            entries.Add(new PatchServerManifestEntry(path, size));
        }

        return entries;
    }

    /// <summary>
    /// A path as it is used from here on: forward slashes, no leading one, and no surrounding space.
    /// </summary>
    private static string? Clean(string? value) {
        var trimmed = value?.Trim().Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
