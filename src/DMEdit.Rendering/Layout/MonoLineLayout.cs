using Avalonia;
using Avalonia.Media;
using DMEdit.Core.Documents;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Monospace fast-path layout for one logical line.  Built when the line
/// contains only printable characters (or tabs) that the resolved glyph
/// typeface has glyphs for, and the typeface itself is monospace.  Other
/// control characters force the line back to the <c>TextLayout</c> slow path.
/// Tabs are expanded to column-aligned positions using the editor's indent
/// width as the tab stop interval.
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

    /// <summary>Whether this line contains tabs (enables column-aware positioning).</summary>
    private readonly bool _hasTabs;

    private bool _disposed;

    private MonoLineLayout(MonoLayoutContext context, string text, RowSpan[] rows,
            bool hasTabs) {
        Context = context;
        Text = text;
        Rows = rows;
        _hasTabs = hasTabs;

        _rowRuns = new GlyphRun?[rows.Length];
        var baselinePoint = new Point(0, context.Baseline);
        for (var r = 0; r < rows.Length; r++) {
            var span = rows[r];
            if (span.CharLen == 0) {
                _rowRuns[r] = null;
                continue;
            }
            if (hasTabs) {
                // Tab-aware: Avalonia's GlyphRun doesn't support per-glyph
                // advances, so we skip the cached GlyphRun for tab rows.
                // Draw() handles tab rows by splitting at tab characters
                // and positioning each segment explicitly.
                _rowRuns[r] = null;
            } else {
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
        // Reject control characters other than tab.  Tab is handled via
        // column-aware advance in the tab-aware row breaker.
        // Accept all characters — control chars render as the fallback
        // glyph at one column width.  Only reject characters that the
        // typeface truly can't handle (TryGetGlyph returns false for
        // non-BMP chars without coverage).
        var hasTabs = false;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\t') { hasTabs = true; continue; }
            if (c < 32) continue; // control chars → fallback glyph
            if (!ctx.TryGetGlyph(c, out _)) return null;
        }

        // Effective row widths: first row uses the full column count.
        // Continuation rows are indented by the line's own leading
        // whitespace plus a half-indent (HangingIndentChars), so they
        // lose that many columns.
        var firstRowCols = Math.Max(1, maxCharsPerRow);
        var leadingIndentCols = ctx.HangingIndentChars > 0
            ? MonoRowBreaker.LeadingIndentColumns(text, ctx.TabWidth)
            : 0;
        var totalContIndent = leadingIndentCols + ctx.HangingIndentChars;
        var contRowCols = Math.Max(1, maxCharsPerRow - totalContIndent);
        var contIndentPx = totalContIndent * ctx.CharWidth;

        // Empty line is a single row with zero content.
        if (text.Length == 0) {
            return new MonoLineLayout(ctx, text, [new RowSpan(0, 0, 0)], false);
        }

        if (hasTabs) {
            // Tab-aware row breaking: uses column counts, not char counts.
            var rows = new List<RowSpan>(4);
            var pos = 0;
            var rowIdx = 0;
            while (pos < text.Length) {
                var cols = rowIdx == 0 ? firstRowCols : contRowCols;
                var (_, nextStart) = MonoRowBreaker.NextRowTabAware(
                    text, pos, cols, ctx.TabWidth);
                var xOffset = rowIdx == 0 ? 0.0 : contIndentPx;
                rows.Add(new RowSpan(pos, nextStart - pos, xOffset));
                pos = nextStart;
                rowIdx++;
            }
            return new MonoLineLayout(ctx, text, rows.ToArray(), true);
        }

        // Non-tab fast path (original logic).
        if (text.Length <= firstRowCols) {
            return new MonoLineLayout(ctx, text,
                [new RowSpan(0, text.Length, 0)], false);
        }

        var plainRows = new List<RowSpan>(4);
        var plainPos = 0;
        var plainRowIdx = 0;
        while (plainPos < text.Length) {
            var rowChars = plainRowIdx == 0 ? firstRowCols : contRowCols;
            var (_, nextStart) = MonoRowBreaker.NextRow(text, plainPos, rowChars);
            var xOffset = plainRowIdx == 0 ? 0.0 : contIndentPx;
            plainRows.Add(new RowSpan(plainPos, nextStart - plainPos, xOffset));
            plainPos = nextStart;
            plainRowIdx++;
        }
        return new MonoLineLayout(ctx, text, plainRows.ToArray(), false);
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
        for (var r = 0; r < Rows.Length; r++) {
            var span = Rows[r];
            if (span.CharLen == 0) continue;
            var rowX = origin.X + span.XOffset;
            var rowY = origin.Y + r * Context.RowHeight;

            if (!_hasTabs) {
                // Non-tab fast path: single cached GlyphRun per row.
                var run = _rowRuns[r];
                if (run is null) continue;
                using (context.PushTransform(Matrix.CreateTranslation(rowX, rowY))) {
                    context.DrawGlyphRun(brush, run);
                }
            } else {
                // Tab-aware: split the row at tab characters and draw
                // each text segment at its column-based X position.
                // Tabs themselves are drawn as whitespace (gap).
                var baselinePoint = new Point(0, Context.Baseline);
                var col = 0;
                var segStart = span.CharStart;
                for (var i = span.CharStart; i <= span.CharStart + span.CharLen; i++) {
                    var isEnd = i == span.CharStart + span.CharLen;
                    var isTab = !isEnd && Text[i] == '\t';
                    if (isTab || isEnd) {
                        var segLen = i - segStart;
                        if (segLen > 0) {
                            // Draw the text segment before this tab/end.
                            var segX = rowX + col * Context.CharWidth;
                            var glyphs = new ushort[segLen];
                            for (var g = 0; g < segLen; g++) {
                                Context.TryGetGlyph(Text[segStart + g], out glyphs[g]);
                            }
                            using var run = new GlyphRun(
                                Context.GlyphTypeface,
                                Context.FontSize,
                                Text.AsMemory(segStart, segLen),
                                glyphs,
                                baselinePoint);
                            using (context.PushTransform(Matrix.CreateTranslation(segX, rowY))) {
                                context.DrawGlyphRun(brush, run);
                            }
                            col += segLen;
                        }
                        if (isTab) {
                            // Advance column to next tab stop.
                            col = (col / Context.TabWidth + 1) * Context.TabWidth;
                            segStart = i + 1;
                        }
                    }
                }
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
    /// <param name="charInLine">Character offset within this line.</param>
    /// <param name="isAtEnd">When true and the caret sits at a soft
    /// line break boundary (at or before a continuation row's first char),
    /// render at the end of the <em>previous</em> row instead of the start
    /// of the next.  Used by End key and left-arrow so the caret can park
    /// at the right edge of a wrapped row.</param>
    /// <summary>
    /// Returns the column (screen position) of <paramref name="charInLine"/>
    /// relative to the start of its row, accounting for tab expansion.
    /// For non-tab lines this is just <c>charInLine - rowStart</c>.
    /// </summary>
    private int ColumnInRow(int charInLine, RowSpan span) {
        if (!_hasTabs) return Math.Max(0, charInLine - span.CharStart);
        return MonoRowBreaker.ColumnOfChar(Text, span.CharStart,
            Math.Min(charInLine, span.CharStart + span.CharLen),
            Context.TabWidth);
    }

    /// <summary>
    /// Returns the total column width of a row (for isAtEnd positioning).
    /// </summary>
    private int RowColumnWidth(RowSpan span) {
        if (!_hasTabs) return span.CharLen;
        return MonoRowBreaker.ColumnOfChar(Text, span.CharStart,
            span.CharStart + span.CharLen, Context.TabWidth);
    }

    public Rect GetCaretBounds(int charInLine, bool isAtEnd = false) {
        if (Rows.Length == 0) return new Rect(0, 0, 0, Context.RowHeight);
        var clamped = Math.Clamp(charInLine, 0, Text.Length);
        var r = RowForChar(clamped);
        var span = Rows[r];

        // Left affinity at a soft-break boundary: render at the end of the
        // previous row.
        if (isAtEnd && r > 0 && clamped <= span.CharStart) {
            var prev = Rows[r - 1];
            var prevCols = RowColumnWidth(prev);
            var x = prev.XOffset + prevCols * Context.CharWidth;
            var y = (r - 1) * Context.RowHeight;
            return new Rect(x, y, 0, Context.RowHeight);
        }

        var col = ColumnInRow(clamped, span);
        var x2 = span.XOffset + col * Context.CharWidth;
        var y2 = r * Context.RowHeight;
        return new Rect(x2, y2, 0, Context.RowHeight);
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
        var targetCol = (int)Math.Round(localX / Context.CharWidth);

        if (!_hasTabs) {
            var col = Math.Clamp(targetCol, 0, span.CharLen);
            return span.CharStart + col;
        }

        // Tab-aware: walk characters accumulating columns until we
        // reach or pass the target column.
        var cumCol = 0;
        for (var i = 0; i < span.CharLen; i++) {
            var c = Text[span.CharStart + i];
            var cw = MonoRowBreaker.CharColumns(c, cumCol, Context.TabWidth);
            var mid = cumCol + cw / 2.0;
            if (targetCol <= mid) return span.CharStart + i;
            cumCol += cw;
        }
        return span.CharStart + span.CharLen;
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
            var lo = Math.Max(rangeStart, rowStart);
            var hi = Math.Min(rangeEnd, rowEnd);
            if (hi <= lo && !(r == Rows.Length - 1 && rangeEnd == Text.Length)) continue;
            if (hi < lo) hi = lo;

            double x, w;
            if (_hasTabs) {
                var loCol = MonoRowBreaker.ColumnOfChar(Text, rowStart, lo, Context.TabWidth);
                var hiCol = MonoRowBreaker.ColumnOfChar(Text, rowStart, hi, Context.TabWidth);
                x = span.XOffset + loCol * Context.CharWidth;
                w = (hiCol - loCol) * Context.CharWidth;
            } else {
                x = span.XOffset + (lo - rowStart) * Context.CharWidth;
                w = (hi - lo) * Context.CharWidth;
            }
            var y = r * Context.RowHeight;
            yield return new Rect(x, y, w, Context.RowHeight);
        }
    }
}

/// <summary>
/// One wrapped row inside a <see cref="MonoLineLayout"/>.
/// </summary>
/// <param name="CharStart">Character index (in the line) where this row begins.</param>
/// <param name="CharLen">Total characters owned by this row, including the trailing
/// break-space at a word-wrap boundary.  For any two adjacent rows,
/// <c>Rows[r].CharStart + Rows[r].CharLen == Rows[r+1].CharStart</c> —
/// no gaps between rows.</param>
/// <param name="XOffset">Pixel X offset from the line origin — 0 for the first
/// row; for continuation rows, the line's leading whitespace columns plus
/// <see cref="MonoLayoutContext.HangingIndentChars"/> converted to pixels.</param>
public readonly record struct RowSpan(int CharStart, int CharLen, double XOffset);
