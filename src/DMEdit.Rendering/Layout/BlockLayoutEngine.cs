using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DMEdit.Core.Blocks;
using DMEdit.Core.Styles;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Lays out a range of blocks from a <see cref="BlockDocument"/> using styles
/// from a <see cref="StyleSheet"/>. Produces <see cref="BlockLayoutResult"/>
/// with per-block <see cref="BlockLayoutLine"/> objects for rendering and
/// hit-testing.
///
/// This engine works alongside the existing <see cref="TextLayoutEngine"/>:
/// each block's text is laid out with Avalonia's TextLayout using the style
/// appropriate for its type. The engine also measures actual font metrics and
/// feeds them back to the <see cref="StyleSheet"/> for height estimation.
/// </summary>
public sealed class BlockLayoutEngine {
    private static readonly IBrush DefaultForeground = Brushes.Black;

    // -----------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------

    /// <summary>
    /// Lays out all blocks in the document. The caller must dispose the result.
    /// </summary>
    public BlockLayoutResult LayoutAll(
        BlockDocument doc,
        StyleSheet styles,
        double maxWidth) {

        return LayoutRange(doc, styles, 0, doc.BlockCount, maxWidth);
    }

    /// <summary>
    /// Lays out a range of blocks [startIndex, startIndex + count).
    /// The Y positions are relative to the first block in the range.
    /// The caller must dispose the result.
    /// </summary>
    public BlockLayoutResult LayoutRange(
        BlockDocument doc,
        StyleSheet styles,
        int startIndex,
        int count,
        double maxWidth) {

        var blocks = new List<BlockLayoutLine>();
        var y = 0.0;
        var endIndex = Math.Min(startIndex + count, doc.BlockCount);

        for (var i = startIndex; i < endIndex; i++) {
            var block = doc[i];
            var style = styles.GetBlockStyle(block.Type);
            var foreground = ParseBrush(style.ForegroundColor) ?? DefaultForeground;

            var typeface = new Typeface(
                new FontFamily(style.FontFamily),
                FontStyle.Normal,
                (FontWeight)style.FontWeight);

            var effectiveMaxWidth = maxWidth - style.PaddingLeft - style.PaddingRight;
            if (effectiveMaxWidth <= 0) {
                effectiveMaxWidth = 1;
            }

            var actualTextLength = block.Length;
            var layoutText = actualTextLength > 0 ? block.Text : " ";

            var layout = CreateTextLayout(layoutText, typeface, style.FontSize, foreground, effectiveMaxWidth);

            // Measure actual font metrics and feed back to the stylesheet
            if (actualTextLength > 0) {
                UpdateFontMetrics(styles, block.Type, style, layout, actualTextLength);
            }

            var contentHeight = layout.Height;
            var totalHeight = style.MarginTop + style.PaddingTop + contentHeight + style.PaddingBottom + style.MarginBottom;

            blocks.Add(new BlockLayoutLine(
                blockIndex: i,
                textLength: actualTextLength,
                y: y,
                height: totalHeight,
                marginTop: style.MarginTop,
                marginBottom: style.MarginBottom,
                paddingLeft: style.PaddingLeft,
                paddingTop: style.PaddingTop,
                layout: layout));

            // Also update the BlockDocument's height tree with the actual height
            doc.UpdateBlockHeight(i, totalHeight);

            y += totalHeight;
        }

        return new BlockLayoutResult(blocks, y, startIndex);
    }

    /// <summary>
    /// Hit-tests a point within a block layout result. Returns the block index
    /// and the character offset within that block.
    /// </summary>
    public BlockPosition HitTest(Point pt, BlockLayoutResult result) {
        if (result.Blocks.Count == 0) {
            return new BlockPosition(0, 0);
        }

        var blockLine = result.FindBlockAtY(pt.Y);
        if (blockLine == null) {
            blockLine = result.Blocks[^1];
        }

        // Convert the point to block-local coordinates
        var localPt = new Point(
            Math.Max(0, pt.X - blockLine.ContentX),
            pt.Y - blockLine.ContentY);

        var hit = blockLine.Layout.HitTestPoint(localPt);
        // Use TextLength (actual block text length) to clamp, not the layout text
        // which may be " " for empty blocks
        var localOffset = Math.Clamp(hit.TextPosition, 0, blockLine.TextLength);

        return new BlockPosition(blockLine.BlockIndex, localOffset);
    }

    /// <summary>
    /// Returns the caret rectangle for a position within a block, in
    /// document-layout coordinates.
    /// </summary>
    public Rect GetCaretBounds(BlockPosition position, BlockLayoutResult result) {
        var blockLine = result.FindBlockByIndex(position.BlockIndex);
        if (blockLine == null) {
            return new Rect(0, 0, 1, 20);
        }

        Rect relRect;
        if (position.LocalOffset == 0) {
            relRect = new Rect(0, 0, 0, blockLine.ContentHeight);
        } else {
            relRect = blockLine.Layout.HitTestTextPosition(position.LocalOffset);
        }

        return new Rect(
            blockLine.ContentX + relRect.X,
            blockLine.ContentY + relRect.Y,
            1,
            relRect.Height > 0 ? relRect.Height : blockLine.ContentHeight);
    }

    // -----------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------

    private static TextLayout CreateTextLayout(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth) =>
        TextLayoutHelper.Create(text, typeface, fontSize, foreground, maxWidth);

    /// <summary>
    /// Measures the average character width from the layout and feeds it
    /// back to the stylesheet for future height estimation.
    /// </summary>
    private static void UpdateFontMetrics(
        StyleSheet styles,
        BlockType blockType,
        BlockStyle style,
        TextLayout layout,
        int textLength) {

        if (textLength == 0) {
            return;
        }

        // For monospace: all characters are the same width, measure one
        // For proportional: average across the laid-out text
        double avgCharWidth;
        if (style.IsMonospace) {
            // Measure a representative character
            using var singleLayout = new TextLayout(
                "M",
                new Typeface(new FontFamily(style.FontFamily)),
                style.FontSize,
                Brushes.Black);
            avgCharWidth = singleLayout.Width;
        } else {
            avgCharWidth = layout.Width > 0
                ? layout.Width / textLength
                : style.FontSize * 0.5;
        }

        styles.UpdateFontMetrics(blockType, avgCharWidth);
    }

    private static IBrush? ParseBrush(string? color) {
        if (color == null) {
            return null;
        }
        try {
            return new SolidColorBrush(Color.Parse(color));
        } catch {
            return null;
        }
    }
}
