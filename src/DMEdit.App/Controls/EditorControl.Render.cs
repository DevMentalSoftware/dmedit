using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DMEdit.Core.Documents;
using DMEdit.Rendering.Layout;

namespace DMEdit.App.Controls;

// Rendering partial of EditorControl.  Owns Render override, the
// gutter / wrap-symbol / whitespace drawing, the selection and
// column-selection geometry, and the caret-blink helpers
// (OnCaretTick / ResetCaretBlink / SetCaretLayersVisible).  Shared
// fields live in the main EditorControl.cs.
public sealed partial class EditorControl {

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext context) {
        _perfSw.Restart();
        PerfStats.RenderCalls++;
        _inRenderPass = true;
        try {
            RenderCore(context);
        } finally {
            _inRenderPass = false;
        }
    }

    private void RenderCore(DrawingContext context) {
        var layout = EnsureLayout();
        var doc = BackgroundPasteInProgress ? null : Document;

        // Background
        context.FillRectangle(_theme.EditorBackground, new Rect(Bounds.Size));

        // Gutter (line numbers)
        DrawGutter(context, layout);

        // Current-line highlight — drawn after the gutter so it overlays
        // line numbers, but before text/selection so those paint on top.
        // Skipped during column selection where "the current row" is
        // ambiguous (there are multiple cursors).
        if (_highlightCurrentLine && doc is not null && doc.ColumnSel is null) {
            DrawCurrentLineHighlight(context, layout, doc);
        }

        // Clip text area so horizontally-scrolled content doesn't paint over the gutter.
        using var _ = _scrollOffset.X > 0
            ? context.PushClip(new Rect(_gutterWidth, 0, Bounds.Width - _gutterWidth, Bounds.Height))
            : default;

        // Column guide line
        if (_wrapLines && WrapColumnActive) {
            var colLimit = _wrapLinesAt * GetCharWidth();
            var available = Math.Max(100, Bounds.Width - _gutterWidth)
                - (_showWrapSymbol ? WrapSymbolPadRight : 0);
            // Only draw the guide when the column limit is actually constraining.
            if (colLimit < available) {
                var guideX = 2 + TextOriginX + colLimit;
                if (guideX < Bounds.Width) {
                    context.DrawLine(_theme.GuideLinePen, new Point(guideX, 0), new Point(guideX, Bounds.Height));
                }
            }
        }

        // Draw selection rectangles behind text
        if (doc != null) {
            if (doc.ColumnSel is { } colSel) {
                DrawColumnSelection(context, layout, colSel);
            } else if (!doc.Selection.IsEmpty) {
                DrawSelection(context, layout, doc.Selection);
            }
        }

        // Draw each visible line's text
        var rh = layout.RowHeight;
        foreach (var line in layout.Lines) {
            var y = line.Row * rh + RenderOffsetY;
            if (y + line.HeightInRows * rh < 0) {
                continue; // above viewport
            }
            if (y > Bounds.Height) {
                break; // below viewport
            }
            line.Render(context, new Point(TextOriginX, y), ForegroundBrush);
            if (_showWhitespace) {
                DrawWhitespace(context, layout, line, y, rh);
            }
        }

        // Draw wrap symbols for lines that word-wrap
        if (_showWrapSymbol && _wrapLines && !_charWrapMode) {
            DrawWrapSymbols(context, layout, rh);
        }

        // Caret is rendered by the CaretLayer child controls (positioned by
        // ArrangeOverride / UpdateCaretLayers) so caret blink invalidates
        // only the layer, not this control.  We can't call Arrange from
        // inside Render (Avalonia forbids it: "Visual was invalidated
        // during the render pass"), so any call site that mutates the
        // caret position must call InvalidateArrange explicitly — see
        // the scroll setter, OnPointerMoved, ResetCaretBlink, etc.

        _perfSw.Stop();
        PerfStats.Render.Record(_perfSw.Elapsed.TotalMilliseconds);
        PerfStats.SampleMemory();
        StatusUpdated?.Invoke();
    }

    private void DrawGutter(DrawingContext context, LayoutResult layout) {
        if (_gutterWidth <= 0) return;

        var rh = layout.RowHeight;
        var table = Document?.Table;

        // Gutter background
        context.FillRectangle(_theme.GutterBackground, new Rect(0, 0, _gutterWidth, Bounds.Height));

        if (!_showLineNumbers || table == null || layout.Lines.Count == 0) return;

        var fontSize = EffectiveFontSize;
        var numW = _gutterWidth - GutterPadRight;
        var brush = _theme.GutterForeground;
        var fontFam = FontFamily.Name;

        // Invalidate the cache if anything that affects the rendered layout
        // of a digit string has changed.
        if (_gutterCacheFontSize != fontSize
            || _gutterCacheMaxWidth != numW
            || !ReferenceEquals(_gutterCacheBrush, brush)
            || _gutterCacheFontFamily != fontFam) {
            foreach (var cached in _gutterNumCache.Values) cached.Dispose();
            _gutterNumCache.Clear();
            _gutterCacheFontSize = fontSize;
            _gutterCacheMaxWidth = numW;
            _gutterCacheBrush = brush;
            _gutterCacheFontFamily = fontFam;
        }

        var typeface = new Typeface(FontFamily);
        var firstLineIdx = layout.TopLine;

        for (var i = 0; i < layout.Lines.Count; i++) {
            var line = layout.Lines[i];
            var y = line.Row * rh + RenderOffsetY;
            if (y + rh < 0) continue;
            if (y > Bounds.Height) break;

            var lineNum = firstLineIdx + i + 1;
            var numText = lineNum.ToString();
            if (!_gutterNumCache.TryGetValue(numText, out var tl)) {
                tl = new TextLayout(
                    numText, typeface, fontSize, brush,
                    textAlignment: TextAlignment.Right,
                    maxWidth: numW);
                _gutterNumCache[numText] = tl;
                // Soft cap so the cache doesn't grow unbounded if the user
                // jumps around a huge file.  Drop the oldest half when we
                // exceed the cap — crude but bounded.
                if (_gutterNumCache.Count > 512) {
                    var toRemove = _gutterNumCache.Keys.Take(256).ToList();
                    foreach (var k in toRemove) {
                        _gutterNumCache[k].Dispose();
                        _gutterNumCache.Remove(k);
                    }
                }
            }
            tl.Draw(context, new Point(0, y));
        }
    }

    private void DrawWrapSymbols(DrawingContext ctx, LayoutResult layout, double rh) {
        // Place the icon at the actual wrap edge — the same point that
        // GetTextWidth computes.  This tracks smoothly through the
        // transition between column-limited and viewport-limited wrapping.
        var extentW = Math.Max(100, _viewport.Width - _gutterWidth);
        var textW = GetTextWidth(extentW);
        if (!double.IsFinite(textW)) return; // wrapping off
        var symbolX = TextOriginX + textW;

        // Draw a wrap indicator using the Fluent icon font.  Cache the
        // TextLayout across renders — it depends only on font size and
        // brush, both stable between caret blinks.
        var brush = _theme.WrapSymbolPen.Brush;
        var fontSize = EffectiveFontSize * 0.8;
        if (_wrapSymbolLayout is null
            || _wrapSymbolLayoutFontSize != fontSize
            || !ReferenceEquals(_wrapSymbolLayoutBrush, brush)) {
            _wrapSymbolLayout?.Dispose();
            _wrapSymbolLayout = new TextLayout(
                IconGlyphs.WrapEnd, IconGlyphs.Face, fontSize, brush);
            _wrapSymbolLayoutFontSize = fontSize;
            _wrapSymbolLayoutBrush = brush;
        }
        var glyphLayout = _wrapSymbolLayout;
        var glyphH = glyphLayout.Height;
        var glyphW = glyphLayout.WidthIncludingTrailingWhitespace;

        foreach (var line in layout.Lines) {
            if (line.HeightInRows <= 1) continue;
            var lineY = line.Row * rh + RenderOffsetY;
            if (lineY + line.HeightInRows * rh < 0) continue;
            if (lineY > Bounds.Height) break;

            for (var r = 0; r < line.HeightInRows - 1; r++) {
                // Center the icon in the reserved padding gap between
                // the wrap edge and the scrollbar.
                var x = 1 + symbolX + (WrapSymbolPadRight - glyphW) * 0.5;
                var y = lineY + r * rh + (rh - glyphH) * 0.5;
                glyphLayout.Draw(ctx, new Point(x, y));
            }
        }
    }

    private void DrawWhitespace(DrawingContext ctx, LayoutResult layout, LayoutLine line, double y, double rh) {
        var table = Document?.Table;
        if (table == null || line.CharLen <= 0) return;

        var ofs = layout.ViewportBase + line.CharStart;
        if (ofs < 0 || line.CharLen > PieceTable.MaxGetTextLength) return;
        if (ofs + line.CharLen > table.Length) return;
        var text = table.GetText(ofs, line.CharLen);
        var typeface = new Typeface(FontFamily);

        // In char-wrap mode, read the raw text (before sanitization) to detect
        // CR/LF characters that were replaced with spaces.
        string? rawText = null;
        if (_charWrapMode && ofs + line.CharLen <= table.Length) {
            rawText = table.GetText(ofs, line.CharLen);
        }

        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            // In char-wrap mode, check the raw character for CR/LF.
            var rawCh = rawText != null && i < rawText.Length ? rawText[i] : ch;
            if (ch != ' ' && ch != '\t' && ch != '\u00A0'
                && rawCh != '\r' && rawCh != '\n') continue;

            var hit = line.HitTestTextPosition(i);
            var x = TextOriginX + hit.X;

            if (ch == '\t') {
                // Draw arrow spanning the tab's width
                var hitNext = line.HitTestTextPosition(i + 1);
                var x1 = TextOriginX + hit.X;
                var x2 = TextOriginX + hitNext.X;
                if (x2 <= x1) continue;

                const double pad = 2;
                const double arrowSize = 3;
                var midY = y + hit.Y + rh / 2;
                var left = x1 + pad;
                var right = x2 - pad;
                if (right - left < arrowSize + 1) continue;

                // Horizontal line
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(left, midY), new Point(right, midY));

                // Arrowhead
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(right - arrowSize, midY - arrowSize),
                    new Point(right, midY));
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(right - arrowSize, midY + arrowSize),
                    new Point(right, midY));
            } else if (rawCh == '\r' || rawCh == '\n') {
                // CR/LF in char-wrap mode — draw a centered glyph.
                var glyph = rawCh == '\r' ? "CR" : "LF";
                using var tl = new TextLayout(glyph, typeface, EffectiveFontSize * 0.5,
                    _theme.WhitespaceGlyphBrush, textAlignment: TextAlignment.Left);
                var glyphW = tl.WidthIncludingTrailingWhitespace;
                var cw = GetCharWidth();
                var dx = (cw - glyphW) / 2;
                var dy = rh * 0.28; // vertically center the smaller text
                tl.Draw(ctx, new Point(x + dx, y + hit.Y + dy));
            } else {
                // Space → · (U+00B7), NBSP → ␣ (U+2423)
                var glyph = ch == ' ' ? "\u00B7" : "\u2423";
                using var tl = new TextLayout(glyph, typeface, EffectiveFontSize,
                    _theme.WhitespaceGlyphBrush, textAlignment: TextAlignment.Left);
                // Center the glyph in the character cell
                var glyphW = tl.WidthIncludingTrailingWhitespace;
                var cw = GetCharWidth();
                var dx = (cw - glyphW) / 2;
                tl.Draw(ctx, new Point(x + dx, y + hit.Y));
            }
        }
    }

    private const double SelCornerRadiusBase = 3.0;
    private double SelCornerRadius => SelCornerRadiusBase * _zoomPercent / 100.0;

    /// <summary>
    /// Draws a translucent rounded-rect band across the editor's full width
    /// at the row containing the primary caret.  Caller ensures the
    /// highlight setting is on and no column selection is active.
    /// </summary>
    private void DrawCurrentLineHighlight(DrawingContext context, LayoutResult layout, Document doc) {
        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (localCaret < 0 || localCaret > totalChars) return;

        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);
        var y = caretRect.Y + RenderOffsetY;
        var h = caretRect.Height;
        if (y + h < 0 || y > Bounds.Height) return;

        context.DrawRectangle(
            _theme.CurrentLineHighlight,
            pen: null,
            new Rect(0, y, Bounds.Width, h));
    }

    private void DrawSelection(DrawingContext context, LayoutResult layout, Selection sel) {
        var localStart = (int)(sel.Start - layout.ViewportBase);
        var localEnd = (int)(sel.End - layout.ViewportBase);

        // Clamp to the visible range
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (localEnd < 0 || localStart > totalChars) {
            return;
        }
        localStart = Math.Clamp(localStart, 0, totalChars);
        localEnd = Math.Clamp(localEnd, 0, totalChars);
        if (localStart == localEnd) {
            return;
        }

        // Collect all selection rects so we know first/last for corner rounding.
        var rh = layout.RowHeight;
        var rects = new List<Rect>();
        foreach (var line in layout.Lines) {
            var lineY = line.Row * rh + RenderOffsetY;
            if (lineY + line.HeightInRows * rh < 0) {
                continue;
            }
            if (lineY > Bounds.Height) {
                break;
            }
            if (line.CharEnd <= localStart || line.CharStart >= localEnd) {
                // Blank lines have CharEnd == CharStart, so the check above
                // rejects them when they sit exactly at localStart.  Let them
                // through when they fall inside [localStart, localEnd).
                if (!(line.CharLen == 0 && line.CharStart >= localStart && line.CharStart < localEnd))
                    continue;
            }

            var rangeStart = Math.Max(0, localStart - line.CharStart);
            var rangeEnd = Math.Min(line.CharLen, localEnd - line.CharStart);
            var rangeLen = rangeEnd - rangeStart;

            if (rangeLen <= 0) {
                // Blank line fully inside selection: show a 1-char-wide placeholder
                // so the user can see that the selection spans across it.
                if (line.CharLen == 0) {
                    rects.Add(new Rect(TextOriginX, lineY, GetCharWidth(), rh));
                }
                continue;
            }

            foreach (var rect in line.HitTestTextRange(rangeStart, rangeLen)) {
                rects.Add(new Rect(rect.X + TextOriginX, lineY + rect.Y, rect.Width, rect.Height));
            }
        }

        if (rects.Count == 0) {
            return;
        }

        if (rects.Count == 1) {
            var r = SelCornerRadius;
            FillRoundedRect(context, SelectionBrush, rects[0], r, r, r, r);
        } else {
            // Build a single outline path for all contiguous rects so there
            // are no internal horizontal edges (which cause sub-pixel seams).
            FillSelectionPath(context, SelectionBrush, rects, SelCornerRadius);
        }
    }

    /// <summary>
    /// Draws selection highlight for a list of rects.  Adjacent rects that
    /// don't overlap horizontally are split into separate groups so the
    /// path never self-intersects (which would leave unfilled holes under
    /// the default EvenOdd fill rule).  Each group is drawn as a single
    /// clockwise contour with rounded corners at both outer edges and
    /// internal row-boundary steps.
    /// </summary>
    private static void FillSelectionPath(
        DrawingContext ctx, IBrush brush, List<Rect> rects, double r) {
        // Split into groups of horizontally overlapping rects.
        var groupStart = 0;
        for (var i = 1; i <= rects.Count; i++) {
            var split = i == rects.Count
                || rects[i - 1].Right <= rects[i].Left
                || rects[i].Right <= rects[i - 1].Left
                || rects[i].Top - rects[i - 1].Bottom > 0.5;
            if (!split) continue;
            var count = i - groupStart;
            if (count == 1) {
                FillRoundedRect(ctx, brush, rects[groupStart], r, r, r, r);
            } else {
                FillSelectionGroup(ctx, brush, rects, groupStart, count, r);
            }
            groupStart = i;
        }
    }

    /// <summary>
    /// Traces the outer contour of a contiguous group of rects as a single
    /// filled path.  Rounded corners at the four outermost corners and at
    /// every internal step where adjacent rows have different Left or Right
    /// edges.  The contour is traced clockwise; convex (outward) corners
    /// use CW arcs and concave (inward) corners use CCW arcs.
    /// </summary>
    private static void FillSelectionGroup(
        DrawingContext ctx, IBrush brush, List<Rect> rects,
        int start, int count, double r) {
        var g = BuildSelectionGroupGeometry(rects, start, count, r);
        ctx.DrawGeometry(brush, null, g);
    }

    /// <summary>
    /// One arc emitted by <see cref="BuildSelectionGroupGeometry"/>.
    /// Recorded when an <c>arcLog</c> is supplied so tests can verify
    /// that every arc has the correct sweep direction.
    /// </summary>
    internal record struct ArcRecord(string Label, SweepDirection Sweep);

    /// <summary>
    /// Builds the <see cref="StreamGeometry"/> for a contiguous group of
    /// selection rects.  When <paramref name="arcLog"/> is non-null, each
    /// <c>ArcTo</c> call also appends an <see cref="ArcRecord"/> so tests
    /// can verify sweep directions without relying on <c>FillContains</c>
    /// (which Avalonia headless doesn't support for arc geometries).
    /// </summary>
    internal static StreamGeometry BuildSelectionGroupGeometry(
        List<Rect> rects, int start, int count, double r,
        List<ArcRecord>? arcLog = null) {
        var first = rects[start];
        var last = rects[start + count - 1];
        var end = start + count;

        var g = new StreamGeometry();
        using (var c = g.Open()) {
            // Start at top-left of first rect, trace clockwise.

            // ── Top edge ──
            c.BeginFigure(new Point(first.Left + r, first.Top), true);
            c.LineTo(new Point(first.Right - r, first.Top));
            c.ArcTo(new Point(first.Right, first.Top + r),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            arcLog?.Add(new ArcRecord("outer-TR", SweepDirection.Clockwise));

            // ── Right edge (top → bottom) ──
            for (var i = start; i < end - 1; i++) {
                var cur = rects[i];
                var next = rects[i + 1];
                if (Math.Abs(cur.Right - next.Right) > 0.5) {
                    if (next.Right < cur.Right) {
                        // Step inward — selection narrows.
                        // DOWN→LEFT = CW (convex), LEFT→DOWN = CCW (concave).
                        c.LineTo(new Point(cur.Right, cur.Bottom - r));
                        c.ArcTo(new Point(cur.Right - r, cur.Bottom),
                            new Size(r, r), 0, false, SweepDirection.Clockwise);
                        arcLog?.Add(new ArcRecord($"R-in[{i}]-convex", SweepDirection.Clockwise));
                        c.LineTo(new Point(next.Right + r, next.Top));
                        c.ArcTo(new Point(next.Right, next.Top + r),
                            new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                        arcLog?.Add(new ArcRecord($"R-in[{i}]-concave", SweepDirection.CounterClockwise));
                    } else {
                        // Step outward — selection widens.
                        // DOWN→RIGHT = CCW (concave), RIGHT→DOWN = CW (convex).
                        c.LineTo(new Point(cur.Right, cur.Bottom - r));
                        c.ArcTo(new Point(cur.Right + r, cur.Bottom),
                            new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                        arcLog?.Add(new ArcRecord($"R-out[{i}]-concave", SweepDirection.CounterClockwise));
                        c.LineTo(new Point(next.Right - r, next.Top));
                        c.ArcTo(new Point(next.Right, next.Top + r),
                            new Size(r, r), 0, false, SweepDirection.Clockwise);
                        arcLog?.Add(new ArcRecord($"R-out[{i}]-convex", SweepDirection.Clockwise));
                    }
                } else {
                    c.LineTo(new Point(cur.Right, cur.Bottom));
                }
            }

            // ── Bottom edge ──
            c.LineTo(new Point(last.Right, last.Bottom - r));
            c.ArcTo(new Point(last.Right - r, last.Bottom),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            arcLog?.Add(new ArcRecord("outer-BR", SweepDirection.Clockwise));
            c.LineTo(new Point(last.Left + r, last.Bottom));
            c.ArcTo(new Point(last.Left, last.Bottom - r),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            arcLog?.Add(new ArcRecord("outer-BL", SweepDirection.Clockwise));

            // ── Left edge (bottom → top) ──
            for (var i = end - 1; i > start; i--) {
                var cur = rects[i];
                var prev = rects[i - 1];
                if (Math.Abs(cur.Left - prev.Left) > 0.5) {
                    if (prev.Left < cur.Left) {
                        // Step outward going up — selection widens.
                        // UP→LEFT = CCW (concave), LEFT→UP = CW (convex).
                        c.LineTo(new Point(cur.Left, cur.Top + r));
                        c.ArcTo(new Point(cur.Left - r, cur.Top),
                            new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                        arcLog?.Add(new ArcRecord($"L-out[{i}]-concave", SweepDirection.CounterClockwise));
                        c.LineTo(new Point(prev.Left + r, cur.Top));
                        c.ArcTo(new Point(prev.Left, cur.Top - r),
                            new Size(r, r), 0, false, SweepDirection.Clockwise);
                        arcLog?.Add(new ArcRecord($"L-out[{i}]-convex", SweepDirection.Clockwise));
                    } else {
                        // Step inward going up — selection narrows.
                        // UP→RIGHT = CW (convex), RIGHT→UP = CCW (concave).
                        c.LineTo(new Point(cur.Left, cur.Top + r));
                        c.ArcTo(new Point(cur.Left + r, cur.Top),
                            new Size(r, r), 0, false, SweepDirection.Clockwise);
                        arcLog?.Add(new ArcRecord($"L-in[{i}]-convex", SweepDirection.Clockwise));
                        c.LineTo(new Point(prev.Left - r, cur.Top));
                        c.ArcTo(new Point(prev.Left, cur.Top - r),
                            new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                        arcLog?.Add(new ArcRecord($"L-in[{i}]-concave", SweepDirection.CounterClockwise));
                    }
                } else {
                    c.LineTo(new Point(cur.Left, cur.Top));
                }
            }

            // ── Close at top-left ──
            c.LineTo(new Point(first.Left, first.Top + r));
            c.ArcTo(new Point(first.Left + r, first.Top),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            arcLog?.Add(new ArcRecord("outer-TL", SweepDirection.Clockwise));
            c.EndFigure(true);
        }
        return g;
    }

    /// <summary>Fills a rectangle with individually rounded corners.</summary>
    private static void FillRoundedRect(
        DrawingContext ctx, IBrush brush, Rect r,
        double tl, double tr, double br, double bl) {
        if (tl == 0 && tr == 0 && br == 0 && bl == 0) {
            ctx.FillRectangle(brush, r);
            return;
        }
        var g = new StreamGeometry();
        using (var c = g.Open()) {
            c.BeginFigure(new Point(r.Left + tl, r.Top), true);
            c.LineTo(new Point(r.Right - tr, r.Top));
            if (tr > 0) {
                c.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Right, r.Bottom - br));
            if (br > 0) {
                c.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Left + bl, r.Bottom));
            if (bl > 0) {
                c.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Left, r.Top + tl));
            if (tl > 0) {
                c.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise);
            }
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
    }

    // Caret pixel layout has moved to ArrangeCaretAt / UpdateCaretLayers,
    // which positions CaretLayer instances inside ArrangeOverride so the
    // editor's per-row scene graph isn't rebuilt for each caret blink.

    // -------------------------------------------------------------------------
    // Column selection rendering
    // -------------------------------------------------------------------------

    private void DrawColumnSelection(DrawingContext context, LayoutResult layout, ColumnSelection colSel) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        var table = doc.Table;
        var sels = colSel.Materialize(table, _indentWidth);
        var rh = layout.RowHeight;
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        var rects = new List<Rect>();

        for (var i = 0; i < sels.Count; i++) {
            var s = sels[i];
            if (s.IsEmpty) {
                continue;
            }
            var localStart = (int)(s.Start - layout.ViewportBase);
            var localEnd = (int)(s.End - layout.ViewportBase);
            if (localEnd < 0 || localStart > totalChars) {
                continue;
            }
            localStart = Math.Clamp(localStart, 0, totalChars);
            localEnd = Math.Clamp(localEnd, 0, totalChars);
            if (localStart == localEnd) {
                continue;
            }

            foreach (var line in layout.Lines) {
                if (line.CharEnd <= localStart || line.CharStart >= localEnd) {
                    continue;
                }
                var lineY = line.Row * rh + RenderOffsetY;
                if (lineY + line.HeightInRows * rh < 0 || lineY > Bounds.Height) {
                    continue;
                }
                var rangeStart = Math.Max(0, localStart - line.CharStart);
                var rangeEnd = Math.Min(line.CharLen, localEnd - line.CharStart);
                var rangeLen = rangeEnd - rangeStart;
                if (rangeLen <= 0) {
                    continue;
                }
                foreach (var rect in line.HitTestTextRange(rangeStart, rangeLen)) {
                    rects.Add(new Rect(rect.X + TextOriginX, lineY + rect.Y, rect.Width, rect.Height));
                }
            }
        }

        if (rects.Count == 0) {
            return;
        }
        if (rects.Count == 1) {
            var r = SelCornerRadius;
            FillRoundedRect(context, SelectionBrush, rects[0], r, r, r, r);
        } else {
            FillSelectionPath(context, SelectionBrush, rects, SelCornerRadius);
        }
    }

    // Multi-cursor (column-selection) carets are arranged via the
    // _columnCaretPool list inside UpdateCaretLayers — see ArrangeOverride.

    // -------------------------------------------------------------------------
    // Caret blink
    // -------------------------------------------------------------------------

    private void OnCaretTick(object? sender, EventArgs e) {
        if (_middleDrag) {
            return;
        }
        _caretVisible = !_caretVisible;
        SetCaretLayersVisible(_caretVisible);
    }

    public void ResetCaretBlink() {
        if (_middleDrag) {
            return;
        }
        _caretVisible = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        SetCaretLayersVisible(true);
        // Caret may also have moved (typing, click, key navigation) — make
        // sure ArrangeOverride re-runs so the layer follows it.
        InvalidateArrange();
    }

    /// <summary>
    /// Pushes the visibility flag down to the primary caret layer and any
    /// active column-selection caret layers.  Cheap because each layer's
    /// setter compares before invalidating.
    /// </summary>
    private void SetCaretLayersVisible(bool visible) {
        if (_primaryCaret is not null) _primaryCaret.CaretVisible = visible;
        for (var i = 0; i < _columnCaretPool.Count; i++) {
            _columnCaretPool[i].CaretVisible = visible;
        }
    }
}
