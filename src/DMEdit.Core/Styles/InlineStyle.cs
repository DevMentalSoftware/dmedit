namespace DMEdit.Core.Styles;

/// <summary>
/// Visual styling overrides for an inline span type. These properties modify
/// the containing block's style for the span's character range.
///
/// All properties are nullable — null means "inherit from the block style."
/// </summary>
public sealed class InlineStyle {
    /// <summary>Font weight override. 700 = bold, null = inherit block style.</summary>
    public int? FontWeight { get; set; }

    /// <summary>Italic override. Null = inherit block style.</summary>
    public bool? Italic { get; set; }

    /// <summary>Font family override (e.g., monospace for inline code). Null = inherit.</summary>
    public string? FontFamily { get; set; }

    /// <summary>Text color override as CSS-style hex string. Null = inherit.</summary>
    public string? ForegroundColor { get; set; }

    /// <summary>Background color override as CSS-style hex string. Null = inherit.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Strikethrough decoration.</summary>
    public bool Strikethrough { get; set; }

    /// <summary>Underline decoration (typically for links).</summary>
    public bool Underline { get; set; }
}
