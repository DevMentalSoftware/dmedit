using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DMEdit.Core.Documents;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Stateless service that converts a document string into a <see cref="LayoutResult"/>
/// containing <see cref="LayoutLine"/> objects suitable for rendering and hit-testing.
/// One LayoutLine is produced per logical line (newline-delimited paragraph).
/// Word-wrap within each line is handled by either the monospace GlyphRun
/// fast path (<see cref="MonoLineLayout"/>) or by Avalonia's <c>TextLayout</c>.
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
    /// <paramref name="rowHeightOverride"/>, if &gt; 0, replaces the computed
    /// per-row pixel height so callers can force pixel-snapping alignment
    /// with their own scroll math.
    /// </summary>
    public LayoutResult LayoutEmpty(
        Typeface typeface, double fontSize, IBrush foreground, double maxWidth,
        double rowHeightOverride = 0) =>
        LayoutLines(new PieceTable(), 0, 1, typeface, fontSize, foreground, maxWidth, 0,
            rowHeightOverride: rowHeightOverride);

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
        long docLength = -1,
        int hangingIndentChars = 0,
        bool useFastTextLayout = true,
        double rowHeightOverride = 0) {

        using var spaceLayout = MakeTextLayout(" ", typeface, fontSize, foreground, double.PositiveInfinity);
        // The caller may pass a pre-snapped row height that matches its own
        // scroll-math (e.g. EditorControl snaps to device pixel multiples to
        // eliminate inter-line jitter).  When both sides use the same value,
        // line Y positions stay in agreement with the scroll extent and the
        // caret visibility check, which matters at large line numbers where
        // a sub-pixel drift per row accumulates past a full line.
        var rowHeight = rowHeightOverride > 0 ? rowHeightOverride : spaceLayout.Height;

        // Resolve a monospace fast-path context once for the whole window.
        // If the typeface doesn't resolve to a single concrete face (e.g. a
        // comma-separated fallback family), or the face isn't monospace, we
        // skip building any MonoLineLayout and everything falls back to
        // TextLayout — the existing proportional-safe path.  The caller can
        // also force the TextLayout path (for font ligatures at the cost of
        // speed and hanging indent) by passing useFastTextLayout: false.
        var monoCtx = useFastTextLayout
            ? TryBuildMonoContext(typeface, fontSize, rowHeight, hangingIndentChars, foreground)
            : null;

        var lines = new List<LayoutLine>();
        var row = 0;
        if (lineCount < 0) lineCount = table.LineCount;
        if (docLength < 0) docLength = table.Length;
        var charOfs = 0L; // character offset accumulator

        // Effective char count for wrap (only meaningful on the mono path).
        // When maxWidth is infinite the line never wraps; pass a very large
        // count so MonoLineLayout treats it as single-row.
        var maxCharsPerRow = int.MaxValue;
        if (monoCtx is not null && double.IsFinite(maxWidth) && maxWidth > 0) {
            maxCharsPerRow = Math.Max(1, (int)(maxWidth / monoCtx.CharWidth));
        }

        for (var lineIdx = topLine; lineIdx < bottomLine && lineIdx < lineCount; lineIdx++) {
            var lineStart = table.LineStartOfs(lineIdx);
            var lineEnd = lineIdx + 1 < lineCount
                ? table.LineStartOfs(lineIdx + 1)
                : docLength;

            // Skip lines with inconsistent offsets — can happen during
            // streaming load when the background scan mutates line data
            // between reads.
            if (lineStart < 0 || lineEnd < lineStart) continue;
            var fullLen = (int)(lineEnd - lineStart);
            var contentLen = table.LineContentLength((int)lineIdx);
            if (contentLen > PieceTable.MaxGetTextLength) {
                throw new LineTooLongException(contentLen, PieceTable.MaxGetTextLength);
            }
            var lineText = contentLen > 0 ? table.GetText(lineStart, contentLen) : "";

            // Try the fast path first.  Returns null for lines with tabs,
            // control characters, or codepoints the font has no glyph for —
            // those lines fall back to TextLayout.
            LayoutLine layoutLine;
            if (monoCtx is not null) {
                var mono = MonoLineLayout.TryBuild(monoCtx, lineText, maxCharsPerRow);
                if (mono is not null) {
                    var heightInRows = Math.Max(1, mono.RowCount);
                    layoutLine = new LayoutLine((int)charOfs, contentLen, row, heightInRows, mono);
                    lines.Add(layoutLine);
                    row += heightInRows;
                    charOfs += fullLen;
                    continue;
                }
            }

            // Slow path: Avalonia TextLayout handles wrap and hit-test.
            // Sanitize binary garbage / lone surrogates first — Avalonia's
            // PerformTextWrapping has known crashes around shaped runs that
            // mix control chars and complex grapheme clusters, which is
            // exactly what binary file viewing produces.  Sanitization is
            // length-preserving so all CharStart/CharLen offsets stay valid.
            var safeText = SanitizeForTextLayout(lineText);
            var slowLayout = MakeTextLayoutSafe(safeText, typeface, fontSize, foreground, maxWidth);
            var h = slowLayout.Height > 0 ? slowLayout.Height : rowHeight;
            var slowHeightInRows = Math.Max(1, (int)Math.Round(h / rowHeight));
            lines.Add(new LayoutLine((int)charOfs, contentLen, row, slowHeightInRows, slowLayout));
            row += slowHeightInRows;
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
        var posInLine = Math.Clamp(line.HitTestPoint(localPt), 0, line.CharLen);
        return line.CharStart + posInLine;
    }

    /// <summary>
    /// Returns the bounding rectangle of the caret at logical offset <paramref name="charOfs"/>,
    /// in layout coordinates (Y is absolute document Y in pixels).
    /// </summary>
    public Rect GetCaretBounds(int charOfs, LayoutResult result,
            bool isAtEnd = false) {
        var lines = result.Lines;
        if (lines.Count == 0) {
            return new Rect(0, 0, 1, 16);
        }

        var rh = result.RowHeight;
        var lineIdx = FindLineIndexForOfs(charOfs, lines);
        var line = lines[lineIdx];
        var posInLine = Math.Clamp(charOfs - line.CharStart, 0, line.CharLen);

        Rect relRect;
        if (posInLine == 0 && !line.IsMono) {
            // TextLayout's HitTestTextPosition(0) can return a degenerate
            // rect with zero height on some Avalonia builds.  Mono path
            // returns a clean rect at column 0 so we skip this shim there.
            relRect = new Rect(0, 0, 0, rh);
        } else {
            relRect = line.HitTestTextPosition(posInLine, isAtEnd);
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

    private static MonoLayoutContext? TryBuildMonoContext(
        Typeface typeface, double fontSize, double rowHeight,
        int hangingIndentChars, IBrush foreground) {
        var gtf = typeface.GlyphTypeface;
        if (gtf is null) return null;
        if (!MonoLayoutContext.IsMonospace(gtf)) return null;
        return new MonoLayoutContext(gtf, fontSize, rowHeight, hangingIndentChars, foreground);
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

    /// <summary>
    /// Builds a <see cref="TextLayout"/> with two layers of fallback for
    /// content that trips Avalonia's text formatter.  The known case (real
    /// user crash 0.5.231 — binary file scrolling) is
    /// <c>InvalidOperationException: Cannot split: requested length N consumes
    /// entire run</c> from <c>ShapedTextRun.Split</c> via
    /// <c>PerformTextWrapping</c>.  Falling back to NoWrap bypasses
    /// <c>PerformTextWrapping</c> entirely; the line will overflow horizontally
    /// rather than crash the dispatcher.  As a last resort we drop the line
    /// content — the <see cref="LayoutLine"/> still occupies its char range
    /// so document offsets remain consistent.
    /// </summary>
    private static TextLayout MakeTextLayoutSafe(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth) {
        try {
            return MakeTextLayout(text, typeface, fontSize, foreground, maxWidth);
        } catch (InvalidOperationException) {
        }
        try {
            return MakeTextLayout(text, typeface, fontSize, foreground, double.PositiveInfinity);
        } catch (InvalidOperationException) {
        }
        return MakeTextLayout("", typeface, fontSize, foreground, double.PositiveInfinity);
    }

    /// <summary>
    /// Replaces characters that Avalonia's text formatter is known to choke
    /// on with U+FFFD REPLACEMENT CHARACTER.  Length-preserving so caller's
    /// CharStart/CharLen offsets remain accurate.  Specifically scrubs:
    /// <list type="bullet">
    /// <item>C0 control characters other than tab (binary file bytes 0x00–0x1F)</item>
    /// <item>Lone (unpaired) UTF-16 surrogates (illegal Unicode that can
    ///       sneak in through external paste / IME / corrupted streams)</item>
    /// </list>
    /// Returns the original instance when the input is already clean (no
    /// allocation in the common case).
    /// </summary>
    internal static string SanitizeForTextLayout(string text) {
        var bad = false;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c < 32 && c != '\t') { bad = true; break; }
            if (char.IsHighSurrogate(c)) {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) {
                    bad = true; break;
                }
                i++;
            } else if (char.IsLowSurrogate(c)) {
                bad = true; break;
            }
        }
        if (!bad) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c < 32 && c != '\t') {
                sb.Append('\uFFFD');
            } else if (char.IsHighSurrogate(c)) {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                } else {
                    sb.Append('\uFFFD');
                }
            } else if (char.IsLowSurrogate(c)) {
                sb.Append('\uFFFD');
            } else {
                sb.Append(c);
            }
        }
        return sb.ToString();
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
