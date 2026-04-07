using Avalonia;
using Avalonia.Media;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Monospace fast-path layout for one logical line.  Built when the line
/// contains only printable characters that the resolved glyph typeface has
/// glyphs for, and the typeface itself is monospace.  Tab and other control
/// characters force the line back to the <c>TextLayout</c> slow path.
/// </summary>
/// <remarks>
/// Stores per-row spans so wrapped continuation rows can be drawn at an
/// X offset (the hanging indent).  All draw and hit-test operations are
/// pure arithmetic on the cached <see cref="MonoLayoutContext.CharWidth"/>
/// and the row span list — no <c>TextLayout</c>, no font shaping, no
/// per-call allocation beyond the GlyphRun built at draw time.
/// </remarks>
public sealed class MonoLineLayout : IDisposable {
    public MonoLayoutContext Context { get; }

    /// <summary>The line text (no trailing newline).  Owned by this instance.</summary>
    public string Text { get; }

    /// <summary>
    /// Per-row spans, in document order.  Each entry covers a range of
    /// characters from <see cref="Text"/>.  Continuation rows (index &gt; 0)
    /// are drawn with an X offset of <see cref="MonoLayoutContext.HangingIndentPx"/>.
    /// </summary>
    public RowSpan[] Rows { get; }

    public int RowCount => Rows.Length;

    // Pre-built GlyphRun per row.  Cached once at construction so that a
    // full redraw (caret blink, scroll, selection change, etc.) does not
    // allocate per-row ushort[] + GlyphRun + glyph shape data every frame.
    // Each cached GlyphRun's BaselineOrigin is fixed at (0, baseline) — we
    // use DrawingContext.PushTransform at draw time to move the row into
    // place without mutating the GlyphRun (which may be captured by
    // reference into Avalonia's deferred scene).
    private readonly GlyphRun?[] _rowRuns;
    private bool _disposed;

    private MonoLineLayout(MonoLayoutContext context, string text, RowSpan[] rows) {
        Context = context;
        Text = text;
        Rows = rows;

        _rowRuns = new GlyphRun?[rows.Length];
        var baselinePoint = new Point(0, context.Baseline);
        for (var r = 0; r < rows.Length; r++) {
            var span = rows[r];
            if (span.CharLen == 0) {
                _rowRuns[r] = null;
                continue;
            }
            var glyphs = new ushort[span.CharLen];
            for (var i = 0; i < span.CharLen; i++) {
                context.TryGetGlyph(text[span.CharStart + i], out glyphs[i]);
            }
            _rowRuns[r] = new GlyphRun(
                context.GlyphTypeface,
                context.FontSize,
                text.AsMemory(span.CharStart, span.CharLen),
                glyphs,
                baselinePoint);
        }
    }

    /// <summary>
    /// Builds a <see cref="MonoLineLayout"/> for <paramref name="text"/> by
    /// walking the line and producing word-break row spans up to
    /// <paramref name="maxCharsPerRow"/> per row, with continuation rows
    /// shrunk by <see cref="MonoLayoutContext.HangingIndentChars"/>.
    /// Returns null if the line contains a tab or any character that the
    /// typeface has no glyph for — those lines must use the slow path.
    /// </summary>
    public static MonoLineLayout? TryBuild(MonoLayoutContext ctx, string text, int maxCharsPerRow) {
        // Reject any control character — tab, CR, LF, etc.  Tabs in
        // particular need column-aware advance, which the fast path
        // does not yet implement.
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c < 32) return null;
            if (!ctx.TryGetGlyph(c, out _)) return null;
        }

        // Effective row widths: first row uses the full column count,
        // continuation rows lose HangingIndentChars columns to the indent.
        var firstRowChars = Math.Max(1, maxCharsPerRow);
        var contRowChars = Math.Max(1, maxCharsPerRow - ctx.HangingIndentChars);

        // Empty line is a single row with zero content.
        if (text.Length == 0) {
            return new MonoLineLayout(ctx, text, [new RowSpan(0, 0, 0)]);
        }

        // Short-line fast path: single row, no wrap math.
        if (text.Length <= firstRowChars) {
            return new MonoLineLayout(ctx, text, [new RowSpan(0, text.Length, 0)]);
        }

        var rows = new List<RowSpan>(4);
        var pos = 0;
        var rowIdx = 0;
        while (pos < text.Length) {
            var rowChars = rowIdx == 0 ? firstRowChars : contRowChars;
            var (drawLen, nextStart) = NextRow(text, pos, rowChars);
            var xOffset = rowIdx == 0 ? 0.0 : ctx.HangingIndentPx;
            rows.Add(new RowSpan(pos, drawLen, xOffset));
            pos = nextStart;
            rowIdx++;
        }
        return new MonoLineLayout(ctx, text, rows.ToArray());
    }

    /// <summary>
    /// Computes the next row's draw length and next row start position.
    /// Backwards-scans for a space inside the row width to break at;
    /// drops the space from the drawn row and skips it for the next row.
    /// Falls back to a hard mid-token break if no space is found.
    /// Mirrors <c>WpfPrintService.PlainTextPaginator.NextRow</c>.
    /// </summary>
    private static (int DrawLen, int NextStart) NextRow(string line, int rowStart, int charsPerRow) {
        var remaining = line.Length - rowStart;
        if (remaining <= charsPerRow) {
            return (remaining, line.Length);
        }
        var hardLimit = rowStart + charsPerRow;
        for (var i = hardLimit - 1; i > rowStart; i--) {
            if (line[i] == ' ') {
                return (i - rowStart, i + 1);
            }
        }
        return (charsPerRow, hardLimit);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws every row of this line into <paramref name="context"/> at
    /// (<paramref name="origin"/>.X + per-row X offset, origin.Y + row top).
    /// Uses the cached per-row <see cref="GlyphRun"/>s; the only per-draw
    /// work is one <see cref="DrawingContext.PushTransform"/> and one
    /// <see cref="DrawingContext.DrawGlyphRun"/> per row.
    /// </summary>
    public void Draw(DrawingContext context, Point origin, IBrush? foreground = null) {
        var brush = foreground ?? Context.Foreground;
        for (var r = 0; r < _rowRuns.Length; r++) {
            var run = _rowRuns[r];
            if (run is null) continue;
            var span = Rows[r];
            var x = origin.X + span.XOffset;
            var y = origin.Y + r * Context.RowHeight;
            using (context.PushTransform(Matrix.CreateTranslation(x, y))) {
                context.DrawGlyphRun(brush, run);
            }
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < _rowRuns.Length; i++) {
            _rowRuns[i]?.Dispose();
            _rowRuns[i] = null;
        }
    }

    // -------------------------------------------------------------------------
    // Hit-testing — pure arithmetic
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the row index containing <paramref name="charInLine"/>, or
    /// the last row if the position is at end-of-line.
    /// </summary>
    public int RowForChar(int charInLine) {
        // Walk forwards; the row count is small (1–N where N is rare).
        for (var r = 0; r < Rows.Length; r++) {
            var span = Rows[r];
            if (charInLine < span.CharStart + span.CharLen) {
                return r;
            }
        }
        return Rows.Length - 1;
    }

    /// <summary>
    /// Returns the bounding rectangle of the caret immediately before
    /// <paramref name="charInLine"/>, in line-local coordinates.
    /// </summary>
    public Rect GetCaretBounds(int charInLine) {
        if (Rows.Length == 0) return new Rect(0, 0, 0, Context.RowHeight);
        var clamped = Math.Clamp(charInLine, 0, Text.Length);
        var r = RowForChar(clamped);
        var span = Rows[r];
        var col = clamped - span.CharStart;
        if (col < 0) col = 0;
        var x = span.XOffset + col * Context.CharWidth;
        var y = r * Context.RowHeight;
        return new Rect(x, y, 0, Context.RowHeight);
    }

    /// <summary>
    /// Returns the character offset (relative to the start of this line)
    /// closest to the given line-local point.
    /// </summary>
    public int HitTestPoint(Point local) {
        if (Rows.Length == 0) return 0;
        var rIdx = (int)(local.Y / Context.RowHeight);
        if (rIdx < 0) rIdx = 0;
        if (rIdx >= Rows.Length) rIdx = Rows.Length - 1;
        var span = Rows[rIdx];
        var localX = local.X - span.XOffset;
        if (localX < 0) localX = 0;
        var col = (int)Math.Round(localX / Context.CharWidth);
        if (col < 0) col = 0;
        if (col > span.CharLen) col = span.CharLen;
        return span.CharStart + col;
    }

    /// <summary>
    /// Returns the bounding rectangles for the character range
    /// [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>),
    /// one rect per affected row.  Coordinates are line-local.
    /// </summary>
    public IEnumerable<Rect> HitTestTextRange(int start, int length) {
        if (length <= 0 || Rows.Length == 0) yield break;
        var rangeStart = Math.Clamp(start, 0, Text.Length);
        var rangeEnd = Math.Clamp(start + length, 0, Text.Length);
        if (rangeEnd <= rangeStart) yield break;

        for (var r = 0; r < Rows.Length; r++) {
            var span = Rows[r];
            var rowStart = span.CharStart;
            var rowEnd = span.CharStart + span.CharLen;
            // Treat the trailing edge of the last row inclusively so the
            // selection extends to the end of the line.
            var lo = Math.Max(rangeStart, rowStart);
            var hi = Math.Min(rangeEnd, rowEnd);
            if (hi <= lo && !(r == Rows.Length - 1 && rangeEnd == Text.Length)) continue;
            if (hi < lo) hi = lo;
            var x = span.XOffset + (lo - rowStart) * Context.CharWidth;
            var w = (hi - lo) * Context.CharWidth;
            var y = r * Context.RowHeight;
            yield return new Rect(x, y, w, Context.RowHeight);
        }
    }
}

/// <summary>
/// One wrapped row inside a <see cref="MonoLineLayout"/>.
/// </summary>
/// <param name="CharStart">Character index (in the line) where this row begins.</param>
/// <param name="CharLen">Number of characters drawn on this row.  Excludes the
/// trailing space at a word-break boundary.</param>
/// <param name="XOffset">Pixel X offset from the line origin — 0 for the first
/// row, <see cref="MonoLayoutContext.HangingIndentPx"/> for continuation rows.</param>
public readonly record struct RowSpan(int CharStart, int CharLen, double XOffset);
