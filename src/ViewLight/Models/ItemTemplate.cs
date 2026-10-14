namespace ViewLight.Models;

/// <summary>
/// A single item template known to the game client.
/// </summary>
/// <param name="TemplateId">The numeric template ID written into the server config.</param>
/// <param name="Name">Human readable name, e.g. <c>Wand-T1-016</c>.</param>
/// <param name="Category">Rough grouping used for filtering in the item picker.</param>
public sealed record ItemTemplate(uint TemplateId, string Name, string Category) {

    /// <summary>Text shown in the item picker list.</summary>
    public string Display => $"{Name}  ·  {TemplateId}";

    public override string ToString() => Display;
}
