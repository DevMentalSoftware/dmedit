using System;
using System.Linq;
using Avalonia;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DMEdit.Rendering.Layout;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

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
    /// Moves the caret to the given offset and scrolls it into view.
    /// Used by GoTo Line and similar external navigation features.
    /// </summary>
    public void GoToPosition(long offset) {
        var doc = Document;
        if (doc == null) return;
        offset = Math.Clamp(offset, 0, doc.Table.Length);
        doc.Selection = Core.Documents.Selection.Collapsed(offset);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
        Focus();
    }

    public void ScrollCaretIntoView() {
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
            if (caretY < _scrollOffset.Y) {
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + vpH + 1) {
                // +1 tolerance for sub-pixel rounding at the bottom edge.
                ScrollValue = caretY + rh - vpH;
            }
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
            if (caretY < _scrollOffset.Y) {
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + vpH) {
                ScrollValue = caretY + rh - vpH;
            }
        } else {
            // ── Wrapping on ───────────────────────────────────────────
            // Lines can span multiple visual rows. Use char-based
            // estimation to position the scroll, then verify with an
            // actual layout pass since the estimate can be off.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);
            var caretY = EstimateWrappedLineY(caretLine, table, charsPerRow, rh);

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
            if (caretY < _scrollOffset.Y) {
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + vpH) {
                ScrollValue = caretY + rh - vpH;
            }

            // Verify with actual layout. Repeat up to 3 times because
            // the first correction can be off when RenderOffsetY shifts.
            for (var pass = 0; pass < 3; pass++) {
                InvalidateLayout();
                var layout = EnsureLayout();
                var caretLocalOfs = (int)(caret - layout.ViewportBase);
                var layoutCharEnd = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
                if (caretLocalOfs < 0 || caretLocalOfs > layoutCharEnd) break;

                var caretRect = _layoutEngine.GetCaretBounds(caretLocalOfs, layout);
                var caretScreenY = caretRect.Y + RenderOffsetY;
                var caretH = Math.Ceiling(Math.Max(caretRect.Height, rh));
                if (caretScreenY < 0) {
                    PerfStats.ScrollRetries++;
                    ScrollValue = _scrollOffset.Y + caretScreenY;
                } else if (caretScreenY + caretH > vpH) {
                    PerfStats.ScrollRetries++;
                    ScrollValue = _scrollOffset.Y + caretScreenY + caretH - vpH;
                } else {
                    break; // caret is visible — done
                }
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
            // Wrapping on: estimated. Nudge by a sub-pixel amount so the
            // round-trip in LayoutWindowed always resolves to targetLine.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);
            ScrollValue = EstimateWrappedLineY(targetLine, table, charsPerRow, rh) + 0.01;
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
                var caretRect = _layoutEngine.GetCaretBounds(localCaret, curLayout);
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
