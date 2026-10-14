namespace ViewLight.Models;

/// <summary>
/// An installable Imlight build published on GitHub.
/// </summary>
public sealed class ImlightRelease {

    /// <summary>Release tag, e.g. <c>r806919.Wizard_1_610</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>Release display name (falls back to <see cref="Tag"/>).</summary>
    public required string Name { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public bool Prerelease { get; init; }

    /// <summary>Asset file name, e.g. <c>Imlight-win-x64.zip</c>.</summary>
    public required string AssetName { get; init; }

    /// <summary>Direct download URL for the Windows asset.</summary>
    public required string AssetUrl { get; init; }

    /// <summary>Asset size in bytes, as reported by the GitHub API.</summary>
    public long AssetSize { get; init; }

    /// <summary>
    /// The game revision the Imlight server expects, parsed out of the tag
    /// (<c>r806919.Wizard_1_610</c> -&gt; <c>r806919.Wizard_1_610</c>).
    /// </summary>
    public string Revision => Tag;

    public string AssetSizeDisplay => AssetSize switch {
        <= 0 => "unknown size",
        < 1024 * 1024 => $"{AssetSize / 1024.0:F0} KB",
        _ => $"{AssetSize / (1024.0 * 1024.0):F0} MB",
    };

    public override string ToString() => Tag;
}
