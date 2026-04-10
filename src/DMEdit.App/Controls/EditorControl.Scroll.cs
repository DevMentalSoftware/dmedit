using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DMEdit.Rendering.Layout;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

/// <summary>
/// Controls how <see cref="EditorControl.ScrollCaretIntoView"/> repositions
/// the viewport when the caret is (or may be) off-screen.  The default,
/// <see cref="Minimal"/>, is correct for almost every caller — edits,
/// navigation, undo/redo — because it only scrolls when the caret isn't
/// already visible, and it scrolls by the smallest amount that makes it so.
/// The non-default policies are for callers that know the caret has just
/// been placed at an explicit target (Ctrl+Home, Ctrl+End, GoTo Line) and
/// want the viewport to show that target at a specific position.
/// </summary>
public enum ScrollPolicy {
    /// <summary>
    /// Scroll only if the caret's current row is not fully visible, and
    /// scroll by the smallest amount that brings it in (top edge if the
    /// caret is above the viewport, bottom edge if below).  When the
    /// caret is already visible, this is a no-op.
    /// </summary>
    Minimal,

    /// <summary>
    /// Place the caret's row at the top of the viewport.  Used for
    /// Ctrl+Home and similar "jump to start" commands where the user
    /// expects the destination to be the first thing they see.
    /// </summary>
    Top,

    /// <summary>
    /// Place the caret's row as close to the vertical center of the
    /// viewport as the document extent allows.  Used for GoTo Line and
    /// similar "jump to target" commands where the user wants context
    /// around the destination, not just the destination flush to an edge.
    /// </summary>
    Center,

    /// <summary>
    /// Place the caret's row at the bottom of the viewport.  Used for
    /// Ctrl+End and similar "jump to end" commands.
    /// </summary>
    Bottom,
}

/// <summary>
/// Travel direction for <see cref="EditorControl.ScrollSelectionIntoView"/>.
/// Only matters when the selection is taller than the viewport: the direction
/// decides which end of the selection is pinned to the visible edge.
/// </summary>
public enum SearchDirection {
    /// <summary>
    /// Caller was moving forward through the document (FindNext, Replace
    /// next, incremental forward).  For a too-tall selection, show the
    /// match's top at the viewport top — the user is reading forward and
    /// the start of the match is the part they need to see first.
    /// </summary>
    Forward,

    /// <summary>
    /// Caller was moving backward (FindPrevious, incremental backward).
    /// For a too-tall selection, show the match's bottom at the viewport
    /// bottom — the user is reading backward and the end of the match is
    /// the part they need to see first.
    /// </summary>
    Backward,
}

// Scroll + caret-visibility + document-swap partial of EditorControl.
// Owns ScrollCaretIntoView (the load-bearing visible-caret invariant),
// ScrollPage, ScrollToTopLine, MoveCaretByPage, the FindLineIndexForOfs /
// GetCaretScreenRow / HitTestAtScreenRow / FindLastVisibleLogicalLine
// hit-test helpers, OnDocumentChanged, and SaveScrollState /
// ReplaceDocument / RestoreScrollState for tab switches.  Shared fields
// (_layout, _rowHeight, _scrollOffset, _viewport, _renderOffsetY,
// _winTopLine, _winScrollOffset, _winRenderOffsetY, _winFirstLineHeight,
// _preferredCaretX) live in the main EditorControl.cs.
public sealed partial class EditorControl {

    private static int FindLineIndexForOfs(long charOfs, LayoutResult layout) {
        var localOfs = (int)(charOfs - layout.ViewportBase);
        var lines = layout.Lines;
        // If the caret is outside the visible window, return -1.
        if (lines.Count == 0 || localOfs < 0) {
            return -1;
        }
        for (var i = lines.Count - 1; i >= 0; i--) {
            if (lines[i].CharStart <= localOfs) {
                return i;
            }
        }
        return 0;
    }

    /// <summary>
    /// Returns the caret's visual screen row (0-based from viewport top).
    /// Used by <see cref="MoveCaretVertical"/> and <see cref="MoveCaretByPage"/>
    /// to preserve the caret's screen position across scroll operations.
    /// </summary>
    private int GetCaretScreenRow(Rect caretRect, double rh) {
        return Math.Max(0, (int)Math.Round((caretRect.Y + RenderOffsetY) / rh));
    }

    /// <summary>
    /// Hit-tests the layout at a given screen row, returning the absolute
    /// document offset closest to (<see cref="_preferredCaretX"/>, screenRow).
    /// </summary>
    private long HitTestAtScreenRow(int screenRow, double rh, LayoutResult layout) {
        var targetY = screenRow * rh + rh / 2 - RenderOffsetY;
        targetY = Math.Clamp(targetY, 0, Math.Max(0, layout.TotalHeight - 1));
        var hitX = _preferredCaretX >= 0 ? _preferredCaretX : 0;
        var localNew = _layoutEngine.HitTest(new Point(hitX, targetY), layout);
        return layout.ViewportBase + localNew;
    }

    /// <summary>
    /// Moves the caret to the given offset and scrolls it into view with
    /// <see cref="ScrollPolicy.Center"/> — GoTo Line and similar cross-
    /// document jumps should show the target with surrounding context,
    /// not flush to a viewport edge.
    /// </summary>
    public void GoToPosition(long offset) {
        var doc = Document;
        if (doc == null) return;
        offset = Math.Clamp(offset, 0, doc.Table.Length);
        doc.Selection = Core.Documents.Selection.Collapsed(offset);
        ScrollCaretIntoView(ScrollPolicy.Center);
        InvalidateVisual();
        ResetCaretBlink();
        Focus();
    }

    public void ScrollCaretIntoView(ScrollPolicy policy = ScrollPolicy.Minimal) {
        var doc = Document;
        if (doc == null) return;
        if (IsLoading) {
            return; // Can't actually happen
        }

        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        PerfStats.ScrollCaretCalls++;

        var rh = GetRowHeight();
        var vpH = Bounds.Height > 0 ? Bounds.Height : _viewport.Height;

        if (_charWrapMode) {
            // ── Character-wrapping mode ──────────────────────────────
            // Use the cpr from the last layout (set by LayoutCharWrap).
            var cpr = _charWrapCharsPerRow > 0 ? _charWrapCharsPerRow : 80;
            var caretRow = caret / cpr;
            var caretY = caretRow * rh;

            if (_scrollOffset.Y > ScrollMaximum) ScrollValue = ScrollMaximum;
            ScrollValue = ComputeTargetScrollY(policy, caretY, rh, vpH, _scrollOffset.Y);
            InvalidateVisual();
            return;
        }

        var caretLine = table.LineFromOfs(caret);

        if (!_wrapLines) {
            // ── Wrapping off ──────────────────────────────────────────
            // The line tree is exact: each entry = one visual row.
            // Update extents so ScrollMaximum/HScrollMaximum aren't stale.
            var cw = GetCharWidth();
            var maxLine = table.MaxLineLength;
            var hExtent = maxLine > 0 ? maxLine * cw + _gutterWidth + TextAreaPadRight : _extent.Width;
            var oldHMax = HScrollMaximum;
            _extent = new Size(hExtent, lineCount * rh);
            if (Math.Abs(HScrollMaximum - oldHMax) > 0.5) FireHScrollChanged();

            var caretY = caretLine * rh;
            if (_scrollOffset.Y > ScrollMaximum) {
                ScrollValue = ScrollMaximum;
            }
            ScrollValue = ComputeTargetScrollY(policy, caretY, rh, vpH, _scrollOffset.Y);
        } else {
            // ── Wrapping on ───────────────────────────────────────────
            // Lines can span multiple visual rows, so we cannot derive
            // the caret's row-Y from its logical line index.
            // EstimateWrappedLineY returns the Y of the line's *start* —
            // row 0 of the wrapped paragraph — regardless of which row
            // inside the paragraph the caret is actually on.  For a
            // caret on row 15 of a 20-row wrapped line, that estimate
            // points 15 rows above where the caret really is.
            //
            // Approach: measure the caret against the current layout
            // FIRST, and compute the target scroll from the actual caret
            // doc-Y.  For Minimal, if the caret is already visible in
            // the current layout, return without touching the scroll
            // (hot path for every keystroke).  For Top/Center/Bottom,
            // unconditionally write the target scroll.
            //
            // Cold-start path (caret outside the current layout window,
            // e.g. after GoToPosition jumped across the document): seed
            // with the line-start estimate, rebuild, re-measure.  The
            // loop converges in at most 3 passes.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);

            // Update extent from estimated total visual rows so that
            // ScrollMaximum isn't stale (e.g. after a large paste).
            var totalChars = table.Length;
            var totalVisualRows = charsPerRow > 0
                ? Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow))
                : lineCount;
            _extent = new Size(_extent.Width, totalVisualRows * rh);
            if (_scrollOffset.Y > ScrollMaximum) {
                ScrollValue = ScrollMaximum;
            }

            for (var pass = 0; pass < 3; pass++) {
                var layout = EnsureLayout();
                var localCaret = (int)(caret - layout.ViewportBase);
                var layoutCharEnd = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;

                if (localCaret < 0 || localCaret > layoutCharEnd) {
                    // Caret is not in the current layout window.  Seed
                    // the scroll with the line-start position (exact
                    // when the row index is populated, estimate for
                    // huge docs) and retry.
                    var estimatedY = ExactOrEstimateLineY(caretLine, table, charsPerRow, rh);
                    ScrollValue = estimatedY;
                    InvalidateLayout();
                    if (pass > 0) PerfStats.ScrollRetries++;
                    continue;
                }

                var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);
                var caretScreenY = caretRect.Y + RenderOffsetY;
                var caretH = Math.Ceiling(Math.Max(caretRect.Height, rh));
                // Convert screen-Y back to doc-Y for the policy computation.
                var caretDocY = caretScreenY + _scrollOffset.Y;

                // Minimal + already visible: hot-path no-op.  For Top/
                // Center/Bottom we always recompute (the target depends
                // on the policy, not on current visibility).
                if (policy == ScrollPolicy.Minimal
                        && caretScreenY >= 0
                        && caretScreenY + caretH <= vpH + 0.5) {
                    break;
                }

                var target = ComputeTargetScrollY(policy, caretDocY, caretH, vpH, _scrollOffset.Y);
                if (Math.Abs(target - _scrollOffset.Y) < 0.5) break;
                ScrollValue = target;
                InvalidateLayout();
                if (pass > 0) PerfStats.ScrollRetries++;
            }
        }

        // Horizontal scroll (wrapping off only).
        // Horizontal extent was already updated above.
        if (!_wrapLines) {
            var lineStart = table.LineStartOfs(caretLine);
            var caretCol = (int)(caret - lineStart);
            var cw = GetCharWidth();
            var caretX = caretCol * cw;
            var textAreaW = _viewport.Width - _gutterWidth;

            if (caretX < _scrollOffset.X) {
                // Show at least one character before the caret so the
                // character the caret sits after is visible (mirrors the
                // TextAreaPadRight on the right side).
                HScrollValue = Math.Max(0, caretX - cw);
            } else if (caretX + cw + TextAreaPadRight > _scrollOffset.X + textAreaW) {
                HScrollValue = caretX + cw + TextAreaPadRight - textAreaW;
            }
        }
    }

    /// <summary>
    /// Given the caret's document-Y, its height (one row in the unwrapped
    /// or char-wrap paths, possibly taller on the wrap-on mono fast path),
    /// the viewport height, and the current scroll, returns the target
    /// scroll value that satisfies the requested policy.
    ///
    /// <para><b>Minimal</b>: only moves when the caret row isn't fully in
    /// view, and moves by the smallest amount that makes it so — exactly
    /// the existing "if above, pull up; if below, pull down" logic.</para>
    ///
    /// <para><b>Top</b> / <b>Center</b> / <b>Bottom</b>: unconditionally
    /// aligns the caret row with the viewport top / center / bottom.
    /// The <see cref="ScrollValue"/> setter clamps to
    /// <c>[0, ScrollMaximum]</c>, so calling these near the document
    /// boundaries can't push the viewport past the content.</para>
    /// </summary>
    private static double ComputeTargetScrollY(
            ScrollPolicy policy, double caretDocY, double caretH,
            double viewportH, double currentScrollY) {
        switch (policy) {
            case ScrollPolicy.Top:
                return caretDocY;
            case ScrollPolicy.Bottom:
                return caretDocY + caretH - viewportH;
            case ScrollPolicy.Center:
                return caretDocY + caretH / 2 - viewportH / 2;
            case ScrollPolicy.Minimal:
            default:
                if (caretDocY < currentScrollY) {
                    return caretDocY;
                }
                if (caretDocY + caretH > currentScrollY + viewportH) {
                    return caretDocY + caretH - viewportH;
                }
                return currentScrollY;
        }
    }

    /// <summary>
    /// Scrolls so the current selection is fully visible.  Used by Find /
    /// Replace / incremental search to reveal a match — <em>not</em> by
    /// editing operations, which scroll the caret only and should use
    /// <see cref="ScrollCaretIntoView"/> instead.
    ///
    /// <para><b>Intuition:</b> FindNext is a "scroll down" operation (the
    /// user is moving forward through the document) and FindPrevious is
    /// "scroll up".  The match lands at the viewport edge that matches
    /// the scroll direction: Forward puts the match at the <em>bottom</em>
    /// edge, Backward puts it at the <em>top</em> edge.  "Already fully
    /// visible" overrides everything — no movement when the match is
    /// already on screen.</para>
    ///
    /// <para><b>Exact scroll targeting:</b> in wrap-on mode, logical
    /// lines can occupy multiple visual rows, and the scroll-value-to-
    /// topLine mapping uses a char-density estimate that can allocate
    /// fewer scroll-space rows to a line than the line actually has.
    /// The old approach (compute a pixel delta, write it to ScrollValue,
    /// let the estimate re-interpret) could miss the target row when the
    /// estimate was off.  The fix: compute the exact <c>(topLine,
    /// renderOffsetY)</c> using per-line row counts, and push that state
    /// directly into LayoutWindowed's cache fields (<c>_winTopLine</c>,
    /// <c>_winRenderOffsetY</c>) via <see cref="ScrollExact"/>.  The
    /// scroll value becomes a best-effort cosmetic hint for the
    /// scrollbar; rendering precision comes from the cache override.</para>
    ///
    /// <para><b>Last word of wrapped row:</b> the bottom row of the match
    /// is the row containing <c>sel.End - 1</c> (the last character of
    /// the match), not <c>sel.End</c>.  A caret at the past-the-end
    /// offset sits at the <em>start of the next row</em> when the match
    /// ends at a soft row break, so using <c>sel.End</c> would pin to
    /// the wrong row.</para>
    /// </summary>
    public void ScrollSelectionIntoView(SearchDirection direction) {
        var doc = Document;
        if (doc == null || IsLoading) return;

        var sel = doc.Selection;
        if (sel.IsEmpty) {
            // No selection → fall back to the caret-based primitive.
            ScrollCaretIntoView();
            return;
        }

        var table = doc.Table;
        if (table.LineCount <= 0) return;

        PerfStats.ScrollCaretCalls++;

        var rh = GetRowHeight();
        var vpH = Bounds.Height > 0 ? Bounds.Height : _viewport.Height;
        if (vpH <= 0) return;

        // Short-circuit: if the entire document fits in the viewport,
        // there's nowhere to scroll.  Without this check, any non-trivial
        // scroll target would clamp to 0 and visibly jump to the top.
        if (ScrollMaximum <= 0.5) return;

        // --- Fast path: is the match fully visible in the current layout? ---
        //
        // This is the "Shift+F3 on the already-selected match" case and
        // the "FindNext to a match already on screen" case.  Both should
        // be no-ops.  We answer the visibility question using the real
        // layout, not an estimate, so short/long lines in the viewport
        // are all measured exactly.
        var layout = EnsureLayout();
        if (layout.Lines.Count > 0) {
            var viewBase = layout.ViewportBase;
            var viewEnd = viewBase + layout.Lines[^1].CharEnd;
            if (sel.Start >= viewBase && sel.End <= viewEnd) {
                var topScreenY = MeasureScreenYOfRowContainingOffset(sel.Start, viewBase, layout);
                var bottomScreenY = MeasureScreenYOfRowContainingOffset(sel.End - 1, viewBase, layout);
                if (topScreenY >= 0 && bottomScreenY + rh <= vpH + 0.5) {
                    return; // already fully visible, no scroll
                }
            }
        }

        // --- Compute exact target (topLine, renderOffsetY) ---
        //
        // Target a specific character in the match and a specific row of
        // that character's line.  Forward pins the last char of the
        // match (sel.End − 1); Backward pins the first char (sel.Start).
        var pinOfs = direction == SearchDirection.Forward ? sel.End - 1 : sel.Start;
        var pinLine = table.LineFromOfs(pinOfs);
        var pinLineStart = table.LineStartOfs(pinLine);
        if (pinLineStart < 0) return; // streaming-load gap, retry next frame
        var charInLine = (int)(pinOfs - pinLineStart);
        var pinRowInLine = ComputeRowOfCharInLine(pinLine, charInLine);

        long targetTopLine;
        double targetRenderOffsetY;
        if (direction == SearchDirection.Backward) {
            // Pin the match's first row to viewport top: topLine is the
            // match's line, renderOffsetY shifts so the first rows of
            // the line are above the viewport.  O(1) — no walk needed.
            targetTopLine = pinLine;
            targetRenderOffsetY = -pinRowInLine * rh;
        } else {
            // Pin the match's last row to viewport bottom: walk backward
            // from matchLine accumulating row counts until we have
            // enough rows above the match to push it to the bottom.
            //
            // We want (sumRowsAbove + pinRowInLine + 1) * rh ≥ vpH, so
            // the bottom of the match's row lands at or past the
            // viewport bottom.  We'll overshoot by a fraction then pull
            // back via a negative renderOffsetY.
            //
            // Max walk is bounded by vpH / rh lines (each contributes
            // at least 1 row), so this is cheap even on massive docs.
            var rowsNeededAbove = (int)Math.Ceiling(vpH / rh) - pinRowInLine - 1;
            if (rowsNeededAbove < 0) rowsNeededAbove = 0;

            targetTopLine = pinLine;
            var sumRowsAbove = 0;
            while (sumRowsAbove < rowsNeededAbove && targetTopLine > 0) {
                targetTopLine--;
                sumRowsAbove += ComputeLineRowCount(targetTopLine);
            }

            // (sumRowsAbove + pinRowInLine) * rh is the layout Y of the
            // match's row top.  We want its BOTTOM at vpH, so its top
            // should be at vpH − rh.  renderOffsetY adjusts.
            var matchRowLayoutY = (sumRowsAbove + pinRowInLine) * rh;
            targetRenderOffsetY = (vpH - rh) - matchRowLayoutY;
            // If we ran out of lines (near doc start), renderOffsetY may
            // end up positive — clamp to 0 so we show from line 0, top-
            // aligned, accepting that the match can't be pushed to the
            // bottom.
            if (targetRenderOffsetY > 0) targetRenderOffsetY = 0;
        }

        // --- Near-end remap: anchor to doc tail ---
        //
        // If targetTopLine is so close to the end of the doc that there
        // aren't enough rows below it to fill the viewport, pin-at-top
        // (Backward) or walked-backward-from-pinLine (Forward) would
        // leave blank space below the last line.  Instead, walk backward
        // from the last line accumulating exact row counts until we have
        // enough rows to fill the viewport.  The match is guaranteed to
        // be visible somewhere in the viewport (since pinLine is inside
        // the walked range), just not at the exact requested edge.
        //
        // This remap, combined with <see cref="LayoutWindowed"/>'s pull-
        // back gate and cache-primed extent, keeps ScrollExact on the
        // happy path for every scroll target — no fallback to estimate-
        // based ScrollValue writes, no "scrollbar at bottom with blank
        // below last line" (bug #3), no "extent collapses so FindNext
        // can't scroll back up" (bug #2).
        var lineCount = table.LineCount;
        var viewportRows = Math.Max(1, (int)Math.Ceiling(vpH / rh));
        var rowsBelowTarget = 0;
        for (var ln = targetTopLine; ln < lineCount && rowsBelowTarget < viewportRows; ln++) {
            rowsBelowTarget += ComputeLineRowCount(ln);
        }
        if (rowsBelowTarget < viewportRows) {
            // Walk backward from the last line until the accumulated row
            // count reaches the viewport row count (or we hit line 0).
            var tailTopLine = lineCount - 1;
            var tailRowSum = ComputeLineRowCount(tailTopLine);
            while (tailRowSum < viewportRows && tailTopLine > 0) {
                tailTopLine--;
                tailRowSum += ComputeLineRowCount(tailTopLine);
            }
            targetTopLine = tailTopLine;
            // Align the layout tail with the viewport bottom.  If the
            // walked range exceeds the viewport (overshoot from the last
            // step), renderOffsetY goes negative pushing content up.
            targetRenderOffsetY = vpH - tailRowSum * rh;
            if (targetRenderOffsetY > 0) targetRenderOffsetY = 0;
        }

        // --- Apply as a single state update ---
        ScrollExact(targetTopLine, targetRenderOffsetY);
    }

    /// <summary>
    /// Measures the screen-Y (pixels from the top of the control) of the
    /// top edge of the row containing the given character offset.  Clamps
    /// the offset to the layout's character range.
    /// </summary>
    /// <remarks>
    /// For the last character of a wrapped row, <c>GetCaretBounds(charOfs + 1)</c>
    /// returns a rect on the row <em>after</em> the character — because
    /// the caret at <c>charOfs + 1</c> sits at the start of the next
    /// row.  Callers that need the row containing a character must pass
    /// the character offset, not the past-the-end caret offset.  See
    /// the 2026-04-09 "last word of row caused extra-row scroll" bug.
    /// </remarks>
    private double MeasureScreenYOfRowContainingOffset(
            long charOfs, long viewBase, LayoutResult layout) {
        var local = (int)(charOfs - viewBase);
        if (local < 0) local = 0;
        var maxLocal = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (local > maxLocal) local = (int)maxLocal;
        var rect = _layoutEngine.GetCaretBounds(local, layout);
        return rect.Y + RenderOffsetY;
    }

    /// <summary>
    /// Returns the exact number of visual rows line <paramref name="lineIdx"/>
    /// occupies under the current wrap settings.  Uses the mono row breaker
    /// for lines without tabs or control characters (the common case);
    /// falls back to <see cref="TextLayout"/> measurement for lines with
    /// tabs or other slow-path triggers.
    /// </summary>
    /// <remarks>
    /// This is the primitive that lets scroll targeting place a specific
    /// row of a wrapped paragraph at the viewport edge without relying on
    /// the char-density estimate.  Cost: O(lineLength / charsPerRow) per
    /// call on the mono path.  For short wrapped lines (~5 rows) this is
    /// a handful of <see cref="MonoRowBreaker.NextRow"/> iterations —
    /// microseconds.
    /// </remarks>
    internal int ComputeLineRowCount(long lineIdx) {
        var doc = Document;
        if (doc == null) return 1;
        var table = doc.Table;
        if (lineIdx < 0 || lineIdx >= table.LineCount) return 1;

        // Wrap off and char-wrap modes both render a logical line in a
        // known-fixed number of rows (1 for wrap-off, multiple of
        // cpr-chunks for char-wrap — but ScrollSelectionIntoView doesn't
        // use char-wrap's path).  Wrap-off returns 1 unconditionally.
        if (!_wrapLines && !_charWrapMode) return 1;
        if (_charWrapMode) {
            var cpr = _charWrapCharsPerRow > 0 ? _charWrapCharsPerRow : 80;
            var contentLen = table.LineContentLength((int)lineIdx);
            if (contentLen <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling((double)contentLen / cpr));
        }

        var lineStart = table.LineStartOfs(lineIdx);
        if (lineStart < 0) return 1; // streaming-load gap
        var len = table.LineContentLength((int)lineIdx);
        if (len <= 0) return 1;
        if (len > PieceTable.MaxGetTextLength) return 1; // too long, punt

        // Compute effective row widths for the mono path.
        var (firstRowChars, contRowChars) = GetMonoRowWidths();
        if (firstRowChars <= 0) return 1;

        var text = table.GetText(lineStart, len);

        // Slow-path fallback: fall back to a TextLayout measurement if
        // the actual rendering won't use the mono row breaker — either
        // because the font is proportional (mono path refuses) or the
        // line contains tabs / control chars (mono path rejects those
        // too).  Keeping this check aligned with <see cref="MonoLineLayout.TryBuild"/>
        // is critical: if ScrollExact computes a row count that differs
        // from the rendered row count, the target position will be off
        // by (difference × rh) pixels and the match can land off-viewport.
        if (!IsFontMonospace() || ContainsSlowPathChars(text)) {
            return SlowPathRowCount(text);
        }

        return MonoRowBreaker.CountRows(text, firstRowChars, contRowChars);
    }

    /// <summary>
    /// Returns the zero-based row index within a line that contains the
    /// character at <paramref name="charInLine"/>.  Uses
    /// <see cref="MonoRowBreaker.RowOfChar"/> for the mono path.
    /// </summary>
    internal int ComputeRowOfCharInLine(long lineIdx, int charInLine) {
        var doc = Document;
        if (doc == null) return 0;
        var table = doc.Table;
        if (lineIdx < 0 || lineIdx >= table.LineCount) return 0;

        if (!_wrapLines && !_charWrapMode) return 0;
        if (_charWrapMode) {
            var cpr = _charWrapCharsPerRow > 0 ? _charWrapCharsPerRow : 80;
            return charInLine / cpr;
        }

        var lineStart = table.LineStartOfs(lineIdx);
        if (lineStart < 0) return 0;
        var len = table.LineContentLength((int)lineIdx);
        if (len <= 0) return 0;
        if (len > PieceTable.MaxGetTextLength) return 0;

        var (firstRowChars, contRowChars) = GetMonoRowWidths();
        if (firstRowChars <= 0) return 0;

        var text = table.GetText(lineStart, len);

        // When the font is proportional or the line has tabs/control
        // chars, the actual rendering uses TextLayout and the mono
        // breaker's row index won't match — measure with TextLayout
        // instead via the slow path.
        if (!IsFontMonospace() || ContainsSlowPathChars(text)) {
            return SlowPathRowOfChar(text, charInLine);
        }

        return MonoRowBreaker.RowOfChar(text, charInLine, firstRowChars, contRowChars);
    }

    /// <summary>
    /// Returns whether the current font resolves to a fixed-pitch face.
    /// When this is false the mono rendering fast path is disabled and
    /// all layout measurements go through <see cref="TextLayout"/>.
    /// ScrollExact must mirror this decision to keep row counts honest.
    /// </summary>
    private bool IsFontMonospace() {
        var typeface = new Typeface(FontFamily);
        var gtf = typeface.GlyphTypeface;
        return gtf != null && gtf.Metrics.IsFixedPitch;
    }

    /// <summary>
    /// Returns the row index within <paramref name="text"/> containing
    /// the character at <paramref name="charInLine"/> using a TextLayout
    /// measurement.  Used when the font is proportional or the text has
    /// tabs that force the slow path.
    /// </summary>
    private int SlowPathRowOfChar(string text, int charInLine) {
        var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
        var textW = GetTextWidth(maxW);
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            text, typeface, EffectiveFontSize, ForegroundBrush,
            textWrapping: Avalonia.Media.TextWrapping.Wrap, maxWidth: textW);
        var clamped = Math.Clamp(charInLine, 0, Math.Max(0, text.Length - 1));
        var hit = tl.HitTestTextPosition(clamped);
        var rh = GetRowHeight();
        return Math.Max(0, (int)Math.Round(hit.Y / rh));
    }

    /// <summary>
    /// Computes the <c>firstRowChars</c> and <c>contRowChars</c> that
    /// <see cref="MonoLineLayout.TryBuild"/> uses for the current wrap
    /// settings.  Continuation rows are shrunk by the hanging-indent
    /// character count.
    /// </summary>
    private (int firstRowChars, int contRowChars) GetMonoRowWidths() {
        var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
        var textW = GetTextWidth(maxW);
        if (!double.IsFinite(textW) || textW <= 0) return (int.MaxValue, int.MaxValue);
        var cw = GetCharWidth();
        if (cw <= 0) return (int.MaxValue, int.MaxValue);
        var maxChars = Math.Max(1, (int)(textW / cw));
        var hangingIndentChars = _hangingIndent && _wrapLines && !_charWrapMode
            ? Math.Max(0, _indentWidth / 2)
            : 0;
        var firstRowChars = maxChars;
        var contRowChars = Math.Max(1, maxChars - hangingIndentChars);
        return (firstRowChars, contRowChars);
    }

    /// <summary>
    /// True if the text contains any character that forces the slow
    /// (TextLayout) rendering path: tabs, control chars, etc.  Mirrors
    /// the check in <see cref="MonoLineLayout.TryBuild"/> (character &lt; 32
    /// rejects the fast path).
    /// </summary>
    private static bool ContainsSlowPathChars(string text) {
        for (var i = 0; i < text.Length; i++) {
            if (text[i] < 32) return true;
        }
        return false;
    }

    /// <summary>
    /// Counts rows via an Avalonia <c>TextLayout</c>.  Used only as a
    /// fallback for lines that can't use the mono row breaker (tabs,
    /// control chars, proportional font ligatures, etc.).
    /// </summary>
    private int SlowPathRowCount(string text) {
        var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
        var textW = GetTextWidth(maxW);
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            text, typeface, EffectiveFontSize, ForegroundBrush,
            textWrapping: Avalonia.Media.TextWrapping.Wrap, maxWidth: textW);
        var rh = GetRowHeight();
        var h = tl.Height > 0 ? tl.Height : rh;
        return Math.Max(1, (int)Math.Round(h / rh));
    }

    /// <summary>
    /// Force the next layout pass to use the specified <paramref name="topLine"/>
    /// and <paramref name="renderOffsetY"/>, bypassing the scroll-value-to-
    /// topLine estimate formula.  Primes <see cref="LayoutWindowed"/>'s
    /// incremental-scroll cache (<c>_winTopLine</c>, <c>_winRenderOffsetY</c>,
    /// <c>_winScrollOffset</c>) and sets a scroll value that's "close enough"
    /// to those for the cached path to take over.
    ///
    /// <para>Why the cache override works: <c>LayoutWindowed</c> normally
    /// computes <c>topLine</c> by calling <c>EstimateWrappedTopLine</c>, which
    /// uses a char-density estimate that allocates each logical line a
    /// scroll-space width proportional to its char count (not its actual row
    /// count).  For lines with more real rows than the estimate allocates,
    /// no scroll value can pin to every row — adjacent scroll values jump
    /// past the line entirely.  The cached path, triggered by
    /// <c>isSmallScroll</c> and a matching <c>_winTopLine</c>, honors the
    /// cache directly and computes <c>RenderOffsetY</c> via the pure-scroll
    /// formula <c>_winRenderOffsetY - (scroll - _winScrollOffset)</c>.  By
    /// setting <c>scroll == _winScrollOffset</c>, the delta is zero and
    /// <c>RenderOffsetY</c> equals exactly what we supplied.</para>
    ///
    /// <para>This keeps the scroll value roughly in sync with rendered
    /// position (so the scrollbar thumb isn't wildly off) while guaranteeing
    /// pixel-exact content positioning.</para>
    /// </summary>
    private void ScrollExact(long topLine, double renderOffsetY) {
        var doc = Document;
        if (doc == null) return;
        var table = doc.Table;
        if (topLine < 0) topLine = 0;
        var lineCount = table.LineCount;
        if (topLine >= lineCount) topLine = Math.Max(0, lineCount - 1);

        var rh = GetRowHeight();
        var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
        var textW = GetTextWidth(maxW);
        var charsPerRow = GetCharsPerRow(textW);

        // Pick a scroll value that falls in the estimate's scroll-window
        // for `topLine`.  EstimateWrappedLineY(topLine) is the lower edge
        // of that window, so we sit right at its start.  The cache override
        // (below) carries the exact rendering info; the scroll value is
        // just a cosmetic hint for the scrollbar and for preventing the
        // cache-invalidation guard from tripping.
        var approxScroll = EstimateWrappedLineY(topLine, table, charsPerRow, rh);

        // Assign _scrollOffset directly (bypassing the ScrollValue setter)
        // so we can update it in lock-step with the cache fields without
        // triggering the side effects that would clear the cache we're
        // about to set.  We also dispose _layout manually so the next
        // EnsureLayout rebuilds with our state.
        _scrollOffset = new Vector(_scrollOffset.X, approxScroll);
        _winTopLine = topLine;
        _winScrollOffset = approxScroll;
        _winRenderOffsetY = renderOffsetY;
        // _winFirstLineHeight is only consulted by the "advance by 1"
        // branch in LayoutWindowed's isSmallScroll logic, which we won't
        // hit (ds = 0).  Set to a reasonable placeholder via row count.
        _winFirstLineHeight = ComputeLineRowCount(topLine) * rh;
        // Arm the exact-pin gate.  LayoutWindowed will consume this on
        // its next run and clear it, so only the frame that renders our
        // target uses the pull-back skip and tight-extent math.  Any
        // subsequent scroll (wheel / drag / arrow) sees the flag already
        // cleared and goes through the normal path.
        _winExactPinActive = true;

        _layout?.Dispose();
        _layout = null;
        InvalidateVisual();
        InvalidateArrange();
        FireScrollChanged();
    }

    // -------------------------------------------------------------------------
    // Page scrolling (keyboard and scrollbar track-click)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scrolls the viewport by one page so that the current bottom line becomes
    /// the new top (page down) or vice versa.  Works in logical-line space so
    /// wrapping is handled correctly.  Called by the scrollbar track-click and
    /// used internally by <see cref="MoveCaretByPage"/>.
    /// </summary>
    public void ScrollPage(int direction) {
        var doc = Document;
        if (doc == null) return;
        var table = doc.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var layout = EnsureLayout();
        var topLine = table.LineFromOfs(layout.ViewportBase);
        var lastVisibleLine = FindLastVisibleLogicalLine(layout, table);
        var pageLines = Math.Max(1L, lastVisibleLine - topLine);

        long targetLine;
        if (direction > 0) {
            targetLine = Math.Min(lastVisibleLine, lineCount - 1);
            // If we can't advance (viewport shows only one line due to wrapping),
            // advance by at least one line.
            if (targetLine <= topLine) {
                targetLine = Math.Min(topLine + 1, lineCount - 1);
            }
        } else {
            targetLine = Math.Max(0, topLine - pageLines);
            if (targetLine >= topLine && topLine > 0) {
                targetLine = topLine - 1;
            }
        }

        ScrollToTopLine(targetLine, table);
    }

    /// <summary>
    /// Sets <see cref="ScrollValue"/> so that the given logical line appears
    /// at the top of the viewport.
    /// </summary>
    private void ScrollToTopLine(long targetLine, PieceTable table) {
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var rh = GetRowHeight();
        if (!_wrapLines) {
            // Wrapping off: exact.
            ScrollValue = targetLine * rh;
        } else {
            // Wrapping on: exact when the row index is built, estimate
            // otherwise.  Nudge by a sub-pixel amount so the round-trip
            // in LayoutWindowed always resolves to targetLine.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);
            ScrollValue = ExactOrEstimateLineY(targetLine, table, charsPerRow, rh) + 0.01;
        }
    }

    /// <summary>
    /// Returns the logical line index of the last fully visible line in the
    /// current layout.  A visual row is "fully visible" when its entire height
    /// fits within the viewport after applying <see cref="RenderOffsetY"/>.
    /// </summary>
    private long FindLastVisibleLogicalLine(LayoutResult layout, PieceTable table) {
        var lines = layout.Lines;
        if (lines.Count == 0) {
            return table.LineFromOfs(layout.ViewportBase);
        }

        var rh = layout.RowHeight;
        for (var i = lines.Count - 1; i >= 0; i--) {
            var screenBottom = (lines[i].Row + lines[i].HeightInRows) * rh + RenderOffsetY;
            if (screenBottom <= _viewport.Height + 0.5) {
                return table.LineFromOfs(layout.ViewportBase + lines[i].CharStart);
            }
        }

        return table.LineFromOfs(layout.ViewportBase);
    }
    /// <summary>
    /// Runs <paramref name="mutate"/>, which changes a layout-affecting flag
    /// (wrap on/off, wrap column, hanging indent, etc.), while keeping the
    /// caret at the same screen-Y position across the layout rebuild.
    ///
    /// <para>Without this, toggling <see cref="WrapLines"/> made the caret
    /// appear to jump to a different viewport row because <c>_scrollOffset.Y</c>
    /// was preserved verbatim across the toggle but the old pixel offset mapped
    /// to a completely different visual row in the new (wrap-on vs wrap-off)
    /// layout.  The symptom reported in the 2026-04-09 session: scrolled wrap-on
    /// with caret on displayed row 44, toggle wrap off, caret snaps to row 0
    /// because the old scroll translates (via <c>scrollY / rh</c>) to a wrap-off
    /// topLine that is BELOW the caret's logical line, so the caret is outside
    /// the newly-built layout and the fallback to <see cref="ScrollCaretIntoView"/>
    /// puts it at the top of the viewport.</para>
    ///
    /// <para>Approach:</para>
    /// <list type="number">
    ///   <item>Capture the caret's screen-Y in the pre-mutate layout.</item>
    ///   <item>Run the mutation and <see cref="InvalidateLayout"/>.</item>
    ///   <item>Compute a direct first-guess target scroll: the caret's
    ///     estimated doc-Y in the new mode minus the saved screen-Y.  This is
    ///     exact in wrap-off mode (<c>caretLine * rh</c>) and an estimate in
    ///     wrap-on mode (via <see cref="EstimateWrappedLineY"/>).</item>
    ///   <item>Set that scroll, rebuild the layout, and iterate up to 3 times
    ///     to converge on the exact screen-Y — mirrors the retry loop in
    ///     <see cref="ScrollCaretIntoView"/>'s wrap-on branch.</item>
    /// </list>
    /// <para>The direct first-guess is the key fix: it avoids the null-layout
    /// fallback that the simpler delta-based approach tripped over when the
    /// caret was outside the new layout's window.</para>
    /// </summary>
    private void PreserveCaretScreenYAcross(Action mutate) {
        var doc = Document;
        if (doc == null || IsLoading) {
            mutate();
            InvalidateLayout();
            return;
        }

        // Capture the caret's current screen-Y before the mutation.
        var captured = TryCaptureCaretScreenY();

        mutate();
        InvalidateLayout();

        if (captured is not { } savedScreenY) {
            return;
        }

        // The caret may now be outside the new layout's window (the pre-mutate
        // scrollY maps to a completely different topLine in the new wrap mode).
        // ScrollCaretIntoView is the load-bearing "caret must be visible"
        // invariant and unconditionally places the caret somewhere inside
        // [0, viewportHeight] — use it as a seed, then correct from there.
        // Without this seed, the delta iteration below can break out
        // immediately (TryComputeCaretScreenY returns null when the caret
        // is outside the layout window) and leave scroll unchanged — which
        // is exactly the bug the user reported: wrap on → off snapped the
        // caret to the top of the viewport.
        ScrollCaretIntoView();

        // Fine-tune: measure where the caret actually landed and correct by
        // the delta to <paramref name="savedScreenY"/>.  Each pass invalidates
        // the layout and rebuilds so line.Row and RenderOffsetY are re-derived
        // from the new scrollY.  Three passes is enough in practice: the
        // first brings us within a few pixels, the second nails it.  The
        // ScrollValue clamp handles doc-top/bottom edge cases.
        for (var pass = 0; pass < 3; pass++) {
            InvalidateLayout();
            var layout = EnsureLayout();
            var actualY = TryComputeCaretScreenY(layout);
            if (actualY is not { } newY) break;
            var delta = newY - savedScreenY;
            if (Math.Abs(delta) < 0.5) break;
            ScrollValue = _scrollOffset.Y + delta;
        }
    }

    /// <summary>
    /// Returns the caret's current screen-Y in pixels (the Y of its top edge
    /// inside the control), or <c>null</c> if there's no layout, no caret,
    /// or the caret isn't within the currently-laid-out window.
    /// </summary>
    private double? TryCaptureCaretScreenY() {
        var doc = Document;
        if (doc == null) return null;
        var layout = EnsureLayout();
        if (layout.Lines.Count == 0) return null;

        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines[^1].CharEnd;
        if (localCaret < 0 || localCaret > totalChars) return null;

        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);
        var screenY = caretRect.Y + RenderOffsetY;
        // Only preserve when the caret was actually visible in the viewport
        // — preserving a caret that was off-screen is worse than just
        // letting ScrollCaretIntoView bring it into view.
        if (screenY < 0 || screenY > _viewport.Height) return null;
        return screenY;
    }

    /// <summary>
    /// Computes where the caret would render in <paramref name="layout"/>, or
    /// returns <c>null</c> if the caret is outside that layout's window.
    /// </summary>
    private double? TryComputeCaretScreenY(LayoutResult layout) {
        var doc = Document;
        if (doc == null || layout.Lines.Count == 0) return null;

        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines[^1].CharEnd;
        if (localCaret < 0 || localCaret > totalChars) return null;

        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);
        return caretRect.Y + RenderOffsetY;
    }

    // -------------------------------------------------------------------------
    // Test-only helpers — exposed via InternalsVisibleTo to DMEdit.App.Tests.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the caret's current screen-Y relative to the control's top
    /// edge, or <c>null</c> if the caret isn't currently on screen.
    /// Used by <c>EditorControlWrapToggleTests</c> to verify that wrap toggles
    /// preserve the caret's vertical position.
    /// </summary>
    internal double? GetCaretScreenYForTest() => TryCaptureCaretScreenY();

    /// <summary>
    /// Invokes <see cref="MoveCaretToLineEdge"/> on behalf of a test.
    /// Mirrors the NavMoveHome / NavMoveEnd command bindings.
    /// </summary>
    internal void MoveCaretToLineEdgeForTest(bool toStart, bool extend) {
        var doc = Document;
        if (doc == null) return;
        MoveCaretToLineEdge(doc, toStart, extend);
    }

    /// <summary>
    /// Invokes <see cref="MoveCaretHorizontal"/> on behalf of a test.
    /// Mirrors the NavMoveLeft / NavMoveRight command bindings.
    /// </summary>
    internal void MoveCaretHorizontalForTest(int delta, bool byWord, bool extend) {
        var doc = Document;
        if (doc == null) return;
        MoveCaretHorizontal(doc, delta, byWord, extend);
    }

    /// <summary>
    /// Invokes <see cref="MoveCaretVertical"/> on behalf of a test.
    /// Mirrors the NavMoveUp / NavMoveDown command bindings (without the
    /// Coalesce/FlushCompound wrapper — tests aren't in edit coalescing mode).
    /// </summary>
    internal void MoveCaretVerticalForTest(int lineDelta, bool extend) {
        var doc = Document;
        if (doc == null) return;
        MoveCaretVertical(doc, lineDelta, extend);
    }

    /// <summary>
    /// Moves the caret by one page while keeping it at the same screen row.
    /// The viewport scrolls; the caret stays on its row.  Near document
    /// boundaries the scroll is reduced so the caret's row always has content.
    /// </summary>
    /// <remarks>
    /// <c>layout.Lines</c> are <em>logical</em> lines — a wrapped line
    /// occupies one entry but spans multiple visual rows.  The screen-row
    /// math therefore works in screen-pixel space, not in line-index space.
    /// <para/>
    /// The caret's screen Y is captured before the scroll, quantised to
    /// an integer visual row, and then restored after the scroll.
    /// <see cref="RenderOffsetY"/> converts between layout and screen
    /// coordinates on both sides, so the screen position is preserved
    /// even when the render offset changes between layouts.
    /// </remarks>
    private void MoveCaretByPage(Document doc, int direction, bool extend) {
        var table = doc.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var curLayout = EnsureLayout();
        var rh = curLayout.RowHeight;
        var caretRow = 0;

        if (curLayout.Lines.Count > 0) {
            var localCaret = (int)(doc.Selection.Caret - curLayout.ViewportBase);
            var totalChars = curLayout.Lines[^1].CharEnd;
            if (localCaret >= 0 && localCaret <= totalChars) {
                var caretRect = _layoutEngine.GetCaretBounds(localCaret, curLayout, _caretIsAtEnd);
                caretRow = GetCaretScreenRow(caretRect, rh);
                if (_preferredCaretX < 0) {
                    _preferredCaretX = caretRect.X;
                }
            }
        }

        // Scroll the viewport by one page.
        ScrollPage(direction);

        // Relayout at the new scroll position.
        _layout?.Dispose();
        _layout = null;
        var newLayout = EnsureLayout();

        // If the target screen row is past the layout content, back up so
        // the last document line lands at caretRow.
        var targetY = caretRow * rh + rh / 2 - RenderOffsetY;
        if (targetY >= newLayout.TotalHeight && newLayout.Lines.Count > 0) {
            var lastLogicalLine = lineCount - 1;
            var backupTopLine = Math.Max(0, lastLogicalLine - caretRow);
            ScrollToTopLine(backupTopLine, table);
            _layout?.Dispose();
            _layout = null;
            newLayout = EnsureLayout();
        }

        // Place the caret at the same visual row.
        if (newLayout.Lines.Count == 0) return;
        var newCaret = HitTestAtScreenRow(caretRow, rh, newLayout);

        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) {
        InvalidateRowIndex(); // content changed — row counts are stale
        _caretIsAtEnd = false; // edit invalidates boundary context
        InvalidateLayout();
    }

    // -------------------------------------------------------------------------
    // Tab scroll state save / restore
    // -------------------------------------------------------------------------

    /// <summary>
    /// Captures scroll and windowed-layout tracking state into
    /// <paramref name="tab"/> so it can be restored when switching back.
    /// </summary>
    public void SaveScrollState(TabState tab) {
        tab.ScrollOffsetX = _scrollOffset.X;
        tab.ScrollOffsetY = _scrollOffset.Y;
        tab.WinTopLine = _winTopLine;
        tab.WinScrollOffset = _winScrollOffset;
        tab.WinRenderOffsetY = _winRenderOffsetY;
        tab.WinFirstLineHeight = _winFirstLineHeight;
    }

    /// <summary>
    /// Replaces the document and restores scroll state in a single
    /// operation without any intermediate frame where the layout is
    /// null. Used by auto-reload so the viewport stays visually stable.
    /// </summary>
    public void ReplaceDocument(Document newDoc, TabState scrollState) {
        if (Document is Document old) {
            old.Changed -= OnDocumentChanged;
        }
        // Set the property value. OnPropertyChanged will fire but
        // _keepScrollOnSwap prevents scroll reset and layout disposal.
        _keepScrollOnSwap = true;
        Document = newDoc;

        // Apply the target scroll position. Don't dispose the old
        // layout — MeasureOverride will rebuild it atomically so there
        // is never a frame with _layout == null.
        _scrollOffset = new Vector(scrollState.ScrollOffsetX, scrollState.ScrollOffsetY);
        _winTopLine = scrollState.WinTopLine;
        _winScrollOffset = scrollState.WinScrollOffset;
        _winRenderOffsetY = scrollState.WinRenderOffsetY;
        _winFirstLineHeight = scrollState.WinFirstLineHeight;

        // Force a full measure → layout → render cycle. MeasureOverride
        // rebuilds the layout with the new document's content and fires
        // ScrollChanged (which syncs the scrollbar extent/position).
        // InvalidateVisual ensures the render pass runs even if the
        // available size hasn't changed.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Restores previously saved scroll state after a document swap.
    /// Must be called <b>after</b> setting <see cref="Document"/>,
    /// which resets scroll to (0,0).
    /// </summary>
    public void RestoreScrollState(TabState tab) {
        _scrollOffset = new Vector(tab.ScrollOffsetX, tab.ScrollOffsetY);
        _winTopLine = tab.WinTopLine;
        _winScrollOffset = tab.WinScrollOffset;
        _winRenderOffsetY = tab.WinRenderOffsetY;
        _winFirstLineHeight = tab.WinFirstLineHeight;
        _layout?.Dispose();
        _layout = null;
        InvalidateMeasure();
        InvalidateVisual();
        FireScrollChanged();
    }
}
