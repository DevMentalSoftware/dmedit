using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// A single laid-out logical line of text (one per `\n`-separated paragraph).
/// Word-wrap within the line is handled by either the slow path
/// (<see cref="Layout"/>, an Avalonia <see cref="TextLayout"/>) or the
/// fast path (<see cref="Mono"/>, a hand-rolled monospace
/// <see cref="MonoLineLayout"/>).  Exactly one of the two is non-null.
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
    /// The Avalonia TextLayout for this line, used by the slow path.
    /// Null when <see cref="Mono"/> is set.
    /// </summary>
    public TextLayout? Layout { get; }

    /// <summary>
    /// The monospace fast-path layout, used when the line qualifies (single
    /// concrete glyph typeface, monospace metrics, no tabs / control chars).
    /// Null when <see cref="Layout"/> is set.
    /// </summary>
    public MonoLineLayout? Mono { get; }

    /// <summary>True when this line uses the monospace fast path.</summary>
    public bool IsMono => Mono is not null;

    public LayoutLine(int charStart, int charLen, int row, int heightInRows, TextLayout layout) {
        CharStart = charStart;
        CharLen = charLen;
        Row = row;
        HeightInRows = heightInRows;
        Layout = layout;
    }

    public LayoutLine(int charStart, int charLen, int row, int heightInRows, MonoLineLayout mono) {
        CharStart = charStart;
        CharLen = charLen;
        Row = row;
        HeightInRows = heightInRows;
        Mono = mono;
    }

    // -------------------------------------------------------------------------
    // Path-agnostic API used by EditorControl
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the entire line at <paramref name="origin"/>.  Branches between
    /// the GlyphRun fast path (one row at a time, hanging indent applied)
    /// and the TextLayout slow path (single Draw call, no hanging indent).
    /// </summary>
    public void Render(DrawingContext context, Point origin, IBrush? foreground = null) {
        if (Mono is { } mono) {
            mono.Draw(context, origin, foreground);
        } else {
            Layout!.Draw(context, origin);
        }
    }

    /// <summary>
    /// Returns the caret bounds for character offset <paramref name="posInLine"/>
    /// (relative to the start of this line), in line-local coordinates.
    /// </summary>
    public Rect HitTestTextPosition(int posInLine, bool isAtEnd = false) {
        if (Mono is { } mono) {
            return mono.GetCaretBounds(posInLine, isAtEnd);
        }
        // Proportional path: Avalonia's HitTestTextPosition doesn't
        // support affinity natively.  Left-affinity at soft breaks
        // would need TextLines enumeration — deferred.
        return Layout!.HitTestTextPosition(posInLine);
    }

    /// <summary>
    /// Returns the bounding rectangles covering the character range
    /// [<paramref name="start"/>, start+length), one rect per affected
    /// visual row.  Line-local coordinates.
    /// </summary>
    public IEnumerable<Rect> HitTestTextRange(int start, int length) {
        if (Mono is { } mono) {
            return mono.HitTestTextRange(start, length);
        }
        return Layout!.HitTestTextRange(start, length);
    }

    /// <summary>
    /// Returns the character offset (in this line) closest to the given
    /// line-local point.
    /// </summary>
    public int HitTestPoint(Point local) {
        if (Mono is { } mono) {
            return mono.HitTestPoint(local);
        }
        return Layout!.HitTestPoint(local).TextPosition;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        Layout?.Dispose();
        Mono?.Dispose();
    }
}
