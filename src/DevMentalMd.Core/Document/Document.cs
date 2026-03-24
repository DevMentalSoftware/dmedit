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

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>
    /// Returns the currently selected text, or an empty string if selection is empty.
    /// </summary>
    public string GetSelectedText() {
        if (Selection.IsEmpty) {
            return "";
        }
        return _table.GetText(Selection.Start, (int)Selection.Len);
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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the current selection. No-op if selection is empty.</summary>
    public void DeleteSelection() {
        if (Selection.IsEmpty) {
            return;
        }
        DeleteRange(Selection.Start, Selection.Len);
        Selection = Selection.Collapsed(Selection.Start);
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        var parts = new string[sels.Count];
        for (var i = 0; i < sels.Count; i++) {
            var s = sels[i];
            parts[i] = s.IsEmpty ? "" : _table.GetText(s.Start, (int)s.Len);
        }
        return string.Join(nl, parts);
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
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
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

        // Get the line text (content only, no line ending).
        var lineLen = (int)(lineEnd - lineStart);
        if (lineLen == 0) {
            return;
        }
        var lineText = _table.GetText(lineStart, lineLen);

        var selStartInLine = (int)(Selection.Start - lineStart);
        var selEndInLine = (int)(Selection.End - lineStart);

        // If selection contains a non-word character → no-op.
        for (var i = selStartInLine; i < selEndInLine; i++) {
            if (!IsWordChar(lineText[i])) {
                return;
            }
        }

        // Expand backward from selection start to non-word or line start.
        var left = selStartInLine;
        while (left > 0 && IsWordChar(lineText[left - 1])) {
            left--;
        }

        // Expand forward from selection end to non-word or line end.
        var right = selEndInLine;
        while (right < lineLen && IsWordChar(lineText[right])) {
            right++;
        }

        Selection = new Selection(lineStart + left, lineStart + right);
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

        var lineLen = (int)(lineEnd - lineStart);
        var lineText = lineLen > 0 ? _table.GetText(lineStart, lineLen) : "";
        var selStartInLine = (int)(Selection.Start - lineStart);
        var selEndInLine = (int)(Selection.End - lineStart);

        // Compute whitespace-bounded range.
        var wsLeft = selStartInLine;
        while (wsLeft > 0 && !char.IsWhiteSpace(lineText[wsLeft - 1])) {
            wsLeft--;
        }
        var wsRight = selEndInLine;
        while (wsRight < lineLen && !char.IsWhiteSpace(lineText[wsRight])) {
            wsRight++;
        }

        var atWhitespaceBoundary = selStartInLine == wsLeft && selEndInLine == wsRight;

        if (mode == ExpandSelectionMode.SubwordFirst && !atWhitespaceBoundary) {
            // Try subword expansion, constrained within the whitespace-bounded word.
            var subLeft = selStartInLine;
            if (subLeft > wsLeft) {
                subLeft--;
                while (subLeft > wsLeft && !IsSubwordBoundary(lineText, subLeft)) {
                    subLeft--;
                }
            }
            var subRight = selEndInLine;
            if (subRight < wsRight) {
                subRight++;
                while (subRight < wsRight && !IsSubwordBoundary(lineText, subRight)) {
                    subRight++;
                }
            }

            var expanded = subLeft != selStartInLine || subRight != selEndInLine;
            var alreadyAtWhitespace = subLeft == wsLeft && subRight == wsRight;
            if (expanded && !alreadyAtWhitespace) {
                Selection = new Selection(lineStart + subLeft, lineStart + subRight);
                return;
            }
        }

        // Whitespace boundary level.
        if (!atWhitespaceBoundary) {
            Selection = new Selection(lineStart + wsLeft, lineStart + wsRight);
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
        var original = _table.GetText(start, selLen);

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
        Changed?.Invoke(this, EventArgs.Empty);
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
    /// Replaces the entire document content in a single compound edit.
    /// </summary>
    public void ConvertIndentation(IndentStyle target, int tabSize = 4) {
        var sb = new System.Text.StringBuilder((int)Math.Min(_table.Length * 2, int.MaxValue));
        var spacesStr = new string(' ', tabSize);
        var atLineStart = true;
        var leadingBuf = new System.Text.StringBuilder();
        var changed = false;

        void FlushLeading() {
            if (leadingBuf.Length == 0) return;
            var before = leadingBuf.ToString();
            if (target == IndentStyle.Spaces) {
                foreach (var c in before) {
                    if (c == '\t') { sb.Append(spacesStr); changed = true; }
                    else sb.Append(c);
                }
            } else {
                var expandedSpaces = 0;
                foreach (var c in before) {
                    if (c == '\t') expandedSpaces += tabSize;
                    else expandedSpaces++;
                }
                var wholeTabs = expandedSpaces / tabSize;
                var remainSpaces = expandedSpaces % tabSize;
                sb.Append('\t', wholeTabs);
                sb.Append(' ', remainSpaces);
                if (sb.Length > 0 && before != sb.ToString()[(sb.Length - wholeTabs - remainSpaces)..])
                    changed = true;
            }
            leadingBuf.Clear();
        }

        _table.ForEachPiece(0, _table.Length, span => {
            foreach (var ch in span) {
                if (atLineStart && (ch == ' ' || ch == '\t')) {
                    leadingBuf.Append(ch);
                    continue;
                }
                if (atLineStart) {
                    FlushLeading();
                    atLineStart = false;
                }
                sb.Append(ch);
                if (ch == '\n' || ch == '\r') {
                    atLineStart = true;
                }
            }
        });
        // Flush any trailing leading whitespace (file ends with whitespace-only line).
        if (atLineStart) FlushLeading();

        if (!changed) {
            IndentInfo = new IndentInfo(target, false);
            return;
        }

        var result = sb.ToString();
        var originalLen = _table.Length;
        var pieces = _table.CapturePieces(0, originalLen);
        var lineInfo = _table.CaptureLineInfo(0, originalLen);
        var savedCaret = Selection.Caret;

        _history.BeginCompound();
        var deleteEdit = lineInfo is var (sl, ll)
            ? new DeleteEdit(0, originalLen, pieces, sl, ll)
            : new DeleteEdit(0, originalLen, pieces);
        _history.Push(deleteEdit, _table, Selection);
        _history.Push(new InsertEdit(0, result), _table, Selection);
        _history.EndCompound();

        Selection = Selection.Collapsed(Math.Min(savedCaret, _table.Length));
        IndentInfo = new IndentInfo(target, false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------------------
    // Undo / Redo
    // -------------------------------------------------------------------------

    public void Undo() {
        var result = _history.Undo(_table);
        if (result is null) {
            return;
        }
        Selection = result.Value.SelectionBefore;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo() {
        var result = _history.Redo(_table);
        if (result is null) {
            return;
        }
        Selection = Selection.Collapsed(CaretAfterRedo(result.Value.Edit));
        Changed?.Invoke(this, EventArgs.Empty);
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
        var adjText = _table.GetText(adjStart, (int)(adjEnd - adjStart));

        var selStart = _table.LineStartOfs(firstLine);
        var selEnd = lastLine + 1 < lineCount
            ? _table.LineStartOfs(lastLine + 1)
            : _table.Length;
        var selText = _table.GetText(selStart, (int)(selEnd - selStart));

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
        var blockDeleted = _table.GetText(blockStart, blockLen);

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
        Changed?.Invoke(this, EventArgs.Empty);
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
