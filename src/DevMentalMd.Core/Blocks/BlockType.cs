namespace DevMentalMd.Core.Blocks;

/// <summary>
/// The type of a document block. Each block in a <see cref="BlockDocument"/>
/// has exactly one type, which determines its rendering style and editing behavior.
/// </summary>
public enum BlockType {
    /// <summary>Body text paragraph. The default block type.</summary>
    Paragraph,

    /// <summary>Heading level 1 (largest).</summary>
    Heading1,

    /// <summary>Heading level 2.</summary>
    Heading2,

    /// <summary>Heading level 3.</summary>
    Heading3,

    /// <summary>Heading level 4.</summary>
    Heading4,

    /// <summary>Heading level 5.</summary>
    Heading5,

    /// <summary>Heading level 6 (smallest).</summary>
    Heading6,

    /// <summary>Fenced code block with optional language tag.</summary>
    CodeBlock,

    /// <summary>Block-level quotation.</summary>
    BlockQuote,

    /// <summary>Unordered (bullet) list item.</summary>
    UnorderedListItem,

    /// <summary>Ordered (numbered) list item.</summary>
    OrderedListItem,

    /// <summary>Horizontal rule / thematic break.</summary>
    HorizontalRule,

    /// <summary>Image block (display-level, not inline).</summary>
    Image,

    /// <summary>Table block (contains rows and cells internally).</summary>
    Table,
}
