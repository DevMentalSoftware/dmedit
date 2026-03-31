using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DMEdit.Core.Blocks;

namespace DMEdit.Core.Styles;

/// <summary>
/// Maps block types and inline span types to their visual styles. Provides
/// style-aware height estimation for the <see cref="BlockDocument"/> Fenwick
/// trees.
///
/// The stylesheet is the single source of truth for visual properties. Both
/// the renderer and the height estimator read from it. The rendering layer
/// calls <see cref="UpdateFontMetrics"/> to feed back measured character
/// widths so that height estimation becomes more accurate.
///
/// First pass: constructed in code with defaults. Later: loaded from a
/// user-editable JSON file.
/// </summary>
public sealed class StyleSheet {
    private readonly Dictionary<BlockType, BlockStyle> _blockStyles = new();
    private readonly Dictionary<InlineSpanType, InlineStyle> _inlineStyles = new();
    private readonly BlockStyle _defaultBlockStyle = new();
    private readonly InlineStyle _defaultInlineStyle = new();

    /// <summary>
    /// The wrap width in pixels. Used by height estimation to compute visual
    /// line count. 0 = no wrapping (each logical line = one visual line).
    /// </summary>
    public double WrapWidth { get; set; } = 720.0;

    // -----------------------------------------------------------------
    // Style access
    // -----------------------------------------------------------------

    /// <summary>Returns the style for a block type. Falls back to the default style.</summary>
    public BlockStyle GetBlockStyle(BlockType type) {
        return _blockStyles.TryGetValue(type, out var style) ? style : _defaultBlockStyle;
    }

    /// <summary>Returns the style for an inline span type. Falls back to the default style.</summary>
    public InlineStyle GetInlineStyle(InlineSpanType type) {
        return _inlineStyles.TryGetValue(type, out var style) ? style : _defaultInlineStyle;
    }

    /// <summary>Sets the style for a block type.</summary>
    public void SetBlockStyle(BlockType type, BlockStyle style) {
        _blockStyles[type] = style;
    }

    /// <summary>Sets the style for an inline span type.</summary>
    public void SetInlineStyle(InlineSpanType type, InlineStyle style) {
        _inlineStyles[type] = style;
    }

    /// <summary>All registered block type styles.</summary>
    public IReadOnlyDictionary<BlockType, BlockStyle> BlockStyles => _blockStyles;

    /// <summary>All registered inline span type styles.</summary>
    public IReadOnlyDictionary<InlineSpanType, InlineStyle> InlineStyles => _inlineStyles;

    // -----------------------------------------------------------------
    // Height estimation
    // -----------------------------------------------------------------

    /// <summary>
    /// Estimates the pixel height of a block using its style and the current
    /// <see cref="WrapWidth"/>. O(1) per block — no text layout involved.
    /// </summary>
    public double EstimateBlockHeight(Block block) {
        var style = GetBlockStyle(block.Type);
        return style.EstimateHeight(block.Length, WrapWidth);
    }

    // -----------------------------------------------------------------
    // Font metrics injection
    // -----------------------------------------------------------------

    /// <summary>
    /// Called by the rendering layer to update the average character width
    /// for a block type's style. This makes subsequent height estimates more
    /// accurate.
    /// </summary>
    public void UpdateFontMetrics(BlockType type, double avgCharWidth) {
        var style = GetBlockStyle(type);
        style.AvgCharWidth = avgCharWidth;
    }

    // -----------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------

    /// <summary>
    /// Creates a stylesheet with sensible defaults for all block and inline
    /// types. Intended as the starting point — the user or a JSON file can
    /// override individual properties later.
    /// </summary>
    public static StyleSheet CreateDefault() {
        var sheet = new StyleSheet();

        // Paragraph (default — already set by BlockStyle constructor)
        sheet.SetBlockStyle(BlockType.Paragraph, new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            MarginBottom = 8,
        });

        // Headings
        sheet.SetBlockStyle(BlockType.Heading1, new BlockStyle {
            FontSize = 28,
            FontWeight = 700,
            LineHeight = 1.3,
            MarginTop = 24,
            MarginBottom = 12,
        });
        sheet.SetBlockStyle(BlockType.Heading2, new BlockStyle {
            FontSize = 22,
            FontWeight = 700,
            LineHeight = 1.3,
            MarginTop = 20,
            MarginBottom = 10,
        });
        sheet.SetBlockStyle(BlockType.Heading3, new BlockStyle {
            FontSize = 18,
            FontWeight = 700,
            LineHeight = 1.3,
            MarginTop = 16,
            MarginBottom = 8,
        });
        sheet.SetBlockStyle(BlockType.Heading4, new BlockStyle {
            FontSize = 16,
            FontWeight = 700,
            LineHeight = 1.4,
            MarginTop = 12,
            MarginBottom = 6,
        });
        sheet.SetBlockStyle(BlockType.Heading5, new BlockStyle {
            FontSize = 14,
            FontWeight = 700,
            LineHeight = 1.4,
            MarginTop = 10,
            MarginBottom = 4,
        });
        sheet.SetBlockStyle(BlockType.Heading6, new BlockStyle {
            FontSize = 13,
            FontWeight = 700,
            LineHeight = 1.4,
            MarginTop = 8,
            MarginBottom = 4,
        });

        // Code block
        sheet.SetBlockStyle(BlockType.CodeBlock, new BlockStyle {
            FontFamily = "Cascadia Code",
            FontSize = 13,
            IsMonospace = true,
            LineHeight = 1.4,
            MarginTop = 8,
            MarginBottom = 8,
            PaddingTop = 8,
            PaddingBottom = 8,
            PaddingLeft = 12,
            PaddingRight = 12,
            BackgroundColor = "#F5F5F5",
            MaxVisibleLines = 30,
        });

        // Block quote
        sheet.SetBlockStyle(BlockType.BlockQuote, new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            MarginTop = 8,
            MarginBottom = 8,
            PaddingLeft = 16,
            ForegroundColor = "#555555",
        });

        // List items
        sheet.SetBlockStyle(BlockType.UnorderedListItem, new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            MarginBottom = 4,
            PaddingLeft = 24,
        });
        sheet.SetBlockStyle(BlockType.OrderedListItem, new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            MarginBottom = 4,
            PaddingLeft = 24,
        });

        // Horizontal rule
        sheet.SetBlockStyle(BlockType.HorizontalRule, new BlockStyle {
            FontSize = 1,
            LineHeight = 1,
            MarginTop = 12,
            MarginBottom = 12,
        });

        // Image
        sheet.SetBlockStyle(BlockType.Image, new BlockStyle {
            MarginTop = 8,
            MarginBottom = 8,
        });

        // Table
        sheet.SetBlockStyle(BlockType.Table, new BlockStyle {
            FontSize = 13,
            LineHeight = 1.4,
            MarginTop = 8,
            MarginBottom = 8,
            MaxVisibleLines = 20,
        });

        // Inline styles
        sheet.SetInlineStyle(InlineSpanType.Bold, new InlineStyle {
            FontWeight = 700,
        });
        sheet.SetInlineStyle(InlineSpanType.Italic, new InlineStyle {
            Italic = true,
        });
        sheet.SetInlineStyle(InlineSpanType.InlineCode, new InlineStyle {
            FontFamily = "Cascadia Code",
            BackgroundColor = "#F0F0F0",
        });
        sheet.SetInlineStyle(InlineSpanType.Strikethrough, new InlineStyle {
            Strikethrough = true,
            ForegroundColor = "#888888",
        });
        sheet.SetInlineStyle(InlineSpanType.Link, new InlineStyle {
            ForegroundColor = "#0366D6",
            Underline = true,
        });

        return sheet;
    }
}
