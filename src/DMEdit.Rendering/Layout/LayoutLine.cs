using Avalonia.Media.TextFormatting;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// A single laid-out logical line of text (one per `\n`-separated paragraph).
/// Word-wrap within the line is handled internally by <see cref="TextLayout"/>.
/// Must be disposed when the layout is invalidated.
/// </summary>
/// <remarks>
/// <see cref="Row"/> and <see cref="HeightInRows"/> are in abstract visual-row
/// units.  Multiply by <see cref="LayoutResult.RowHeight"/> to get pixel values.
/// This eliminates sub-pixel rounding bugs in vertical math and makes the
/// layout invariant to font-size changes (only RowHeight changes).
/// </remarks>
public sealed class LayoutLine : IDisposable {
    private bool _disposed;

    /// <summary>Logical character offset of the first character in this line (in the full document string).</summary>
    public int CharStart { get; }

    /// <summary>Number of characters in this line, excluding any trailing newline character.</summary>
    public int CharLen { get; }

    /// <summary>Visual row index of the top of this line (0-based, in row units from document top).</summary>
    public int Row { get; }

    /// <summary>Height of this line in visual rows (≥ 1; &gt; 1 when word-wrap is active).</summary>
    public int HeightInRows { get; }

    /// <summary>Exclusive end offset: CharStart + CharLen.</summary>
    public int CharEnd => CharStart + CharLen;

    /// <summary>
    /// The Avalonia TextLayout for this line.
    /// Used for rendering (<c>Layout.Draw(context, origin)</c>) and hit-testing.
    /// </summary>
    public TextLayout Layout { get; }

    internal LayoutLine(int charStart, int charLen, int row, int heightInRows, TextLayout layout) {
        CharStart = charStart;
        CharLen = charLen;
        Row = row;
        HeightInRows = heightInRows;
        Layout = layout;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        Layout.Dispose();
    }
}
