using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace DevMentalMd.Rendering.Layout;

/// <summary>
/// Stateless service that converts a document string into a <see cref="LayoutResult"/>
/// containing <see cref="LayoutLine"/> objects suitable for rendering and hit-testing.
/// One LayoutLine is produced per logical line (newline-delimited paragraph).
/// Word-wrap within each line is handled by Avalonia's <c>TextLayout</c>.
/// </summary>
public sealed class TextLayoutEngine {
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lays out <paramref name="text"/> with the given font, foreground brush, and viewport width.
    /// The caller must dispose the returned <see cref="LayoutResult"/> when it is no longer needed.
    /// </summary>
    public LayoutResult Layout(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth) {

        var lines = new List<LayoutLine>();
        var y = 0.0;
        var charOfs = 0;

        foreach (var (lineText, newlineLen) in SplitLogicalLines(text)) {
            var layout = MakeTextLayout(lineText, typeface, fontSize, foreground, maxWidth);
            // Use at least the height of a single space if the line is empty
            var h = layout.Height > 0
                ? layout.Height
                : MakeTextLayout(" ", typeface, fontSize, foreground, maxWidth).Height;
            lines.Add(new LayoutLine(charOfs, lineText.Length, y, layout));
            y += h;
            charOfs += lineText.Length + newlineLen;
        }

        if (lines.Count == 0) {
            var layout = MakeTextLayout("", typeface, fontSize, foreground, maxWidth);
            lines.Add(new LayoutLine(0, 0, 0.0, layout));
        }

        return new LayoutResult(lines);
    }

    /// <summary>
    /// Returns the logical character offset closest to <paramref name="pt"/> in the laid-out text.
    /// </summary>
    public int HitTest(Point pt, LayoutResult result) {
        var lines = result.Lines;
        if (lines.Count == 0) {
            return 0;
        }

        var line = FindLineAt(pt.Y, lines);
        var localPt = new Point(Math.Max(0, pt.X), pt.Y - line.Y);
        var hit = line.Layout.HitTestPoint(localPt);

        var posInLine = Math.Clamp(hit.TextPosition, 0, line.CharLen);
        return line.CharStart + posInLine;
    }

    /// <summary>
    /// Returns the bounding rectangle of the caret at logical offset <paramref name="charOfs"/>,
    /// in layout coordinates (Y is absolute document Y).
    /// </summary>
    public Rect GetCaretBounds(int charOfs, LayoutResult result) {
        var lines = result.Lines;
        if (lines.Count == 0) {
            return new Rect(0, 0, 1, 16);
        }

        var lineIdx = FindLineIndexForOfs(charOfs, lines);
        var line = lines[lineIdx];
        var posInLine = Math.Clamp(charOfs - line.CharStart, 0, line.CharLen);

        Rect relRect;
        if (posInLine == 0) {
            relRect = new Rect(0, 0, 0, line.Height);
        } else {
            relRect = line.Layout.HitTestTextPosition(posInLine);
        }

        return new Rect(
            relRect.X,
            line.Y + relRect.Y,
            1,
            relRect.Height > 0 ? relRect.Height : line.Height);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static List<(string lineText, int newlineLen)> SplitLogicalLines(string text) {
        var result = new List<(string, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '\n') {
                result.Add((text[start..i], 1));
                start = i + 1;
            } else if (text[i] == '\r') {
                var nlLen = (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                result.Add((text[start..i], nlLen));
                i += nlLen - 1;
                start = i + 1;
            }
        }
        result.Add((text[start..], 0));
        return result;
    }

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

    private static LayoutLine FindLineAt(double y, IReadOnlyList<LayoutLine> lines) {
        var best = lines[0];
        foreach (var line in lines) {
            if (line.Y <= y) {
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
