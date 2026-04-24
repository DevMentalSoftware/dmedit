using System.Text;
using DMEdit.Core.Documents.History;

namespace DMEdit.Core.Documents;

// Line operations partial of Document.  Owns DeleteLine, MoveLineUp,
// MoveLineDown, and their private helpers GetSelectedLineRange and
// SwapLines.
public sealed partial class Document {

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
        var blockPieces = _table.CapturePieces(blockStart, blockLen);
        _history.Push(new DeleteEdit(blockStart, blockLen, blockPieces), _table, Selection);
        PushInsert(blockStart, newContent);
        _history.EndCompound();

        // Adjust selection to follow the moved lines.
        var anchorDelta = Selection.Anchor - selStart;
        var activeDelta = Selection.Active - selStart;
        Selection = new Selection(newSelStart + anchorDelta, newSelStart + activeDelta);
        RaiseChanged();
    }
}
