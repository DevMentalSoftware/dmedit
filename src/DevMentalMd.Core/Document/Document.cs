using System.Text;
using DevMentalMd.Core.Documents.History;
using DevMentalMd.Core.Printing;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// High-level document model: wraps a <see cref="PieceTable"/> with undo/redo history
/// and selection state. This is the primary type the editor UI interacts with.
/// </summary>
public sealed class Document {
    private readonly PieceTable _table;
    private readonly EditHistory _history = new();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Creates an empty document (for untitled/new documents).</summary>
    public Document() {
        _table = new PieceTable();
    }

    /// <summary>
    /// Creates a document from a string.  Used by tests; production code
    /// uses the parameterless constructor or <see cref="Document(PieceTable)"/>.
    /// </summary>
    internal Document(string initialContent) {
        _table = new PieceTable(initialContent);
        if (initialContent.Length > 0) {
            LineEndingInfo = LineEndingInfo.Detect(initialContent);
        }
    }

    /// <summary>
    /// Constructs a document wrapping an existing <see cref="PieceTable"/>.
    /// Used by <c>FileLoader</c> when loading from an <see cref="Buffers.IBuffer"/>.
    /// </summary>
    public Document(PieceTable table) {
        _table = table;
    }

    // -------------------------------------------------------------------------
    // State access
    // -------------------------------------------------------------------------

    public PieceTable Table => _table;
    public EditHistory History => _history;
    public Selection Selection { get; set; } = Selection.Collapsed(0L);

    /// <summary>
    /// When non-null, the editor is in column (block) selection mode.
    /// The rectangular selection governs editing; <see cref="Selection"/>
    /// remains set but is secondary.
    /// </summary>
    public ColumnSelection? ColumnSel { get; set; }

    /// <summary>
    /// The detected (or assigned) line ending style for this document.
    /// Defaults to the platform default for new documents.
    /// </summary>
    public LineEndingInfo LineEndingInfo { get; set; } = LineEndingInfo.PlatformDefault;

    /// <summary>
    /// Detected indentation style. Set during file loading.
    /// Defaults to spaces for new documents.
    /// </summary>
    public IndentInfo IndentInfo { get; set; } = IndentInfo.Default;

    /// <summary>
    /// Page layout settings used for printing and PDF export.
    /// Persisted per-document so the user's last-used paper size,
    /// orientation, and margins are remembered across print invocations.
    /// </summary>
    public PrintSettings PrintSettings { get; set; } = new();

    /// <summary>
    /// Detected (or user-assigned) file encoding. Determines how the document
    /// is written on the next save. Defaults to UTF-8 (no BOM) for new documents.
    /// </summary>
    public EncodingInfo EncodingInfo { get; set; } = EncodingInfo.Default;

    /// <summary>
    /// True while the backing buffer is still streaming from disk.
    /// </summary>
    public bool IsLoading => _table.Buffer is { LengthIsKnown: false };

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>
    /// Returns the currently selected text, or an empty string if selection is empty.
    /// </summary>
    /// <summary>
    /// Maximum selection size (in characters) that Copy/Cut will materialize.
    /// Selections larger than this return <c>null</c> so the caller can notify
    /// the user instead of risking an out-of-memory condition.
    /// </summary>
    public const int MaxCopyLength = 50 * 1024 * 1024; // 50 MB worth of chars

    public string? GetSelectedText() {
        if (Selection.IsEmpty) {
            return "";
        }
        long selLen = Selection.Len;
        if (selLen > MaxCopyLength) {
            return null;
        }
        var len = (int)selLen;
        if (len <= PieceTable.MaxGetTextLength) {
            return _table.GetText(Selection.Start, len);
        }
        // Large selection: build via ForEachPiece to avoid GetText guard.
        var sb = new StringBuilder(len);
        _table.ForEachPiece(Selection.Start, len, span => sb.Append(span));
        return sb.ToString();
    }

    /// <summary>
    /// True when the document's undo depth matches the last-saved position.
    /// Used to detect when undo/redo returns the document to its saved state.
    /// </summary>
    public bool IsAtSavePoint => _history.IsAtSavePoint;

    /// <summary>
    /// Records the current undo depth as the "saved" position.
    /// Call after successfully writing the document to disk.
    /// </summary>
    public void MarkSavePoint() => _history.MarkSavePoint();

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised after any mutation to the document content.</summary>
    public event EventHandler? Changed;

    private int _suppressChanged;

    /// <summary>
    /// Suppresses <see cref="Changed"/> events until the returned disposable
    /// is disposed, then fires a single event.  Calls can be nested.
    /// </summary>
    public IDisposable SuppressChangedEvents() {
        _suppressChanged++;
        return new ChangedScope(this);
    }

    private void RaiseChanged() {
        if (_suppressChanged == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ChangedScope(Document doc) : IDisposable {
        private bool _disposed;
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            doc._suppressChanged--;
            if (doc._suppressChanged == 0) doc.Changed?.Invoke(doc, EventArgs.Empty);
        }
    }

    // -------------------------------------------------------------------------
    // Edit operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="text"/> at the current caret position.
    /// If there is a non-empty selection, the selected text is replaced.
    /// </summary>
    public void Insert(string text) {
        if (text.Length == 0) {
            return;
        }
        var ofs = Selection.Start;
        var replacing = !Selection.IsEmpty;
        if (replacing) {
            _history.BeginCompound();
            DeleteRange(ofs, Selection.Len);
        }
        _history.Push(new InsertEdit(ofs, text), _table, Selection);
        if (replacing) {
            _history.EndCompound();
        }
        Selection = Selection.Collapsed(ofs + text.Length);
        RaiseChanged();
    }

    /// <summary>Deletes the current selection. No-op if selection is empty.</summary>
    public void DeleteSelection() {
        if (Selection.IsEmpty) {
            return;
        }
        DeleteRange(Selection.Start, Selection.Len);
        Selection = Selection.Collapsed(Selection.Start);
        RaiseChanged();
    }

    /// <summary>Deletes the character before the caret (like the Backspace key).</summary>
    public void DeleteBackward() {
        if (!Selection.IsEmpty) {
            DeleteSelection();
            return;
        }
        var ofs = Selection.Caret;
        if (ofs == 0L) {
            return;
        }
        // Handle \r\n as a single unit
        var delLen = 1;
        if (ofs >= 2 && _table.GetText(ofs - 2, 2) == "\r\n") {
            delLen = 2;
        }
        var delOfs = ofs - delLen;
        var pieces = _table.CapturePieces(delOfs, delLen);
        _history.Push(new DeleteEdit(delOfs, delLen, pieces), _table, Selection);
        Selection = Selection.Collapsed(delOfs);
        RaiseChanged();
    }

    /// <summary>Deletes the character after the caret (like the Delete key).</summary>
    public void DeleteForward() {
        if (!Selection.IsEmpty) {
            DeleteSelection();
            return;
        }
        var ofs = Selection.Caret;
        if (ofs >= _table.Length) {
            return;
        }
        // Handle \r\n as a single unit
        var delLen = 1;
        if (ofs + 1 < _table.Length && _table.GetText(ofs, 2) == "\r\n") {
            delLen = 2;
        }
        var pieces = _table.CapturePieces(ofs, delLen);
        _history.Push(new DeleteEdit(ofs, delLen, pieces), _table, Selection);
        RaiseChanged();
    }

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

        _history.BeginCompound();

        // Apply bottom-to-top so earlier offsets stay valid.
        for (var i = sels.Count - 1; i >= 0; i--) {
            var s = sels[i];
            var line = colSel.TopLine + i;

            // Pad short lines to reach the left column.
            var pad = ColumnSelection.PaddingNeeded(_table, line, colSel.LeftCol, tabSize);
            if (pad > 0) {
                var lineEnd = ColumnSelection.LineContentEnd(_table, line);
                var spaces = new string(' ', pad);
                _history.Push(new InsertEdit(lineEnd, spaces), _table, Selection);
                // Adjust the selection offsets for the padding we just added.
                s = new Selection(lineEnd + pad, lineEnd + pad);
            }

            // Delete the selected range, then insert.
            if (!s.IsEmpty) {
                var dPieces = _table.CapturePieces(s.Start, s.Len);
                _history.Push(new DeleteEdit(s.Start, s.Len, dPieces), _table, Selection);
            }
            _history.Push(new InsertEdit(s.Start, text), _table, Selection);
        }

        _history.EndCompound();

        // Update the column selection: all cursors advance by text.Length columns.
        var newCol = colSel.LeftCol + ColumnSelection.OfsToCol(
            _table,
            _table.LineStartOfs(colSel.TopLine) +
            ColumnSelection.ColToCharIdx(_table, _table.LineStartOfs(colSel.TopLine),
                (int)(ColumnSelection.LineContentEnd(_table, colSel.TopLine) - _table.LineStartOfs(colSel.TopLine)),
                colSel.LeftCol + text.Length, tabSize),
            tabSize);
        // Simpler: the new column is just LeftCol + length of inserted text (char count).
        newCol = colSel.LeftCol + text.Length;
        ColumnSel = new ColumnSelection(colSel.AnchorLine, newCol, colSel.ActiveLine, newCol);

        // Also update stream selection to first cursor position.
        var firstCaret = ColumnSel.Value.MaterializeCarets(_table, tabSize);
        if (firstCaret.Count > 0) {
            Selection = Selection.Collapsed(firstCaret[0]);
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
    /// Pastes one line from <paramref name="lines"/> at each cursor position
    /// in the column selection. The array length must equal the column
    /// selection's <see cref="ColumnSelection.LineCount"/>. Exits column mode.
    /// </summary>
    public void PasteAtCursors(string[] lines, int tabSize) {
        if (ColumnSel is not { } colSel || lines.Length != colSel.LineCount) {
            return;
        }
        var sels = colSel.Materialize(_table, tabSize);
        _history.BeginCompound();
        for (var i = sels.Count - 1; i >= 0; i--) {
            var s = sels[i];
            var line = colSel.TopLine + i;
            var pad = ColumnSelection.PaddingNeeded(_table, line, colSel.LeftCol, tabSize);
            if (pad > 0) {
                var lineEnd = ColumnSelection.LineContentEnd(_table, line);
                _history.Push(new InsertEdit(lineEnd, new string(' ', pad)), _table, Selection);
                s = new Selection(lineEnd + pad, lineEnd + pad);
            }
            if (!s.IsEmpty) {
                var dPieces = _table.CapturePieces(s.Start, s.Len);
                _history.Push(new DeleteEdit(s.Start, s.Len, dPieces), _table, Selection);
            }
            _history.Push(new InsertEdit(s.Start, lines[i]), _table, Selection);
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

    // -------------------------------------------------------------------------
    // Line operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Deletes the entire logical line containing the caret.
    /// Includes the trailing newline, or the preceding newline if this is the last line.
    /// </summary>
    public void DeleteLine() {
        var caret = Selection.Caret;
        var lineIdx = _table.LineFromOfs(caret);
        var lineCount = _table.LineCount;
        var lineStart = _table.LineStartOfs(lineIdx);

        long deleteStart, deleteEnd;

        if (lineIdx + 1 < lineCount) {
            // Not the last line: delete from lineStart to start of next line
            deleteStart = lineStart;
            deleteEnd = _table.LineStartOfs(lineIdx + 1);
        } else {
            // Last line: delete to end of document
            deleteStart = lineStart;
            deleteEnd = _table.Length;
            // Also eat the preceding newline so no trailing blank remains
            if (lineIdx > 0 && deleteStart > 0) {
                deleteStart--;
                if (deleteStart > 0 && _table.GetText(deleteStart - 1, 1)[0] == '\r') {
                    deleteStart--;
                }
            }
        }

        var len = deleteEnd - deleteStart;
        if (len == 0) {
            return;
        }

        var dlPieces = _table.CapturePieces(deleteStart, len);
        _history.Push(new DeleteEdit(deleteStart, len, dlPieces), _table, Selection);
        Selection = Selection.Collapsed(Math.Min(deleteStart, _table.Length));
        RaiseChanged();
    }

    /// <summary>
    /// Moves the line(s) covered by the current selection up by one logical line.
    /// No-op if the first selected line is already line 0.
    /// </summary>
    public void MoveLineUp() {
        var (firstLine, lastLine) = GetSelectedLineRange();
        if (firstLine <= 0) {
            return;
        }
        SwapLines(firstLine - 1, firstLine, lastLine, -1);
    }

    /// <summary>
    /// Moves the line(s) covered by the current selection down by one logical line.
    /// No-op if the last selected line is already the final line.
    /// </summary>
    public void MoveLineDown() {
        var (firstLine, lastLine) = GetSelectedLineRange();
        if (lastLine >= _table.LineCount - 1) {
            return;
        }
        SwapLines(lastLine + 1, firstLine, lastLine, +1);
    }

    // -------------------------------------------------------------------------
    // Word / selection operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Expands the current selection outward to alphanumeric boundaries within
    /// the current line. No-op if the selection already contains a
    /// non-alphanumeric character or spans multiple lines. When collapsed
    /// (caret only), expands around the caret position.
    /// </summary>
    public void SelectWord() {
        var len = _table.Length;
        if (len == 0) {
            return;
        }

        var (lineStart, lineEnd) = GetLineContentRange(Selection.Start);

        // If selection spans multiple lines, treat as containing non-word chars → no-op.
        if (!Selection.IsEmpty && Selection.End > lineEnd) {
            return;
        }

        // Work in a bounded window around the selection so we don't
        // materialize an entire line (could be multi-GB for single-line files).
        const int windowRadius = 1024;
        var winStart = Math.Max(lineStart, Selection.Start - windowRadius);
        var winEnd = Math.Min(lineEnd, Selection.End + windowRadius);
        var winLen = (int)(winEnd - winStart);
        if (winLen == 0) {
            return;
        }
        var winText = _table.GetText(winStart, winLen);

        var selStartInWin = (int)(Selection.Start - winStart);
        var selEndInWin = (int)(Selection.End - winStart);

        // If selection contains a non-word character → no-op.
        for (var i = selStartInWin; i < selEndInWin; i++) {
            if (!IsWordChar(winText[i])) {
                return;
            }
        }

        // Expand backward from selection start to non-word or window start.
        var left = selStartInWin;
        while (left > 0 && IsWordChar(winText[left - 1])) {
            left--;
        }

        // Expand forward from selection end to non-word or window end.
        var right = selEndInWin;
        while (right < winLen && IsWordChar(winText[right])) {
            right++;
        }

        Selection = new Selection(winStart + left, winStart + right);
    }

    /// <summary>
    /// Selects the content of the line containing the caret (excluding the
    /// line ending). Used for triple-click.
    /// </summary>
    public void SelectLine() {
        if (_table.Length == 0) {
            return;
        }
        var (lineStart, lineEnd) = GetLineContentRange(Selection.Caret);
        Selection = new Selection(lineStart, lineEnd);
    }

    /// <summary>
    /// Expands the selection outward through progressively broader levels.
    /// The levels depend on the <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="ExpandSelectionMode.SubwordFirst"/>: subword → whitespace → line → document</item>
    ///   <item><see cref="ExpandSelectionMode.Word"/>: whitespace → line → document</item>
    /// </list>
    /// Each invocation detects the current level by inspecting selection boundaries
    /// and advances to the next level.
    /// </summary>
    public void ExpandSelection(ExpandSelectionMode mode) {
        var len = _table.Length;
        if (len == 0) {
            return;
        }

        // Already the entire document?
        if (Selection.Start == 0 && Selection.End == len) {
            return;
        }

        var (lineStart, lineEnd) = GetLineContentRange(Selection.Start);

        // If selection spans beyond this line → expand to entire document.
        if (!Selection.IsEmpty && Selection.End > lineEnd) {
            Selection = new Selection(0L, len);
            return;
        }

        // Already the entire line?
        if (Selection.Start == lineStart && Selection.End == lineEnd) {
            Selection = new Selection(0L, len);
            return;
        }

        // Use a bounded window to avoid materializing a multi-GB single line.
        const int windowRadius = 1024;
        var winStart = Math.Max(lineStart, Selection.Start - windowRadius);
        var winEnd = Math.Min(lineEnd, Selection.End + windowRadius);
        var winLen = (int)(winEnd - winStart);
        var winText = winLen > 0 ? _table.GetText(winStart, winLen) : "";
        var selStartInWin = (int)(Selection.Start - winStart);
        var selEndInWin = (int)(Selection.End - winStart);

        // Compute whitespace-bounded range.
        var wsLeft = selStartInWin;
        while (wsLeft > 0 && !char.IsWhiteSpace(winText[wsLeft - 1])) {
            wsLeft--;
        }
        var wsRight = selEndInWin;
        while (wsRight < winLen && !char.IsWhiteSpace(winText[wsRight])) {
            wsRight++;
        }

        var atWhitespaceBoundary = selStartInWin == wsLeft && selEndInWin == wsRight;

        if (mode == ExpandSelectionMode.SubwordFirst && !atWhitespaceBoundary) {
            // Try subword expansion, constrained within the whitespace-bounded word.
            var subLeft = selStartInWin;
            if (subLeft > wsLeft) {
                subLeft--;
                while (subLeft > wsLeft && !IsSubwordBoundary(winText, subLeft)) {
                    subLeft--;
                }
            }
            var subRight = selEndInWin;
            if (subRight < wsRight) {
                subRight++;
                while (subRight < wsRight && !IsSubwordBoundary(winText, subRight)) {
                    subRight++;
                }
            }

            var expanded = subLeft != selStartInWin || subRight != selEndInWin;
            var alreadyAtWhitespace = subLeft == wsLeft && subRight == wsRight;
            if (expanded && !alreadyAtWhitespace) {
                Selection = new Selection(winStart + subLeft, winStart + subRight);
                return;
            }
        }

        // Whitespace boundary level.
        if (!atWhitespaceBoundary) {
            Selection = new Selection(winStart + wsLeft, winStart + wsRight);
            return;
        }

        // Line level.
        Selection = new Selection(lineStart, lineEnd);
    }

    /// <summary>
    /// Returns (lineStart, lineEnd) where lineEnd is the offset of the first
    /// line-ending character (or document length). This gives the "content"
    /// range of the line excluding \r\n / \n / \r.
    /// </summary>
    private (long lineStart, long lineEnd) GetLineContentRange(long ofs) {
        var line = _table.LineFromOfs(Math.Min(ofs, _table.Length));
        var lineStart = _table.LineStartOfs(line);
        long lineEnd;
        if (line + 1 < _table.LineCount) {
            var nextLineStart = _table.LineStartOfs(line + 1);
            // Strip trailing \r\n, \n, or \r from the end.
            lineEnd = nextLineStart;
            if (lineEnd > lineStart) {
                var tail = _table.GetText(Math.Max(lineStart, lineEnd - 2), (int)Math.Min(2, lineEnd - lineStart));
                if (tail.EndsWith("\r\n")) {
                    lineEnd -= 2;
                } else if (tail.EndsWith("\n") || tail.EndsWith("\r")) {
                    lineEnd -= 1;
                }
            }
        } else {
            lineEnd = _table.Length;
        }
        return (lineStart, lineEnd);
    }

    /// <summary>
    /// Returns true if position <paramref name="i"/> in the text is a subword
    /// boundary: camelCase transitions, underscore, digit/letter transitions,
    /// or non-alphanumeric characters.
    /// </summary>
    private static bool IsSubwordBoundary(string text, int i) {
        if (i <= 0 || i >= text.Length) {
            return false;
        }
        var prev = text[i - 1];
        var curr = text[i];

        // Non-alphanumeric on either side is always a boundary.
        if (!char.IsLetterOrDigit(prev) || !char.IsLetterOrDigit(curr)) {
            return true;
        }
        // lowercase → Uppercase  (e.g. "camelCase" → boundary before 'C')
        if (char.IsLower(prev) && char.IsUpper(curr)) {
            return true;
        }
        // Uppercase → Uppercase+lowercase  (e.g. "HTMLParser" → boundary before 'P')
        if (char.IsUpper(prev) && char.IsUpper(curr) && i + 1 < text.Length && char.IsLower(text[i + 1])) {
            return true;
        }
        // digit ↔ letter transition
        if (char.IsDigit(prev) != char.IsDigit(curr)) {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Transforms the case of the selected text. No-op if selection is empty.
    /// Uses a compound edit so undo reverts in one step.
    /// </summary>
    public void TransformCase(CaseTransform transform) {
        if (Selection.IsEmpty) {
            return;
        }
        var start = Selection.Start;
        var selLen = (int)Selection.Len;
        var sb = new StringBuilder(selLen);
        _table.ForEachPiece(start, selLen, span => sb.Append(span));
        var original = sb.ToString();

        var transformed = transform switch {
            CaseTransform.Upper => original.ToUpperInvariant(),
            CaseTransform.Lower => original.ToLowerInvariant(),
            CaseTransform.Proper => ToProperCase(original),
            _ => original,
        };

        if (transformed == original) {
            return;
        }

        _history.BeginCompound();
        DeleteRange(start, selLen);
        _history.Push(new InsertEdit(start, transformed), _table, Selection);
        _history.EndCompound();

        // Preserve selection over the transformed text
        Selection = new Selection(start, start + transformed.Length);
        RaiseChanged();
    }

    // -------------------------------------------------------------------------
    // Bulk replace
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uniform bulk replace: all matches have the same length and the same
    /// replacement string.  Single undo entry, one line tree rebuild.
    /// </summary>
    public int BulkReplaceUniform(long[] matchPositions, int matchLen, string replacement) {
        if (matchPositions.Length == 0) return 0;

        var savedPieces = _table.SnapshotPieces();
        var savedLines = _table.SnapshotLineLengths();
        var savedAddLen = _table.AddBufferLength;

        var edit = new UniformBulkReplaceEdit(
            matchPositions, matchLen, replacement,
            savedPieces, savedLines, savedAddLen);
        _history.Push(edit, _table, Selection);

        Selection = Selection.Collapsed(Math.Min(Selection.Caret, _table.Length));
        RaiseChanged();
        return matchPositions.Length;
    }

    /// <summary>
    /// Varying bulk replace: matches have different lengths and/or different
    /// replacements (e.g. regex replace, indentation conversion).
    /// Single undo entry, one line tree rebuild.
    /// </summary>
    public int BulkReplaceVarying((long Pos, int Len)[] matches, string[] replacements) {
        if (matches.Length == 0) return 0;

        var savedPieces = _table.SnapshotPieces();
        var savedLines = _table.SnapshotLineLengths();
        var savedAddLen = _table.AddBufferLength;

        var edit = new VaryingBulkReplaceEdit(
            matches, replacements,
            savedPieces, savedLines, savedAddLen);
        _history.Push(edit, _table, Selection);

        Selection = Selection.Collapsed(Math.Min(Selection.Caret, _table.Length));
        RaiseChanged();
        return matches.Length;
    }

    // -------------------------------------------------------------------------
    // Line ending conversion
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts all line endings in the document to the specified style.
    /// Replaces the entire document content in a single compound edit.
    /// </summary>
    /// <summary>
    /// Sets the document's line ending style. No physical edit is performed —
    /// the actual conversion happens at save time in <see cref="IO.FileSaver"/>.
    /// New lines typed by the user already use <see cref="LineEndingInfo.NewlineString"/>.
    /// </summary>
    public void ConvertLineEndings(LineEnding target) {
        LineEndingInfo = new LineEndingInfo(target, false);
    }

    /// <summary>
    /// Converts all leading indentation in the document between tabs and spaces.
    /// Uses bulk replace — no full-document string materialization.
    /// </summary>
    public void ConvertIndentation(IndentStyle target, int tabSize = 4) {
        var spacesStr = new string(' ', tabSize);

        // Phase 1: walk the document to find indent regions that need changing.
        var matches = new List<(long Pos, int Len)>();
        var replacements = new List<string>();
        var leadingBuf = new StringBuilder();
        var atLineStart = true;
        var indentStart = 0L;
        var docPos = 0L;

        void FlushLeading() {
            if (leadingBuf.Length == 0) return;
            var before = leadingBuf.ToString();
            string after;
            if (target == IndentStyle.Spaces) {
                var sb = new StringBuilder(before.Length * tabSize);
                foreach (var c in before) {
                    if (c == '\t') { sb.Append(spacesStr); }
                    else { sb.Append(c); }
                }
                after = sb.ToString();
            } else {
                var expandedSpaces = 0;
                foreach (var c in before) {
                    if (c == '\t') { expandedSpaces += tabSize; }
                    else { expandedSpaces++; }
                }
                var wholeTabs = expandedSpaces / tabSize;
                var remainSpaces = expandedSpaces % tabSize;
                after = new string('\t', wholeTabs) + new string(' ', remainSpaces);
            }
            if (after != before) {
                matches.Add((indentStart, before.Length));
                replacements.Add(after);
            }
            leadingBuf.Clear();
        }

        _table.ForEachPiece(0, _table.Length, span => {
            foreach (var ch in span) {
                if (atLineStart && (ch == ' ' || ch == '\t')) {
                    if (leadingBuf.Length == 0) {
                        indentStart = docPos;
                    }
                    leadingBuf.Append(ch);
                    docPos++;
                    continue;
                }
                if (atLineStart) {
                    FlushLeading();
                    atLineStart = false;
                }
                docPos++;
                if (ch == '\n' || ch == '\r') {
                    atLineStart = true;
                }
            }
        });
        // Flush any trailing leading whitespace (file ends with whitespace-only line).
        if (atLineStart) FlushLeading();

        if (matches.Count == 0) {
            IndentInfo = new IndentInfo(target, false);
            return;
        }

        BulkReplaceVarying(matches.ToArray(), replacements.ToArray());
        IndentInfo = new IndentInfo(target, false);
    }

    // -------------------------------------------------------------------------
    // Undo / Redo
    // -------------------------------------------------------------------------

    /// <summary>
    /// Undoes the most recent edit. Returns the edit that was reverted,
    /// or <c>null</c> if nothing to undo.
    /// </summary>
    public IDocumentEdit? Undo() {
        var result = _history.Undo(_table);
        if (result is null) {
            return null;
        }
        Selection = result.Value.SelectionBefore;
        RaiseChanged();
        return result.Value.Edit;
    }

    /// <summary>
    /// Re-applies the most recently undone edit. Returns the edit that
    /// was applied, or <c>null</c> if nothing to redo.
    /// </summary>
    public IDocumentEdit? Redo() {
        var result = _history.Redo(_table);
        if (result is null) {
            return null;
        }
        Selection = Selection.Collapsed(CaretAfterRedo(result.Value.Edit));
        RaiseChanged();
        return result.Value.Edit;
    }

    /// <summary>
    /// Computes the caret position after applying <paramref name="edit"/>.
    /// </summary>
    private static long CaretAfterRedo(IDocumentEdit edit) => edit switch {
        // Insert applied → caret at end of inserted text.
        InsertEdit ins => ins.Ofs + ins.Text.Length,
        // Delete applied → caret at deletion point.
        DeleteEdit del => del.Ofs,
        // Compound applies in forward order → last apply is the last edit.
        CompoundEdit comp => CaretAfterRedo(comp.Edits[^1]),
        // Bulk replace → caret at start of document (no single obvious position).
        UniformBulkReplaceEdit => 0L,
        VaryingBulkReplaceEdit => 0L,
        _ => 0L
    };

    // -------------------------------------------------------------------------
    // Compound edit grouping (exposed for the editor to batch keystrokes)
    // -------------------------------------------------------------------------

    public void BeginCompound() => _history.BeginCompound();
    public void EndCompound() => _history.EndCompound();

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void DeleteRange(long ofs, long len) {
        var pieces = _table.CapturePieces(ofs, len);
        var lineInfo = _table.CaptureLineInfo(ofs, len);
        var edit = lineInfo is var (sl, ll)
            ? new DeleteEdit(ofs, len, pieces, sl, ll)
            : new DeleteEdit(ofs, len, pieces);
        _history.Push(edit, _table, Selection);
    }

    /// <summary>
    /// Returns the (firstLine, lastLine) range of logical lines covered by the selection.
    /// If selection ends at column 0 of a line, that line is excluded.
    /// </summary>
    private (long firstLine, long lastLine) GetSelectedLineRange() {
        var startLine = _table.LineFromOfs(Selection.Start);
        var endLine = _table.LineFromOfs(Math.Max(Selection.Start, Selection.End - 1));
        if (!Selection.IsEmpty
            && Selection.End == _table.LineStartOfs(endLine)
            && endLine > startLine) {
            endLine--;
        }
        return (startLine, endLine);
    }

    /// <summary>
    /// Swaps the adjacent line with the selected line range.
    /// </summary>
    private void SwapLines(long adjacentLine, long firstLine, long lastLine, int direction) {
        var lineCount = _table.LineCount;

        var adjStart = _table.LineStartOfs(adjacentLine);
        var adjEnd = adjacentLine + 1 < lineCount
            ? _table.LineStartOfs(adjacentLine + 1)
            : _table.Length;
        var adjSb = new StringBuilder((int)(adjEnd - adjStart));
        _table.ForEachPiece(adjStart, adjEnd - adjStart, span => adjSb.Append(span));
        var adjText = adjSb.ToString();

        var selStart = _table.LineStartOfs(firstLine);
        var selEnd = lastLine + 1 < lineCount
            ? _table.LineStartOfs(lastLine + 1)
            : _table.Length;
        var selSb = new StringBuilder((int)(selEnd - selStart));
        _table.ForEachPiece(selStart, selEnd - selStart, span => selSb.Append(span));
        var selText = selSb.ToString();

        // Handle missing trailing newline on the last document line
        var selHasNl = selText.Length > 0 && selText[^1] == '\n';
        var adjHasNl = adjText.Length > 0 && adjText[^1] == '\n';

        if (!adjHasNl && selHasNl) {
            var nlLen = selText.Length >= 2 && selText[^2] == '\r' ? 2 : 1;
            adjText += selText[^nlLen..];
            selText = selText[..^nlLen];
        } else if (adjHasNl && !selHasNl) {
            var nlLen = adjText.Length >= 2 && adjText[^2] == '\r' ? 2 : 1;
            selText += adjText[^nlLen..];
            adjText = adjText[..^nlLen];
        }

        var blockStart = Math.Min(adjStart, selStart);
        var blockEnd = Math.Max(adjEnd, selEnd);
        var blockLen = (int)(blockEnd - blockStart);
        var blockSb = new StringBuilder(blockLen);
        _table.ForEachPiece(blockStart, blockLen, span => blockSb.Append(span));
        var blockDeleted = blockSb.ToString();

        string newContent;
        long newSelStart;
        if (direction < 0) {
            newContent = selText + adjText;
            newSelStart = blockStart;
        } else {
            newContent = adjText + selText;
            newSelStart = blockStart + adjText.Length;
        }

        _history.BeginCompound();
        _history.Push(new DeleteEdit(blockStart, blockDeleted), _table, Selection);
        _history.Push(new InsertEdit(blockStart, newContent), _table, Selection);
        _history.EndCompound();

        // Adjust selection to follow the moved lines
        var anchorDelta = Selection.Anchor - selStart;
        var activeDelta = Selection.Active - selStart;
        Selection = new Selection(newSelStart + anchorDelta, newSelStart + activeDelta);
        RaiseChanged();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ToProperCase(string text) {
        var chars = text.ToCharArray();
        var newWord = true;
        for (var i = 0; i < chars.Length; i++) {
            if (char.IsWhiteSpace(chars[i]) || chars[i] == '-' || chars[i] == '_') {
                newWord = true;
            } else if (newWord) {
                chars[i] = char.ToUpperInvariant(chars[i]);
                newWord = false;
            } else {
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
        }
        return new string(chars);
    }
}
