namespace DevMentalMd.Core.Blocks;

/// <summary>
/// The type of an inline formatting span within a block's text content.
/// Spans are applied via explicit user commands (toolbar, keyboard shortcuts),
/// never by typing raw markdown syntax.
/// </summary>
public enum InlineSpanType {
    /// <summary>Bold / strong emphasis.</summary>
    Bold,

    /// <summary>Italic / emphasis.</summary>
    Italic,

    /// <summary>Inline code (monospace).</summary>
    InlineCode,

    /// <summary>Strikethrough text.</summary>
    Strikethrough,

    /// <summary>Hyperlink. The span's <see cref="InlineSpan.Url"/> holds the target.</summary>
    Link,
}
