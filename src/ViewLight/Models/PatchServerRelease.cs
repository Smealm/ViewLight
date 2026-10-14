namespace ViewLight.Models;

/// <summary>
/// An installable Aurorium patch server published on GitHub.
/// </summary>
/// <remarks>
/// Unlike an Imlight release this is a single executable rather than an archive: Aurorium publishes
/// <c>aurorium.exe</c> for Windows, so "installing" it is downloading one file and writing its config
/// beside it.
/// </remarks>
public sealed class PatchServerRelease {

    /// <summary>Release tag, e.g. <c>v4.0.2</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>Release display name (falls back to <see cref="Tag"/>).</summary>
    public required string Name { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public bool Prerelease { get; init; }

    /// <summary>Asset file name, e.g. <c>aurorium.exe</c>.</summary>
    public required string AssetName { get; init; }

    /// <summary>Direct download URL for the Windows build.</summary>
    public required string AssetUrl { get; init; }

    /// <summary>Asset size in bytes, as reported by the GitHub API.</summary>
    public long AssetSize { get; init; }

    public string AssetSizeDisplay => AssetSize switch {
        <= 0 => "unknown size",
        < 1024 * 1024 => $"{AssetSize / 1024.0:F0} KB",
        _ => $"{AssetSize / (1024.0 * 1024.0):F1} MB",
    };

    public override string ToString() => Tag;
}
