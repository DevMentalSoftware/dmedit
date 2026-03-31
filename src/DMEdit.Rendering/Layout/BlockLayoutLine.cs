using Avalonia.Media.TextFormatting;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// A laid-out block within a <see cref="BlockLayoutResult"/>. Holds the
/// Avalonia <see cref="TextLayout"/> for the block's text and its position
/// within the document-level layout.
///
/// Unlike <see cref="LayoutLine"/> which represents a single newline-delimited
/// line, this represents an entire block (paragraph, heading, etc.) which may
/// contain multiple visual lines from word-wrapping.
/// </summary>
public sealed class BlockLayoutLine : IDisposable {
    private bool _disposed;

    /// <summary>Index of the block in the <see cref="DMEdit.Core.Blocks.BlockDocument"/>.</summary>
    public int BlockIndex { get; }

    /// <summary>
    /// The actual character length of the block's text. May differ from the
    /// layout text length (which uses " " for empty blocks to get a height).
    /// </summary>
    public int TextLength { get; }

    /// <summary>Vertical position of the top of this block (pixels from document top).</summary>
    public double Y { get; }

    /// <summary>Total height of this block including margins and padding.</summary>
    public double Height { get; }

    /// <summary>Margin above this block (included in Height).</summary>
    public double MarginTop { get; }

    /// <summary>Margin below this block (included in Height).</summary>
    public double MarginBottom { get; }

    /// <summary>Padding left for the text content area.</summary>
    public double PaddingLeft { get; }

    /// <summary>Padding top for the text content area.</summary>
    public double PaddingTop { get; }

    /// <summary>
    /// The Avalonia TextLayout for this block's text. Used for rendering
    /// and hit-testing. The layout respects the block's style (font, size, etc.).
    /// </summary>
    public TextLayout Layout { get; }

    /// <summary>The actual computed height of the text content (excluding margins/padding).</summary>
    public double ContentHeight => Layout.Height;

    internal BlockLayoutLine(
        int blockIndex,
        int textLength,
        double y,
        double height,
        double marginTop,
        double marginBottom,
        double paddingLeft,
        double paddingTop,
        TextLayout layout) {

        BlockIndex = blockIndex;
        TextLength = textLength;
        Y = y;
        Height = height;
        MarginTop = marginTop;
        MarginBottom = marginBottom;
        PaddingLeft = paddingLeft;
        PaddingTop = paddingTop;
        Layout = layout;
    }

    /// <summary>
    /// The Y coordinate where the text content begins (after margin and padding).
    /// </summary>
    public double ContentY => Y + MarginTop + PaddingTop;

    /// <summary>
    /// The X coordinate where the text content begins (after padding).
    /// </summary>
    public double ContentX => PaddingLeft;

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        Layout.Dispose();
    }
}
