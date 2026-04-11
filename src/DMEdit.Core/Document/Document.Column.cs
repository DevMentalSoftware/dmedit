using System.Text;
using DMEdit.Core.Documents.History;

namespace DMEdit.Core.Documents;

// Column (multi-cursor) edit operations partial of Document.  Owns
// InsertAtCursors, DeleteBackwardAtCursors, DeleteForwardAtCursors,
// DeleteColumnSelectionContent, GetColumnSelectedText, PasteAtCursors,
// and ClearColumnSelection.
public sealed partial class Document {

    // -------------------------------------------------------------------------
    // Column (multi-cursor) edit operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="text"/> at each cursor position defined by the
    /// current <see cref="ColumnSel"/>. If the column selection has width,
    /// the selected text on each line is replaced. Lines shorter than the
    /// target column are padded with spaces. All edits are a single undo step.
    /// </summary>
    public void InsertAtCursors(string text, int tabSize) {
        if (ColumnSel is not { } colSel || text.Length == 0) {
            return;
        }
        var sels = colSel.Materialize(_table, tabSize);
        if (sels.Count == 0) {
            return;
        }

        // Multi-line text broadcasts the entire string at every caret (matches
        // VS Code / Sublime / Rider).  After the broadcast, the column
        // rectangle no longer corresponds to any rectangle in the post-insert
        // document — each row was split by the inserted newlines — so we drop
        // column mode and collapse the stream selection.  The line tree
        // remains valid because each PushInsert routes through the same
        // SpliceInsertLines path the editor uses for every other newline
        // insert; bottom-to-top order keeps offsets stable.  TRIAGE Priority 5
        // tracks the future free-multi-cursor model that would let us
        // preserve carets across this transition.
        var hasNewline = text.AsSpan().IndexOfAny('\n', '\r') >= 0;

        _history.BeginCompound();

        // Captured at the topmost (last-processed) caret so we know where to
        // collapse the stream selection after a multi-line broadcast.
        var topCaretEnd = 0L;

        // Apply bottom-to-top so earlier offsets stay valid.
        for (var i = sels.Count - 1; i >= 0; i--) {
            var s = sels[i];
            var line = colSel.TopLine + i;

            // Pad short lines to reach the left column.
            var pad = ColumnSelection.PaddingNeeded(_table, line, colSel.LeftCol, tabSize);
            if (pad > 0) {
                var lineEnd = ColumnSelection.LineContentEnd(_table, line);
                var spaces = new string(' ', pad);
                PushInsert(lineEnd, spaces);
                // Adjust the selection offsets for the padding we just added.
                s = new Selection(lineEnd + pad, lineEnd + pad);
            }

            // Delete the selected range, then insert.
            if (!s.IsEmpty) {
                var dPieces = _table.CapturePieces(s.Start, s.Len);
                _history.Push(new DeleteEdit(s.Start, s.Len, dPieces), _table, Selection);
            }
            PushInsert(s.Start, text);

            if (i == 0) {
                topCaretEnd = s.Start + text.Length;
            }
        }

        _history.EndCompound();

        if (hasNewline) {
            // Multi-line broadcast: rectangle is meaningless post-insert.
            ColumnSel = null;
            Selection = Selection.Collapsed(topCaretEnd);
        } else {
            // Single-line: preserve the column rectangle, advance every caret
            // by text.Length characters (column = char count for non-tab text).
            var newCol = colSel.LeftCol + text.Length;
            ColumnSel = new ColumnSelection(colSel.AnchorLine, newCol, colSel.ActiveLine, newCol);
            var firstCaret = ColumnSel.Value.MaterializeCarets(_table, tabSize);
            if (firstCaret.Count > 0) {
                Selection = Selection.Collapsed(firstCaret[0]);
            }
        }
        RaiseChanged();
    }

    /// <summary>
    /// Deletes one character before each cursor in the column selection (Backspace).
    /// No-op if any cursor is at column 0. All edits are a single undo step.
    /// </summary>
    public void DeleteBackwardAtCursors(int tabSize) {
        if (ColumnSel is not { } colSel) {
            return;
        }

        // If the column selection has width, delete the selected text instead.
        if (colSel.LeftCol != colSel.RightCol) {
            DeleteColumnSelectionContent(tabSize);
            return;
        }

        if (colSel.LeftCol <= 0) {
            return;
        }

        var carets = colSel.MaterializeCarets(_table, tabSize);
        if (carets.Count == 0) {
            return;
        }

        _history.BeginCompound();
        for (var i = carets.Count - 1; i >= 0; i--) {
            var caret = carets[i];
            if (caret <= 0) {
                continue;
            }
            // Handle \r\n as a single unit
            var delLen = 1;
            if (caret >= 2 && _table.GetText(caret - 2, 2) == "\r\n") {
                delLen = 2;
            }
            var bPieces = _table.CapturePieces(caret - delLen, delLen);
            _history.Push(new DeleteEdit(caret - delLen, delLen, bPieces), _table, Selection);
        }
        _history.EndCompound();

        var newCol = Math.Max(0, colSel.ActiveCol - 1);
        var newAnchorCol = Math.Max(0, colSel.AnchorCol - 1);
        ColumnSel = new ColumnSelection(colSel.AnchorLine, newAnchorCol, colSel.ActiveLine, newCol);

        var newCarets = ColumnSel.Value.MaterializeCarets(_table, tabSize);
        if (newCarets.Count > 0) {
            Selection = Selection.Collapsed(newCarets[0]);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Deletes one character after each cursor in the column selection (Delete key).
    /// All edits are a single undo step.
    /// </summary>
    public void DeleteForwardAtCursors(int tabSize) {
        if (ColumnSel is not { } colSel) {
            return;
        }

        // If the column selection has width, delete the selected text instead.
        if (colSel.LeftCol != colSel.RightCol) {
            DeleteColumnSelectionContent(tabSize);
            return;
        }

        var carets = colSel.MaterializeCarets(_table, tabSize);
        if (carets.Count == 0) {
            return;
        }

        _history.BeginCompound();
        for (var i = carets.Count - 1; i >= 0; i--) {
            var caret = carets[i];
            if (caret >= _table.Length) {
                continue;
            }
            var delLen = 1;
            if (caret + 1 < _table.Length && _table.GetText(caret, 2) == "\r\n") {
                delLen = 2;
            }
            var fPieces = _table.CapturePieces(caret, delLen);
            _history.Push(new DeleteEdit(caret, delLen, fPieces), _table, Selection);
        }
        _history.EndCompound();

        // Column positions don't change after forward delete.
        var newCarets = colSel.MaterializeCarets(_table, tabSize);
        if (newCarets.Count > 0) {
            Selection = Selection.Collapsed(newCarets[0]);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Deletes the content within the column selection rectangle on each line.
    /// Collapses the column selection to zero width at the left edge.
    /// </summary>
    public void DeleteColumnSelectionContent(int tabSize) {
        if (ColumnSel is not { } colSel) {
            return;
        }
        var sels = colSel.Materialize(_table, tabSize);
        if (sels.Count == 0) {
            return;
        }

        _history.BeginCompound();
        for (var i = sels.Count - 1; i >= 0; i--) {
            var s = sels[i];
            if (s.IsEmpty) {
                continue;
            }
            var cPieces = _table.CapturePieces(s.Start, s.Len);
            _history.Push(new DeleteEdit(s.Start, s.Len, cPieces), _table, Selection);
        }
        _history.EndCompound();

        // Collapse column selection to zero width at LeftCol.
        ColumnSel = new ColumnSelection(colSel.AnchorLine, colSel.LeftCol, colSel.ActiveLine, colSel.LeftCol);

        var newCarets = ColumnSel.Value.MaterializeCarets(_table, tabSize);
        if (newCarets.Count > 0) {
            Selection = Selection.Collapsed(newCarets[0]);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Returns the text selected on each line of the column selection,
    /// joined by the document's line ending.
    /// </summary>
    public string GetColumnSelectedText(int tabSize) {
        if (ColumnSel is not { } colSel) {
            return "";
        }
        var sels = colSel.Materialize(_table, tabSize);
        if (sels.Count == 0) {
            return "";
        }
        var nl = LineEndingInfo.Dominant switch {
            LineEnding.CRLF => "\r\n",
            LineEnding.CR => "\r",
            _ => "\n",
        };
        var sb = new StringBuilder();
        for (var i = 0; i < sels.Count; i++) {
            if (i > 0) sb.Append(nl);
            var s = sels[i];
            if (!s.IsEmpty) {
                _table.ForEachPiece(s.Start, s.Len, span => sb.Append(span));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Distributes one line from <paramref name="lines"/> across each cursor
    /// position in the column selection.  When the array length matches the
    /// rectangle's row count exactly, every caret receives its corresponding
    /// line.  When there are more lines than carets, the trailing lines are
    /// ignored.  When there are fewer lines than carets, the trailing carets
    /// are left untouched.  Exits column mode after the paste.
    /// </summary>
    public void PasteAtCursors(string[] lines, int tabSize) {
        if (ColumnSel is not { } colSel || lines.Length == 0) {
            return;
        }
        var sels = colSel.Materialize(_table, tabSize);
        if (sels.Count == 0) {
            return;
        }
        // Process at most min(carets, lines) — the symmetric "drop excess"
        // rule applies on both sides: extra lines beyond the caret count are
        // dropped, extra carets beyond the line count are left alone.
        var processCount = Math.Min(sels.Count, lines.Length);

        _history.BeginCompound();
        // Bottom-to-top within the processed range so earlier offsets stay
        // valid.  Carets at indices [processCount, sels.Count) are skipped
        // entirely — they keep their pre-paste content.
        for (var i = processCount - 1; i >= 0; i--) {
            var s = sels[i];
            var line = colSel.TopLine + i;
            var pad = ColumnSelection.PaddingNeeded(_table, line, colSel.LeftCol, tabSize);
            if (pad > 0) {
                var lineEnd = ColumnSelection.LineContentEnd(_table, line);
                PushInsert(lineEnd, new string(' ', pad));
                s = new Selection(lineEnd + pad, lineEnd + pad);
            }
            if (!s.IsEmpty) {
                var dPieces = _table.CapturePieces(s.Start, s.Len);
                _history.Push(new DeleteEdit(s.Start, s.Len, dPieces), _table, Selection);
            }
            PushInsert(s.Start, lines[i]);
        }
        _history.EndCompound();
        ColumnSel = null;
        Selection = Selection.Collapsed(Selection.Caret);
        RaiseChanged();
    }

    /// <summary>
    /// Exits column selection mode. Collapses the stream selection to the
    /// caret position of the first cursor in the column rectangle.
    /// </summary>
    public void ClearColumnSelection(int tabSize = 4) {
        if (ColumnSel is not { } colSel) {
            return;
        }
        var carets = colSel.MaterializeCarets(_table, tabSize);
        ColumnSel = null;
        if (carets.Count > 0) {
            // Place the caret at the active corner's line.
            var activeIdx = colSel.ActiveLine - colSel.TopLine;
            activeIdx = Math.Clamp(activeIdx, 0, carets.Count - 1);
            Selection = Selection.Collapsed(carets[activeIdx]);
        }
    }
}
