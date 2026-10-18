using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

// Pointer / mouse wheel input partial of EditorControl.  OnTextInput
// and the keyboard-driven commands live with the command dispatch in
// EditorControl.Commands.cs; this file contains just the low-level
// pointer event handlers and the wheel-scrolling glue.
public sealed partial class EditorControl {

    // -------------------------------------------------------------------------
    // Mouse / pointer input
    // -------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (IsLoading) return;
        var props = e.GetCurrentPoint(this).Properties;


        _preferredCaretX = -1;
        _preferredCaretCol = -1;
        FlushCompound();

        // Hide caret and pause blinking while processing the press.
        _caretVisible = false;
        _caretTimer.Stop();
        if (props.IsMiddleButtonPressed) {
            _middleDrag = true;
            _middleDragStartY = e.GetPosition(this).Y;
            Cursor = new Cursor(StandardCursorType.None);
            ScrollBar?.BeginExternalMiddleDrag();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        Focus();
        var doc = Document;
        if (doc == null) {
            return;
        }
        if (e.Pointer.Type == PointerType.Touch) {
            // Touch: dragging pans like every touch UI, rather than
            // selecting (GH #25).  The caret is placed on release, and
            // only if the finger didn't move — a pan never moves it.
            _touchDown = true;
            _touchPanning = false;
            _touchStart = e.GetPosition(this);
            _touchLast = _touchStart;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var layout = EnsureLayout();
        var pt = e.GetPosition(this);
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
        // TextPosition-native hit-test: the click Y picks the visual row
        // directly, so the caret lands on the row clicked — no boundary-
        // affinity heuristic needed.
        var clickedPos = _layoutEngine.HitTestPos(layoutPt, layout);
        var ofs = clickedPos.CharOffset;
        _caretPosition = clickedPos;

        var isLeft = props.IsLeftButtonPressed;
        // Left-click: place caret or extend selection (with Shift).
        // Right-click: place caret only (no Shift-extend, no drag-select).
        if (isLeft) {
            var clickCount = e.ClickCount;
            if (clickCount == 3) {
                // Triple-click: select entire line.
                doc.Selection = Selection.Collapsed(ofs);
                doc.SelectLine();
                e.Handled = true;
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
            if (clickCount == 2) {
                // Double-click: select word at click position.
                doc.Selection = Selection.Collapsed(ofs);
                doc.SelectWord();
                e.Handled = true;
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (alt && !_charWrapMode) {
                // Alt+click: start column (block) selection.
                // Works in wrap mode too — wrapping is display-only, so the
                // selection operates on logical lines and columns as usual.
                var table = doc.Table;
                var line = (int)table.LineFromOfs(ofs);
                var col = ColumnSelection.OfsToCol(table, ofs, _indentWidth);
                doc.ColumnSel = new ColumnSelection(line, col, line, col);
                doc.Selection = Selection.Collapsed(ofs);
                _columnDrag = true;
                _pointerDown = true;
                e.Pointer.Capture(this);
                // Force carets steady-visible (no blinking) during column
                // drag so the user can see the anchor point, especially
                // when dragging vertically with no horizontal extent.
                _caretVisible = true;
                _caretTimer.Stop();
            } else if (alt && _charWrapMode) {
                // Alt+click while char-wrap is on — notify user instead of
                // silently falling through to normal click.
                StatusMessage?.Invoke(ColumnBlockedByWrap);
            } else {
                // Exit column mode on normal click.
                if (doc.ColumnSel != null) {
                    doc.ColumnSel = null;
                }
                doc.Selection = shift
                    ? doc.Selection.ExtendTo(ofs)
                    : Selection.Collapsed(ofs);
                _pointerDown = true;
                e.Pointer.Capture(this);
            }
        } else {
            // Right-click: preserve selection if clicking within it,
            // otherwise collapse to the click position.
            if (!doc.Selection.IsEmpty && ofs >= doc.Selection.Start && ofs <= doc.Selection.End) {
                // Clicked inside selected text — keep selection for context menu.
            } else {
                doc.Selection = Selection.Collapsed(ofs);
            }
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (_middleDrag) {
            var deltaY = e.GetPosition(this).Y - _middleDragStartY;
            ScrollBar?.UpdateExternalMiddleDrag(deltaY);
            return;
        }
        if (_touchDown) {
            TouchMoved(e.GetPosition(this));
            return;
        }
        if (!_pointerDown) {
            return; // only left-drag extends selection
        }
        var doc = Document;
        if (doc == null) {
            return;
        }
        var layout = EnsureLayout();
        var pt = e.GetPosition(this);
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
        var dragPos = _layoutEngine.HitTestPos(layoutPt, layout);
        var ofs = dragPos.CharOffset;
        if (_columnDrag && doc.ColumnSel is { } colSel) {
            var table = doc.Table;
            var line = (int)table.LineFromOfs(ofs);
            var col = ColumnSelection.OfsToCol(table, ofs, _indentWidth);
            doc.ColumnSel = colSel.ExtendTo(line, col);
        } else {
            doc.Selection = doc.Selection.ExtendTo(ofs);
            _caretPosition = dragPos;
        }
        InvalidateVisual();
        InvalidateArrange();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (_middleDrag) {
            _middleDrag = false;
            Cursor = new Cursor(StandardCursorType.Ibeam);
            ScrollBar?.EndExternalMiddleDrag();
            e.Pointer.Capture(null);
            ResetCaretBlink();
            return;
        }
        if (_touchDown) {
            TouchReleased(e.GetPosition(this));
            e.Pointer.Capture(null);
            return;
        }
        _columnDrag = false;
        _pointerDown = false;
        e.Pointer.Capture(null);
        ResetCaretBlink();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        // A cancelled touch (e.g. the OS took over the gesture) is neither
        // a tap nor a continuing pan.
        _touchDown = false;
        _touchPanning = false;
    }

    // -------------------------------------------------------------------------
    // Touch panning
    // -------------------------------------------------------------------------

    /// <summary>
    /// Distance (DIPs) a touch must travel before it counts as a pan rather
    /// than a tap.  Fingers wobble; without a slop every tap would scroll.
    /// </summary>
    private const double TouchSlop = 8;

    private void TouchMoved(Point pt) {
        if (!_touchPanning) {
            var moved = pt - _touchStart;
            if (Math.Abs(moved.X) < TouchSlop && Math.Abs(moved.Y) < TouchSlop) {
                return;
            }
            _touchPanning = true;
        }
        // Content follows the finger: dragging up scrolls down.
        var delta = _touchLast - pt;
        _touchLast = pt;
        ScrollValue += delta.Y;
        HScrollValue += delta.X;
    }

    private void TouchReleased(Point pt) {
        var wasTap = !_touchPanning;
        _touchDown = false;
        _touchPanning = false;
        if (wasTap) {
            PlaceCaretAtPoint(pt);
        }
        ResetCaretBlink();
    }

    /// <summary>Collapses the selection to the text position under <paramref name="pt"/>.</summary>
    private void PlaceCaretAtPoint(Point pt) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        var layout = EnsureLayout();
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
        var pos = _layoutEngine.HitTestPos(layoutPt, layout);
        if (doc.ColumnSel != null) {
            doc.ColumnSel = null;
        }
        doc.Selection = Selection.Collapsed(pos.CharOffset);
        _caretPosition = pos;
        InvalidateVisual();
    }

    internal void TouchPressForTest(Point pt) {
        _touchDown = true;
        _touchPanning = false;
        _touchStart = pt;
        _touchLast = pt;
    }

    internal void TouchMoveForTest(Point pt) => TouchMoved(pt);
    internal void TouchReleaseForTest(Point pt) => TouchReleased(pt);

    protected override void OnGotFocus(FocusChangedEventArgs e) {
        base.OnGotFocus(e);
        // Show caret even during loading — user can navigate and position caret.
        ResetCaretBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e) {
        base.OnLostFocus(e);
        _caretVisible = false;
        _caretTimer.Stop();
        FlushCompound();
        SetCaretLayersVisible(false);
        InvalidateArrange();
    }

    // -------------------------------------------------------------------------
    // Mouse wheel scrolling (replaces ScrollViewer)
    // -------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            var step = e.Delta.Y > 0 ? 10 : -10;
            ZoomPercent += step;
            e.Handled = true;
            return;
        }

        var rh = GetRowHeight();
        var delta = -e.Delta.Y * rh * 3; // 3 rows per wheel notch
        ScrollValue += delta;
        e.Handled = true;
    }
}
