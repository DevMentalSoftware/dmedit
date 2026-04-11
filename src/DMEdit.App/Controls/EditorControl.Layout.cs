using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DMEdit.Core.Documents;
using DMEdit.Rendering.Layout;

namespace DMEdit.App.Controls;

// Layout partial of EditorControl.  Owns InvalidateLayout, the
// row-height / char-width measurement helpers, gutter width
// computation, Measure/Arrange overrides, the two layout pipelines
// (LayoutWindowed for normal mode, LayoutCharWrap for huge files),
// EnsureLayout which dispatches between them, and the caret-layer
// arrangement helpers.  Shared fields live in the main EditorControl.cs.
public sealed partial class EditorControl {

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invalidates the cached layout, forcing a rebuild on the next render pass.
    /// Public so that <c>MainWindow</c> can trigger re-layout when a streaming buffer
    /// reports progress.
    /// </summary>
    public void InvalidateLayout() {
        _layout?.Dispose();
        _layout = null;
        _winTopLine = -1; // reset incremental scroll (content changed)
        _winExactPinActive = false; // any pending exact pin is stale
        // Don't InvalidateRowIndex() here — the row index detects stale
        // charsPerRow lazily in EnsureRowIndex() and only rebuilds when
        // the effective wrap width actually changes.  Nulling it on every
        // InvalidateLayout (which fires on every resize frame) would
        // cause dozens of throwaway rebuilds during a window resize drag.
        PerfStats.LayoutInvalidations++;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double GetRowHeight() {
        if (_rowHeight > 0) {
            return _rowHeight;
        }
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            " ", typeface, EffectiveFontSize, ForegroundBrush,
            maxWidth: double.PositiveInfinity, maxHeight: double.PositiveInfinity);
        var raw = tl.Height > 0 ? tl.Height : 20.0;
        // Snap to device pixel multiple so line.Row * rh is always
        // pixel-aligned and inter-line gaps stay uniform during scrolling.
        var scale = VisualRoot?.RenderScaling ?? 1.0;
        _rowHeight = Math.Ceiling(raw * scale) / scale;
        return _rowHeight;
    }

    private double GetCharWidth() {
        if (_charWidth > 0) {
            return _charWidth;
        }
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            "0", typeface, EffectiveFontSize, ForegroundBrush,
            maxWidth: double.PositiveInfinity, maxHeight: double.PositiveInfinity);
        _charWidth = tl.WidthIncludingTrailingWhitespace > 0 ? tl.WidthIncludingTrailingWhitespace : 8.0;
        return _charWidth;
    }

    /// <summary>
    /// Recomputes <see cref="_gutterWidth"/> and <see cref="_gutterDigitCnt"/>
    /// based on the current document's line count and font metrics.
    /// </summary>
    private void UpdateGutterWidth() {
        if (!_showLineNumbers) {
            _gutterWidth = GutterPadLeft;
            _gutterDigitCnt = 0;
            return;
        }
        long displayCount;
        if (_charWrapMode && _charWrapCharsPerRow > 0 && Document?.Table != null) {
            displayCount = (long)Math.Ceiling((double)Document.Table.Length / _charWrapCharsPerRow);
        } else {
            displayCount = Document?.Table.LineCount ?? 1;
        }
        if (displayCount < 1) displayCount = 1;
        _gutterDigitCnt = Math.Max(2, (int)Math.Floor(Math.Log10(displayCount)) + 1);
        _gutterWidth = GutterPadLeft + _gutterDigitCnt * GetCharWidth() + GutterPadRight;
    }

    /// <summary>
    /// Computes the effective text-area width passed to the layout engine.
    /// When wrapping is off returns <see cref="double.PositiveInfinity"/>.
    /// When wrapping is on, returns the lesser of the viewport width and
    /// the <see cref="WrapLinesAt"/> column limit (in pixels).
    /// </summary>
    private double GetTextWidth(double extentWidth) {
        if (!_wrapLines) return double.PositiveInfinity;
        // Reserve space for the wrap symbol so it doesn't overlap the scrollbar.
        var available = _showWrapSymbol ? extentWidth - WrapSymbolPadRight : extentWidth;
        if (WrapColumnActive) {
            var colLimit = _wrapLinesAt * GetCharWidth();
            return Math.Min(available, colLimit);
        }
        return available;
    }

    /// <summary>
    /// Returns the number of characters that fit in one visual row at the
    /// current font/wrap settings, or 0 when wrapping is off.
    /// </summary>
    private int GetCharsPerRow(double textWidth) {
        if (!double.IsFinite(textWidth) || textWidth <= 0) return 0;
        return Math.Max(1, (int)(textWidth / GetCharWidth()));
    }

    /// <summary>
    /// Computes the characters-per-row for character-wrapping mode.
    /// Rounds down to the nearest full character that fits in the text area.
    /// When <see cref="WrapLines"/> is on and <see cref="WrapLinesAt"/> is set,
    /// constrains to that column limit.
    /// </summary>
    private int ComputeCharWrapCharsPerRow(double extentWidth) {
        var cw = GetCharWidth();
        if (cw <= 0) return 80;
        var fromViewport = Math.Max(1, (int)(extentWidth / cw));
        if (_wrapLines && WrapColumnActive) {
            return Math.Min(fromViewport, _wrapLinesAt);
        }
        return fromViewport;
    }

    /// <summary>
    /// Estimates the pixel Y position of a logical line using per-line
    /// character counts from the <see cref="LineIndexTree"/>.  O(log N).
    /// When wrapping is off (<paramref name="charsPerRow"/> = 0), each line
    /// is exactly one row, so the result is <c>lineIndex * rh</c>.
    /// </summary>
    /// <summary>
    /// Estimates the Y pixel offset for a given line index when wrapping is
    /// enabled. Lines can span multiple visual rows, so we approximate by
    /// dividing total chars before the line by chars-per-row.
    /// NOT used when wrapping is off — use <c>lineIndex * rh</c> instead.
    /// </summary>
    private static double EstimateWrappedLineY(long lineIndex, PieceTable table, int charsPerRow, double rh) {
        if (lineIndex <= 0) return 0;
        var charsBefore = (long)table.LineStartOfs(lineIndex);
        if (charsBefore < 0) return lineIndex * rh; // streaming load fallback
        var visualRows = charsPerRow > 0
            ? Math.Max(lineIndex, (long)Math.Ceiling((double)charsBefore / charsPerRow))
            : lineIndex;
        return visualRows * rh;
    }

    /// <summary>
    /// Inverse of <see cref="EstimateWrappedLineY"/>: maps a scroll-pixel offset
    /// to the logical line at the viewport top when wrapping is enabled.
    /// NOT used when wrapping is off — use <c>(long)(scrollY / rh)</c> instead.
    /// </summary>
    private static long EstimateWrappedTopLine(double scrollY, PieceTable table, long lineCount, int charsPerRow, double rh) {
        if (scrollY <= 0 || lineCount <= 0) return 0;
        var targetRow = (long)(scrollY / rh);
        if (charsPerRow <= 0) return Math.Clamp(targetRow, 0, lineCount - 1);
        var targetCharOfs = Math.Min((long)targetRow * charsPerRow, table.Length);
        var charBasedLine = table.LineFromOfs(targetCharOfs);
        return Math.Clamp(Math.Min(targetRow, charBasedLine), 0, lineCount - 1);
    }

    /// <summary>
    /// Wraps <see cref="TextLayoutEngine.LayoutEmpty"/> with the editor's
    /// pixel-snapped row height.  Every empty layout built from EditorControl
    /// must use the same row height as the main layout path so scroll math,
    /// extent height, and caret visibility checks all agree — otherwise the
    /// raw engine row height drifts from <see cref="GetRowHeight"/>'s snapped
    /// value and causes off-by-a-pixel-per-row errors that accumulate at
    /// large line numbers (see journal entry 21).
    /// </summary>
    private LayoutResult BuildEmptyLayout(Typeface typeface, double maxWidth) =>
        _layoutEngine.LayoutEmpty(typeface, EffectiveFontSize, ForegroundBrush, maxWidth, GetRowHeight());

    /// <summary>
    /// Builds or retrieves the current layout.
    /// Only the visible window of text is fetched and laid out (windowed layout).
    /// </summary>
    private LayoutResult EnsureLayout() {
        if (_layout != null) {
            return _layout;
        }
        if (_layoutFailed) {
            _layout = BuildEmptyLayout(new Typeface(FontFamily), 100);
            return _layout;
        }
        _perfSw.Restart();

        var doc = BackgroundPasteInProgress ? null : Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var boundsW = Bounds.Width > 0 ? Bounds.Width : 900;
        var extentW = Math.Max(100, boundsW - _gutterWidth);
        var textW = GetTextWidth(extentW);
        _lastTextWidth = textW;
        var lineCount = doc?.Table.LineCount ?? 0;

        try {
            if (_charWrapMode && doc != null && doc.Table.Length > 0) {
                LayoutCharWrap(doc, typeface, extentW);
            } else if (doc != null && lineCount > 0) {
                LayoutWindowed(doc, lineCount, typeface, textW, extentW);
            } else {
                _layout = BuildEmptyLayout(typeface, textW);
                _extent = new Size(extentW, 0);
                RenderOffsetY = 0;
            }
        } catch (LineTooLongException ex) {
            // Line too long for normal layout — switch to character-wrapping
            // mode and retry.  Don't set _layoutFailed so the next layout
            // pass uses char-wrap successfully.
            _charWrapMode = true;
            _layout = BuildEmptyLayout(typeface, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            InvalidateMeasure();
            LineTooLongDetected?.Invoke(ex);
        } catch {
            _layoutFailed = true;
            _layout = BuildEmptyLayout(typeface, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            throw;
        }

        _perfSw.Stop();
        PerfStats.Layout.Record(_perfSw.Elapsed.TotalMilliseconds);

        PerfStats.ViewportLines = _layout?.Lines.Count ?? 0;
        PerfStats.ViewportRows = _layout?.TotalRows ?? 0;
        PerfStats.ScrollPercent = _extent.Height > _viewport.Height
            ? _scrollOffset.Y / (_extent.Height - _viewport.Height) * 100
            : 0;

        return _layout!;
    }

    protected override Size MeasureOverride(Size availableSize) {
        _layout?.Dispose();
        _layout = null;
        var oldViewportW = _viewport.Width;
        _viewport = availableSize;

        if (_layoutFailed) {
            _layout = BuildEmptyLayout(new Typeface(FontFamily), 100);
            return availableSize;
        }

        _perfSw.Restart();

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var extentW = double.IsInfinity(availableSize.Width)
            ? 0
            : Math.Max(100, availableSize.Width - _gutterWidth);
        var textW = GetTextWidth(extentW);
        _lastTextWidth = textW;

        var lineCount = doc?.Table.LineCount ?? 0;

        try {
            if (_charWrapMode && doc != null && doc.Table.Length > 0) {
                LayoutCharWrap(doc, typeface, extentW);
            } else if (doc != null && lineCount > 0) {
                LayoutWindowed(doc, lineCount, typeface, textW, extentW);
            } else {
                _layout = BuildEmptyLayout(typeface, textW);
                _extent = new Size(extentW, 0);
                RenderOffsetY = 0;
            }
        } catch (LineTooLongException ex) {
            // Line too long for normal layout — switch to character-wrapping
            // mode and retry.  Don't set _layoutFailed so the next layout
            // pass uses char-wrap successfully.
            _charWrapMode = true;
            _layout = BuildEmptyLayout(typeface, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            InvalidateMeasure();
            LineTooLongDetected?.Invoke(ex);
        } catch {
            _layoutFailed = true;
            _layout = BuildEmptyLayout(typeface, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            throw;
        }

        _perfSw.Stop();
        PerfStats.Layout.Record(_perfSw.Elapsed.TotalMilliseconds);

        // Viewport width change affects HScrollMaximum and scrollbar ViewportSize,
        // but LayoutWindowed's oldHMax comparison misses this because _viewport is
        // already updated before oldHMax is captured.
        if (Math.Abs(availableSize.Width - oldViewportW) > 0.5)
            FireHScrollChanged();

        RaiseScrollInvalidated();
        FireScrollChanged();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var size = base.ArrangeOverride(finalSize);
        UpdateCaretLayers();
        return size;
    }

    /// <summary>
    /// Recomputes the position of the primary caret layer (and any extra
    /// column-selection caret layers) and arranges them inside this control.
    /// Called from <see cref="ArrangeOverride"/> on every layout pass and
    /// also directly from sites that move the caret without otherwise
    /// triggering layout (scroll, focus changes, edit-coalesce flush, etc.).
    /// Off-viewport carets are arranged to a zero-size rect so they paint
    /// nothing without needing to remove them from VisualChildren.
    /// </summary>
    private void UpdateCaretLayers() {
        if (_primaryCaret is null) return;
        var doc = Document;
        // Ensure the layout is current — the ScrollValue setter disposes
        // _layout, and ArrangeOverride can run before MeasureOverride
        // rebuilds it.  Without this, the caret layer is arranged to a
        // zero rect (hidden) and never recovers.
        var layout = _layout ?? (doc != null ? EnsureLayout() : null);
        var emptyRect = new Rect(0, 0, 0, 0);

        // Hide everything if no doc, no layout, scroll-drag, mid-paste,
        // loading, or focus lost.  These match the gates that the old
        // Render() path used to draw the caret.
        var scrollDrag = ScrollBar?.IsDragging ?? false;
        var hideAll = doc is null
            || layout is null
            || _middleDrag
            || scrollDrag
            || IsLoading
            || !IsFocused
            || BackgroundPasteInProgress;

        if (hideAll) {
            _primaryCaret.Arrange(emptyRect);
            for (var i = 0; i < _columnCaretPool.Count; i++) {
                _columnCaretPool[i].Arrange(emptyRect);
            }
            return;
        }

        _primaryCaret.OverwriteMode = _overwriteMode;
        _primaryCaret.Brush = CaretBrush;
        _primaryCaret.CaretWidth = CaretWidth;

        if (doc!.ColumnSel is { } colSel) {
            // Column-selection multi-cursor.  The first cursor goes to
            // _primaryCaret; the rest come from the pool, growing as
            // needed.  Pool entries beyond the cursor count are arranged
            // to zero so they paint nothing.
            var carets = colSel.MaterializeCarets(doc.Table, _indentWidth);
            if (carets.Count == 0) {
                _primaryCaret.Arrange(emptyRect);
                for (var i = 0; i < _columnCaretPool.Count; i++) {
                    _columnCaretPool[i].Arrange(emptyRect);
                }
                return;
            }

            ArrangeCaretAt(_primaryCaret, layout!, carets[0]);

            for (var i = 1; i < carets.Count; i++) {
                var poolIdx = i - 1;
                if (poolIdx >= _columnCaretPool.Count) {
                    var extra = new CaretLayer {
                        Brush = CaretBrush,
                        CaretWidth = CaretWidth,
                        OverwriteMode = _overwriteMode,
                        CaretVisible = _caretVisible,
                    };
                    _columnCaretPool.Add(extra);
                    VisualChildren.Add(extra);
                }
                var layer = _columnCaretPool[poolIdx];
                layer.OverwriteMode = _overwriteMode;
                layer.Brush = CaretBrush;
                layer.CaretWidth = CaretWidth;
                layer.CaretVisible = _caretVisible;
                ArrangeCaretAt(layer, layout!, carets[i]);
            }

            // Hide unused pool slots beyond the current cursor count.
            for (var i = carets.Count - 1; i < _columnCaretPool.Count; i++) {
                _columnCaretPool[i].Arrange(emptyRect);
            }
            return;
        }

        // Single-cursor common case.
        ArrangeCaretAt(_primaryCaret, layout!, doc.Selection.Caret);
        for (var i = 0; i < _columnCaretPool.Count; i++) {
            _columnCaretPool[i].Arrange(emptyRect);
        }
    }

    /// <summary>
    /// Computes the pixel rect of the caret at <paramref name="caretOfs"/>
    /// in document coordinates and arranges <paramref name="layer"/> there.
    /// Off-viewport positions get a zero-size rect.
    /// </summary>
    private void ArrangeCaretAt(CaretLayer layer, LayoutResult layout, long caretOfs) {
        var localCaret = (int)(caretOfs - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (localCaret < 0 || localCaret > totalChars) {
            layer.Arrange(new Rect(0, 0, 0, 0));
            return;
        }
        var rect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);

        // CharWrapMode: the layout uses Avalonia TextLayout (not
        // MonoLineLayout), which doesn't handle isAtEnd.  When isAtEnd
        // is set at a row boundary, manually move the caret rect to the
        // end of the previous row.
        if (_charWrapMode && _caretIsAtEnd && _charWrapCharsPerRow > 0) {
            var docOfs = layout.ViewportBase + localCaret;
            if (docOfs > 0 && docOfs % _charWrapCharsPerRow == 0) {
                var rh = GetRowHeight();
                var cw = GetCharWidth();
                var prevRowEndX = _charWrapCharsPerRow * cw;
                rect = new Rect(prevRowEndX, rect.Y - rh, rect.Width, rect.Height);
            }
        }

        var y = rect.Y + RenderOffsetY;
        if (y + rect.Height < 0 || y > Bounds.Height) {
            layer.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        double caretX;
        double widthForLayer;
        if (_overwriteMode) {
            // Block-style: layer width matches the underlying glyph cell.
            // Step by one code point so a surrogate pair draws as one block.
            var blockW = rect.Height * 0.55; // fallback ~em-width
            if (localCaret < totalChars) {
                var docOfs = layout.ViewportBase + localCaret;
                var table = Document?.Table;
                var stepW = table != null
                    ? (int)(CodepointBoundary.StepRight(table, docOfs) - docOfs)
                    : 1;
                if (stepW < 1) stepW = 1;
                var nextRect = _layoutEngine.GetCaretBounds(localCaret + stepW, layout);
                var w = nextRect.X - rect.X;
                if (w > 0) blockW = w;
            }
            caretX = rect.X + TextOriginX;
            widthForLayer = blockW;
        } else {
            // Insertion-point: layer is exactly CaretWidth pixels wide,
            // device-pixel snapped so the line stays crisp.
            var scale = VisualRoot?.RenderScaling ?? 1.0;
            caretX = Math.Round((rect.X + TextOriginX) * scale) / scale;
            widthForLayer = CaretWidth;
        }

        // Hide if the caret is horizontally inside the gutter (e.g. heavy
        // horizontal scroll left would push it under the gutter).
        if (caretX + widthForLayer <= _gutterWidth) {
            layer.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        layer.Arrange(new Rect(caretX, y, widthForLayer, rect.Height));
    }

    /// <summary>
    /// Fetches only the text visible in the current scroll viewport and lays it out.
    /// Sets <see cref="_layout"/>, <see cref="_extent"/>, and <see cref="RenderOffsetY"/>.
    /// </summary>
    /// <remarks>
    /// The scroll math estimates total visual rows from character count and character width,
    /// so that the extent accounts for word-wrap and the scroll-to-line mapping works in
    /// visual-row units. For monospace fonts this is exact; for proportional fonts it's a
    /// close approximation.
    /// </remarks>
    private void LayoutWindowed(Document doc, long lineCount, Typeface typeface, double maxWidth, double extentWidth) {
        var rh = GetRowHeight();

        // Compute total visual rows and map scroll offset → top line.
        var charsPerRow = _wrapLines ? GetCharsPerRow(maxWidth) : 0;
        long totalVisualRows;
        long topLine;
        if (!_wrapLines) {
            // Wrapping off: each line tree entry = one visual row (exact).
            totalVisualRows = lineCount;
            topLine = Math.Clamp((long)(_scrollOffset.Y / rh), 0, Math.Max(0, lineCount - 1));
        } else {
            // Wrapping on: lines can span multiple rows.  Use the exact
            // per-line row index when available; fall back to the char-
            // density estimate for docs above the build threshold.
            totalVisualRows = ExactOrEstimateTotalRows(doc.Table, lineCount, charsPerRow);
            topLine = ExactOrEstimateTopLine(_scrollOffset.Y, doc.Table,
                lineCount, charsPerRow, rh);
        }

        // Note: no "sanity-check" drift clear here.  The incremental cache
        // is the source of truth — the small-scroll branch below constrains
        // topLine to ±1 from the cache so we track the rendered position
        // line-by-line, regardless of what the estimate formula says.  For
        // wrap-on docs with mixed wrap counts, the formula's topLine can
        // legitimately jump by several lines per frame as the char-density
        // estimate crosses line boundaries.  Clearing the cache on that
        // drift was a bug — it dropped us into the large-jump render path
        // and produced visible content jumps during smooth drag (2026-04-09
        // session, "thumb drags broken for wrap-on mixed-wrap doc").
        //
        // Drift CAN accumulate a row or two over hundreds of sub-pixel
        // small-scroll ticks, but the self-correction at every large jump
        // (wheel notch > 2*rh, thumb track click, page-up/down) re-primes
        // the cache from the formula.  Arbitrary drift was a mirage — the
        // apparent "drift" was the formula being wrong, not the cache.

        // For single-row scrolls (arrow buttons), constrain topLine to change
        // by at most ±1 from the previous frame.  This lets the incremental
        // render-offset logic use the actual cached line height instead of
        // the estimate, giving pixel-perfect smooth scrolling.
        // Everything else (wheel, drag, page-down) uses the formula-based
        // topLine directly — the threshold of 2*rh cleanly separates arrow
        // clicks (ds = rh) from wheel notches (ds = 3*rh).
        var isSmallScroll = _winTopLine >= 0
            && Math.Abs(_scrollOffset.Y - _winScrollOffset) < 2 * rh;

        if (isSmallScroll) {
            var ds = _scrollOffset.Y - _winScrollOffset;

            if (topLine > _winTopLine) {
                // Would advance.  Only allow +1, and only when the first
                // line is fully scrolled above the viewport.
                var firstLineBottom = _winRenderOffsetY - ds + _winFirstLineHeight;
                if (firstLineBottom > 0) {
                    topLine = _winTopLine;      // not fully off-screen yet
                } else {
                    topLine = _winTopLine + 1;  // advance by exactly 1
                }
            } else if (topLine < _winTopLine) {
                topLine = _winTopLine - 1;      // retreat by exactly 1
            }

            // If topLine stayed the same but scrolling up would leave a gap
            // (render offset goes positive), retreat to include the previous line.
            if (topLine == _winTopLine) {
                var wouldBeOffset = _winRenderOffsetY - ds;
                if (wouldBeOffset > 0 && topLine > 0) {
                    topLine--;
                }
            }
        }

        // Fetch enough lines to fill the viewport (+ buffer for partial rows)
        var visibleRows = (int)(_viewport.Height / rh) + 4;
        var bottomLine = Math.Min(lineCount, topLine + visibleRows);

        // If showing the end of the document but topLine is too high to
        // fill the viewport (e.g. after deleting a line while scrolled to
        // the bottom), pull topLine back so the layout has enough content.
        //
        // Gate: when ScrollExact has armed `_winExactPinActive`, it has
        // already computed the target topLine using exact per-line row
        // counts (via the tail-walk in ScrollSelectionIntoView).  Pull-
        // back's visibleRows count assumes 1 row per line, so it can
        // drag topLine further back than ScrollExact wants and corrupt
        // the cache override (bugs #2/#3 from the 2026-04-09 Find
        // scrolling session).  The flag is single-use — it's cleared
        // further down this method so subsequent wheel/drag/arrow
        // scrolls go through the normal pull-back path and never see
        // the gate fire.
        if (!_winExactPinActive && bottomLine >= lineCount && lineCount > visibleRows) {
            topLine = Math.Min(topLine, lineCount - visibleRows);
        }

        var startOfs = topLine > 0 ? doc.Table.LineStartOfs(topLine) : 0L;
        long endOfs;
        if (bottomLine >= lineCount) {
            endOfs = doc.Table.Length;
        } else {
            endOfs = doc.Table.LineStartOfs(bottomLine);
        }

        // During streaming/paged loads, LineStartOfs returns -1 when the
        // required page isn't in memory yet. Layout empty text — the next
        // ProgressChanged event will trigger re-layout once data is available.
        if (startOfs < 0 || endOfs < 0) {
            _layout = BuildEmptyLayout(typeface, maxWidth);
            _extent = new Size(extentWidth, totalVisualRows * rh);
            RenderOffsetY = 0;
            return;
        }

        var len = (int)(endOfs - startOfs);

        // Sanity check: if the computed range falls outside the actual
        // table content, the line tree and piece table are in an inconsistent
        // state (e.g. intermediate state during undo or document reload).
        // Skip this layout pass — the next one will see the consistent state.
        var docLen = doc.Table.Length;
        if (startOfs + len > docLen) {
            _layout = BuildEmptyLayout(typeface, maxWidth);
            _extent = new Size(extentWidth, totalVisualRows * rh);
            RenderOffsetY = 0;
            return;
        }

        // Layout one line at a time directly from the PieceTable so we
        // never materialize multiple lines into a single string.  Hanging
        // indent engages only on the monospace fast path — proportional
        // typefaces ignore the column count.
        var hangingIndentChars = _hangingIndent && _wrapLines && !_charWrapMode
            ? Math.Max(0, _indentWidth / 2)
            : 0;
        _layout = _layoutEngine.LayoutLines(
            doc.Table, topLine, bottomLine, typeface, EffectiveFontSize, ForegroundBrush,
            maxWidth, startOfs, lineCount, doc.Table.Length, hangingIndentChars,
            _useFastTextLayout, rh, _indentWidth);
        _layout.TopLine = topLine;

#if DEBUG
        // Invariant: the row count each rendered line actually has must
        // match what ComputeLineRowCount returns.  If these diverge,
        // ScrollExact targets the wrong row and Find matches land off-
        // viewport.  Catching it here — on every layout pass in Debug —
        // makes the entire mono/slow-path alignment test matrix redundant.
        if (_wrapLines && !_charWrapMode) {
            for (var i = 0; i < _layout.Lines.Count; i++) {
                var ll = _layout.Lines[i];
                var lineIdx = topLine + i;
                var rendered = ll.HeightInRows;
                var computed = ComputeLineRowCount(lineIdx);
                Debug.Assert(rendered == computed,
                    $"Row count mismatch line {lineIdx}: rendered {rendered}, "
                    + $"computed {computed} (mono={ll.IsMono})");
            }
        }
#endif

        // When the layout covers the entire document, use exact height
        // instead of the estimate — gives pixel-perfect scrolling on
        // small files.
        //
        // Exact-pin end-of-doc: when ScrollExact has armed the exact-pin
        // flag for a target that lands at the tail of the doc, tighten
        // the extent so that ScrollMaximum exactly equals the current
        // scroll value.  This makes the scrollbar thumb sit at the
        // bottom, lets the render path's max-scroll clamp kick in
        // cleanly, and prevents the next ScrollSelectionIntoView call
        // from finding a stale extent.  The flag is single-use (cleared
        // below) so ordinary wheel/drag/arrow scrolling never takes
        // this path — ordinary scroll needs the estimate-based extent
        // to stay consistent frame-to-frame.
        double extentHeight;
        if (topLine == 0 && bottomLine >= lineCount) {
            // Entire doc in one layout — use exact height.
            extentHeight = _layout.TotalHeight;
        } else {
            // Use the total visual rows count (exact or estimated)
            // regardless of whether ScrollExact is active.  The old
            // exact-pin branch inflated the extent to
            // max(layoutHeight, scrollY + vpH), but scrollY is an
            // estimate that can wildly over-/under-shoot for wrapped
            // docs with variable line lengths — inflating the scrollbar
            // extent after every FindNext (user-reported 2026-04-11).
            // The clamping at line 684 handles scrollY > ScrollMaximum.
            extentHeight = totalVisualRows * rh;
        }
        var contentWidth = !_wrapLines
            ? _gutterWidth + doc.Table.MaxLineLength * GetCharWidth() + TextAreaPadRight
            : extentWidth;
        var oldHMax = HScrollMaximum;
        _extent = new Size(Math.Max(extentWidth, contentWidth), extentHeight);
        var newHMax = HScrollMaximum;
        // Clamp scroll offset so that shrinking content or a wider viewport
        // doesn't leave the editor showing empty space to the right of text.
        if (_scrollOffset.X > newHMax)
            _scrollOffset = new Vector(newHMax, _scrollOffset.Y);
        if (_scrollOffset.Y > ScrollMaximum)
            _scrollOffset = new Vector(_scrollOffset.X, ScrollMaximum);
        if (Math.Abs(newHMax - oldHMax) > 0.5) FireHScrollChanged();

        // Compute render offset.  For small topLine changes (arrow keys, wheel)
        // use an incremental offset based on the actual cached line height so
        // each rh of scroll produces exactly rh of visual movement.
        // For large jumps (thumb drag, page-down) use the formula estimate.
        var deltaTop = topLine - _winTopLine;

        if (_winTopLine >= 0 && deltaTop == 0) {
            // topLine unchanged — pure scroll, trivially smooth.
            RenderOffsetY = _winRenderOffsetY - (_scrollOffset.Y - _winScrollOffset);
        } else if (_winTopLine >= 0 && deltaTop == 1 && _winFirstLineHeight > 0) {
            // topLine advanced by 1.  Compensate for the departed line's
            // actual height (not the estimate) to avoid a visual jump.
            RenderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                + _winFirstLineHeight;
        } else if (_winTopLine >= 0 && deltaTop == -1 && _layout.Lines.Count > 0) {
            // topLine retreated by 1.  The new first line was prepended;
            // subtract its actual height to keep content in place.
            RenderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                - _layout.Lines[0].HeightInRows * rh;
        } else {
            // First layout or large jump.  Use the exact row index when
            // available so dragging the thumb lands at the precise content
            // position — no more "topLine+RenderOffsetY estimate disagrees
            // with actual first-line position" jumps during drag.
            RenderOffsetY = _wrapLines
                ? ExactOrEstimateLineY(topLine, doc.Table, charsPerRow, rh) - _scrollOffset.Y
                : topLine * rh - _scrollOffset.Y;
        }

        // Safety: prevent any remaining gap at the viewport top.
        if (RenderOffsetY > 0) {
            RenderOffsetY = 0;
        }

        // Safety: prevent gap at the viewport bottom.  If the layout has
        // enough content to fill the viewport but the render offset pushes
        // it too far above, snap it down so the content reaches the bottom.
        if (_layout.TotalHeight >= _viewport.Height) {
            var contentBottom = RenderOffsetY + _layout.TotalHeight;
            if (contentBottom < _viewport.Height) {
                RenderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // When at max scroll and the layout includes the end of the document,
        // anchor content bottom to viewport bottom so the last line is flush.
        // Only at max scroll — otherwise scrolling up from the bottom would be stuck.
        // Gate: when ScrollExact has armed the exact-pin flag, it has already
        // computed the precise renderOffsetY (including near-end remap).
        // The anchor would override that targeting and push the match
        // off-viewport — the root cause of FindPrev landing off-screen
        // for matches near the document tail.
        var scrollMax = _extent.Height - _viewport.Height;
        if (!_winExactPinActive
                && bottomLine >= lineCount && _scrollOffset.Y >= scrollMax - 1.0
                && _layout.TotalHeight >= _viewport.Height) {
            var contentBottom = RenderOffsetY + _layout.TotalHeight;
            if (contentBottom != _viewport.Height) {
                RenderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // Sync scrollbar: after all render-offset adjustments, update
        // _scrollOffset.Y to the actual rendered position so the scrollbar
        // thumb accurately tracks the viewport.  The formula:
        //   trueScroll = ExactLineY(topLine) - RenderOffsetY
        // is algebraically equivalent to the incremental ds computation
        // for deltaTop 0 and ±1, so the scrollbar moves by exactly the
        // user's scroll delta on each frame — no drift.
        if (_wrapLines) {
            var trueScroll = ExactOrEstimateLineY(topLine, doc.Table, charsPerRow, rh)
                - RenderOffsetY;
            var maxScroll = Math.Max(0, extentHeight - _viewport.Height);
            trueScroll = Math.Clamp(trueScroll, 0, maxScroll);
            if (double.IsFinite(trueScroll)) {
                _scrollOffset = new Vector(_scrollOffset.X, trueScroll);
            }
        }

        // Cache state for next incremental frame.
        _winTopLine = topLine;
        _winScrollOffset = _scrollOffset.Y;
        _winRenderOffsetY = RenderOffsetY;
        _winFirstLineHeight = _layout.Lines.Count > 0 ? _layout.Lines[0].HeightInRows * rh : rh;
        // Consume the exact-pin flag — it applies only to the single
        // layout pass that renders ScrollExact's target.  Subsequent
        // wheel/drag/arrow scrolls start with the flag already clear.
        _winExactPinActive = false;
    }

    /// <summary>
    /// Character-wrapping layout: row N starts at char <c>N * charsPerRow</c>.
    /// All scroll math is O(1) — no tree lookups, no estimation.
    /// </summary>
    private void LayoutCharWrap(Document doc, Typeface typeface, double extentWidth) {
        var cw = GetCharWidth();
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var prevCpr = _charWrapCharsPerRow;
        _charWrapCharsPerRow = ComputeCharWrapCharsPerRow(extentWidth);
        var cpr = _charWrapCharsPerRow;
        if (cpr <= 0) cpr = 1;

        // Use buf-space length for row arithmetic (character count).
        var bufLen = doc.Table.Length;
        var totalRows = bufLen > 0 ? (long)Math.Ceiling((double)bufLen / cpr) : 1;
        // When cpr changes (resize, wrap toggle), adjust scroll so the caret
        // stays at the same screen position.
        var oldMaxScroll = ScrollMaximum;
        var wasAtBottom = oldMaxScroll > 0 && _scrollOffset.Y >= oldMaxScroll - 1;
        var maxScroll = Math.Max(0, totalRows * rh - _viewport.Height);
        if (wasAtBottom) {
            _scrollOffset = new Vector(_scrollOffset.X, maxScroll);
        } else {
            // Keep the caret at the same screen-relative Y position.
            var caretOfs = doc.Selection.Caret;
            if (prevCpr > 0 && prevCpr != cpr) {
                var caretRow = caretOfs / cpr;
                var caretY = caretRow * rh;
                // Where was the caret on screen before?
                var oldCaretRow = caretOfs / prevCpr;
                var oldCaretY = oldCaretRow * rh;
                var oldScreenY = oldCaretY - _scrollOffset.Y;
                // Adjust scroll so caret stays at the same screen Y.
                var newScrollY = Math.Clamp(caretY - oldScreenY, 0, maxScroll);
                _scrollOffset = new Vector(_scrollOffset.X, newScrollY);
            } else if (_scrollOffset.Y > maxScroll) {
                _scrollOffset = new Vector(_scrollOffset.X, maxScroll);
            }
        }
        var topRow = Math.Clamp((long)(_scrollOffset.Y / rh), 0, Math.Max(0, totalRows - 1));
        var visibleRows = (int)Math.Ceiling(_viewport.Height / rh) + 2;

        // ViewportBase = offset of first visible character.
        var startOfs = topRow * cpr;

        var lines = new List<LayoutLine>();
        var foreground = ForegroundBrush;
        for (var i = 0; i < visibleRows; i++) {
            var rowIdx = topRow + i;
            if (rowIdx >= totalRows) break;
            var rowStart = rowIdx * cpr;
            var len = (int)Math.Min(cpr, bufLen - rowStart);
            if (len <= 0) break;
            var rawText = doc.Table.GetText(rowStart, len);
            var text = SanitizeCharWrapText(rawText);
            var rowCharStart = (int)(rowStart - startOfs);
            var layout = MakeCharWrapLayout(text, typeface, EffectiveFontSize, foreground);
            lines.Add(new LayoutLine(rowCharStart, len, i, 1, layout));
        }

        if (lines.Count == 0) {
            var layout = MakeCharWrapLayout("", typeface, EffectiveFontSize, foreground);
            lines.Add(new LayoutLine(0, 0, 0, 1, layout));
        }

        _layout = new LayoutResult(lines, rh, startOfs);
        _layout.TopLine = topRow;
        _extent = new Size(Math.Max(extentWidth, cpr * cw), totalRows * rh);
        RenderOffsetY = topRow * rh - _scrollOffset.Y;

        // Clamp scroll and render offset.
        if (_scrollOffset.Y > ScrollMaximum) {
            _scrollOffset = new Vector(_scrollOffset.X, ScrollMaximum);
        }
        if (RenderOffsetY > 0) RenderOffsetY = 0;

        _winTopLine = topRow;
        _winScrollOffset = _scrollOffset.Y;
        _winRenderOffsetY = RenderOffsetY;
        _winFirstLineHeight = rh;
    }

    /// <summary>
    /// Replaces tab, CR, and LF characters with single-width display glyphs
    /// so every character occupies exactly one monospace cell.  When
    /// ShowWhitespace is off, control chars become spaces; when on, they
    /// use visible glyphs.
    /// </summary>
    private string SanitizeCharWrapText(string raw) {
        // Fast path: no control chars.
        if (raw.AsSpan().IndexOfAny('\t', '\r', '\n') < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw) {
            sb.Append(ch switch {
                '\t' or '\r' or '\n' => ' ',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Creates a NoWrap TextLayout for a single char-wrap row.</summary>
    private static TextLayout MakeCharWrapLayout(
        string text, Typeface typeface, double fontSize, IBrush foreground) =>
        new(text, typeface, fontSize, foreground,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity);
}
