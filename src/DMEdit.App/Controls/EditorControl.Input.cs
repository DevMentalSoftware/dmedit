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
        var layout = EnsureLayout();
        var pt = e.GetPosition(this);
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;

        // Determine caret affinity from click position.  If the hit-test
        // offset lands at a soft-break boundary, the click Y tells us
        // which visual row the user intended: if the default rendering
        // (right affinity) would place the caret on a DIFFERENT row than
        // the one clicked, use left affinity to park the caret at the end
        // of the clicked row instead.
        _caretIsAtEnd = false;
        var defaultRect = _layoutEngine.GetCaretBounds(localOfs, layout);
        var clickRow = (int)(layoutPt.Y / layout.RowHeight);
        var defaultRow = (int)((defaultRect.Y) / layout.RowHeight);
        if (defaultRow > clickRow) {
            _caretIsAtEnd = true;
        }

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
            if (alt && !_wrapLines && !_charWrapMode) {
                // Alt+click: start column (block) selection.
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
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;
        if (_columnDrag && doc.ColumnSel is { } colSel) {
            var table = doc.Table;
            var line = (int)table.LineFromOfs(ofs);
            var col = ColumnSelection.OfsToCol(table, ofs, _indentWidth);
            doc.ColumnSel = colSel.ExtendTo(line, col);
        } else {
            doc.Selection = doc.Selection.ExtendTo(ofs);
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
        _columnDrag = false;
        _pointerDown = false;
        e.Pointer.Capture(null);
        ResetCaretBlink();
    }

    protected override void OnGotFocus(GotFocusEventArgs e) {
        base.OnGotFocus(e);
        // Show caret even during loading — user can navigate and position caret.
        ResetCaretBlink();
    }

    protected override void OnLostFocus(RoutedEventArgs e) {
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
        var rh = GetRowHeight();
        var delta = -e.Delta.Y * rh * 3; // 3 rows per wheel notch
        ScrollValue += delta;
        e.Handled = true;
    }
}
