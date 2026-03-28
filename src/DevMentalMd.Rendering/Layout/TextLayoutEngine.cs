using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Rendering.Layout;

/// <summary>
/// Stateless service that converts a document string into a <see cref="LayoutResult"/>
/// containing <see cref="LayoutLine"/> objects suitable for rendering and hit-testing.
/// One LayoutLine is produced per logical line (newline-delimited paragraph).
/// Word-wrap within each line is handled by Avalonia's <c>TextLayout</c>.
/// </summary>
/// <remarks>
/// <see cref="LayoutLine.Row"/> and <see cref="LayoutLine.HeightInRows"/> are in
/// abstract visual-row units.  The engine stores the pixel height of one row in
/// <see cref="LayoutResult.RowHeight"/> so consumers can convert to pixels when needed.
/// </remarks>
public sealed class TextLayoutEngine {
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a layout for an empty document (single empty line).
    /// </summary>
    public LayoutResult LayoutEmpty(
        Typeface typeface, double fontSize, IBrush foreground, double maxWidth) =>
        LayoutLines(new PieceTable(), 0, 1, typeface, fontSize, foreground, maxWidth, 0);

    /// <summary>
    /// Lays out visible lines directly from the <see cref="PieceTable"/>,
    /// reading one line at a time to avoid materializing multiple lines
    /// into a single string.
    /// </summary>
    public LayoutResult LayoutLines(
        PieceTable table,
        long topLine,
        long bottomLine,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth,
        long viewportBase,
        long lineCount = -1,
        long docLength = -1) {

        using var spaceLayout = MakeTextLayout(" ", typeface, fontSize, foreground, double.PositiveInfinity);
        var rowHeight = spaceLayout.Height;

        var lines = new List<LayoutLine>();
        var row = 0;
        if (lineCount < 0) lineCount = table.LineCount;
        if (docLength < 0) docLength = table.Length;
        var charOfs = 0L;

        for (var lineIdx = topLine; lineIdx < bottomLine && lineIdx < lineCount; lineIdx++) {
            var lineStart = table.LineStartOfs(lineIdx);
            var lineEnd = lineIdx + 1 < lineCount
                ? table.LineStartOfs(lineIdx + 1)
                : docLength;

            // Skip lines with inconsistent offsets — can happen during
            // streaming load when the background scan mutates line data
            // between reads.
            if (lineStart < 0 || lineEnd < 0 || lineEnd < lineStart) continue;
            var fullLen = (int)(lineEnd - lineStart);
            if (fullLen > PieceTable.MaxGetTextLength) continue;
            var nlLen = 0;
            if (fullLen > 0) {
                // Read the last 1-2 chars to detect newline type.
                var tailStart = Math.Max(lineStart, lineEnd - 2);
                var tail = table.GetText(tailStart, (int)(lineEnd - tailStart));
                if (tail.Length > 0 && tail[^1] == '\n') {
                    nlLen = (tail.Length >= 2 && tail[^2] == '\r') ? 2 : 1;
                } else if (tail.Length > 0 && tail[^1] == '\r') {
                    nlLen = 1;
                }
            }
            var contentLen = fullLen - nlLen;
            var lineText = contentLen > 0 ? table.GetText(lineStart, contentLen) : "";

            var layout = MakeTextLayout(lineText, typeface, fontSize, foreground, maxWidth);
            var h = layout.Height > 0 ? layout.Height : rowHeight;
            var heightInRows = Math.Max(1, (int)Math.Round(h / rowHeight));
            lines.Add(new LayoutLine((int)charOfs, contentLen, row, heightInRows, layout));
            row += heightInRows;
            charOfs += fullLen;
        }

        if (lines.Count == 0) {
            var layout = MakeTextLayout("", typeface, fontSize, foreground, maxWidth);
            lines.Add(new LayoutLine(0, 0, 0, 1, layout));
        }

        return new LayoutResult(lines, rowHeight, viewportBase);
    }

    /// <summary>
    /// Returns the logical character offset closest to <paramref name="pt"/> in the laid-out text.
    /// Coordinates are in pixels (layout-document space).
    /// </summary>
    public int HitTest(Point pt, LayoutResult result) {
        var lines = result.Lines;
        if (lines.Count == 0) {
            return 0;
        }

        var rh = result.RowHeight;
        var line = FindLineAt(pt.Y, lines, rh);
        var localPt = new Point(Math.Max(0, pt.X), pt.Y - line.Row * rh);
        var hit = line.Layout.HitTestPoint(localPt);

        var posInLine = Math.Clamp(hit.TextPosition, 0, line.CharLen);
        return line.CharStart + posInLine;
    }

    /// <summary>
    /// Returns the bounding rectangle of the caret at logical offset <paramref name="charOfs"/>,
    /// in layout coordinates (Y is absolute document Y in pixels).
    /// </summary>
    public Rect GetCaretBounds(int charOfs, LayoutResult result) {
        var lines = result.Lines;
        if (lines.Count == 0) {
            return new Rect(0, 0, 1, 16);
        }

        var rh = result.RowHeight;
        var lineIdx = FindLineIndexForOfs(charOfs, lines);
        var line = lines[lineIdx];
        var posInLine = Math.Clamp(charOfs - line.CharStart, 0, line.CharLen);

        Rect relRect;
        if (posInLine == 0) {
            relRect = new Rect(0, 0, 0, rh);
        } else {
            relRect = line.Layout.HitTestTextPosition(posInLine);
        }

        return new Rect(
            relRect.X,
            line.Row * rh + relRect.Y,
            1,
            relRect.Height > 0 ? relRect.Height : rh);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static TextLayout MakeTextLayout(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth) {

        var wrapping = double.IsFinite(maxWidth) && maxWidth > 0
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;
        var effectiveMaxWidth = double.IsFinite(maxWidth) && maxWidth > 0
            ? maxWidth
            : double.PositiveInfinity;

        return new TextLayout(
            text,
            typeface,
            fontSize,
            foreground,
            textAlignment: TextAlignment.Left,
            textWrapping: wrapping,
            maxWidth: effectiveMaxWidth,
            maxHeight: double.PositiveInfinity);
    }

    /// <summary>Finds the line whose pixel-Y range contains <paramref name="y"/>.</summary>
    private static LayoutLine FindLineAt(double y, IReadOnlyList<LayoutLine> lines, double rh) {
        var best = lines[0];
        foreach (var line in lines) {
            if (line.Row * rh <= y) {
                best = line;
            } else {
                break;
            }
        }
        return best;
    }

    private static int FindLineIndexForOfs(int charOfs, IReadOnlyList<LayoutLine> lines) {
        for (var i = lines.Count - 1; i >= 0; i--) {
            if (lines[i].CharStart <= charOfs) {
                return i;
            }
        }
        return 0;
    }
}
