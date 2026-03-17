namespace DevMentalMd.Core.Documents;

/// <summary>
/// Represents a rectangular (column/block) selection defined by two corners
/// in logical-line and tab-aware-column space. The rectangle spans all lines
/// from <see cref="TopLine"/> to <see cref="BottomLine"/> and all columns
/// from <see cref="LeftCol"/> to <see cref="RightCol"/>.
/// </summary>
public readonly record struct ColumnSelection(int AnchorLine, int AnchorCol, int ActiveLine, int ActiveCol) {
    public int TopLine => Math.Min(AnchorLine, ActiveLine);
    public int BottomLine => Math.Max(AnchorLine, ActiveLine);
    public int LeftCol => Math.Min(AnchorCol, ActiveCol);
    public int RightCol => Math.Max(AnchorCol, ActiveCol);
    public int LineCount => BottomLine - TopLine + 1;

    /// <summary>Returns a new selection with the active corner moved.</summary>
    public ColumnSelection ExtendTo(int line, int col) => this with { ActiveLine = line, ActiveCol = col };

    /// <summary>Returns a new selection with <see cref="ActiveLine"/> shifted by <paramref name="delta"/>.</summary>
    public ColumnSelection ExtendLine(int delta) => this with { ActiveLine = ActiveLine + delta };

    /// <summary>Collapses to the left column edge (both Anchor and Active).</summary>
    public ColumnSelection CollapseToLeft() => this with { AnchorCol = LeftCol, ActiveCol = LeftCol };

    /// <summary>Collapses to the right column edge (both Anchor and Active).</summary>
    public ColumnSelection CollapseToRight() => this with { AnchorCol = RightCol, ActiveCol = RightCol };

    /// <summary>Shifts both AnchorCol and ActiveCol by <paramref name="delta"/>, clamped ≥ 0.</summary>
    public ColumnSelection ShiftColumns(int delta) => this with {
        AnchorCol = Math.Max(0, AnchorCol + delta),
        ActiveCol = Math.Max(0, ActiveCol + delta),
    };

    /// <summary>
    /// Shifts both AnchorLine and ActiveLine by <paramref name="delta"/>,
    /// clamped so TopLine ≥ 0 and BottomLine ≤ <paramref name="maxLine"/>.
    /// </summary>
    public ColumnSelection ShiftLines(int delta, int maxLine) {
        var clampedDelta = delta;
        if (TopLine + clampedDelta < 0) clampedDelta = -TopLine;
        if (BottomLine + clampedDelta > maxLine) clampedDelta = maxLine - BottomLine;
        return this with {
            AnchorLine = AnchorLine + clampedDelta,
            ActiveLine = ActiveLine + clampedDelta,
        };
    }

    /// <summary>Sets both AnchorCol and ActiveCol to <paramref name="col"/>.</summary>
    public ColumnSelection MoveColumnsTo(int col) => this with { AnchorCol = col, ActiveCol = col };

    // -----------------------------------------------------------------
    // Materialization: rectangle → per-line stream selections
    // -----------------------------------------------------------------

    /// <summary>
    /// Converts the column rectangle into a list of stream <see cref="Selection"/>
    /// ranges, one per logical line. Lines shorter than <see cref="LeftCol"/>
    /// produce a collapsed selection at end-of-line content.
    /// </summary>
    public IReadOnlyList<Selection> Materialize(PieceTable table, int tabSize) {
        var top = Math.Max(0, TopLine);
        var bot = Math.Min(BottomLine, (int)table.LineCount - 1);
        if (top > bot) {
            return [];
        }
        var result = new Selection[bot - top + 1];
        for (var i = top; i <= bot; i++) {
            var lineStart = table.LineStartOfs(i);
            var lineEnd = LineContentEnd(table, i);
            var lineLen = (int)(lineEnd - lineStart);
            var leftOfs = lineStart + ColToCharIdx(table, lineStart, lineLen, LeftCol, tabSize);
            var rightOfs = lineStart + ColToCharIdx(table, lineStart, lineLen, RightCol, tabSize);
            result[i - top] = new Selection(leftOfs, rightOfs);
        }
        return result;
    }

    /// <summary>
    /// Returns the caret (Active-side) character offset for each line in the rectangle.
    /// </summary>
    public IReadOnlyList<long> MaterializeCarets(PieceTable table, int tabSize) {
        var top = Math.Max(0, TopLine);
        var bot = Math.Min(BottomLine, (int)table.LineCount - 1);
        if (top > bot) {
            return [];
        }
        var result = new long[bot - top + 1];
        for (var i = top; i <= bot; i++) {
            var lineStart = table.LineStartOfs(i);
            var lineEnd = LineContentEnd(table, i);
            var lineLen = (int)(lineEnd - lineStart);
            result[i - top] = lineStart + ColToCharIdx(table, lineStart, lineLen, ActiveCol, tabSize);
        }
        return result;
    }

    // -----------------------------------------------------------------
    // Tab-aware column ↔ character-index conversion
    // -----------------------------------------------------------------

    /// <summary>
    /// Converts a tab-aware visual column to a character index within a line.
    /// Tabs expand to the next multiple of <paramref name="tabSize"/>. Returns
    /// the character index (clamped to <paramref name="lineLen"/>).
    /// </summary>
    public static int ColToCharIdx(PieceTable table, long lineStart, int lineLen, int targetCol, int tabSize) {
        if (targetCol <= 0 || lineLen == 0) {
            return 0;
        }
        var col = 0;
        for (var i = 0; i < lineLen; i++) {
            if (col >= targetCol) {
                return i;
            }
            var ch = table.CharAt(lineStart + i);
            if (ch == '\t') {
                col += tabSize - (col % tabSize);
            } else {
                col++;
            }
        }
        // Past end of line content — clamp to lineLen
        return lineLen;
    }

    /// <summary>
    /// Converts a character offset to a tab-aware visual column.
    /// </summary>
    public static int OfsToCol(PieceTable table, long ofs, int tabSize) {
        var line = (int)table.LineFromOfs(ofs);
        var lineStart = table.LineStartOfs(line);
        var charIdx = (int)(ofs - lineStart);
        var lineEnd = LineContentEnd(table, line);
        var lineLen = (int)(lineEnd - lineStart);
        var col = 0;
        var limit = Math.Min(charIdx, lineLen);
        for (var i = 0; i < limit; i++) {
            var ch = table.CharAt(lineStart + i);
            if (ch == '\t') {
                col += tabSize - (col % tabSize);
            } else {
                col++;
            }
        }
        // If charIdx > lineLen, add 1 per character beyond content (shouldn't normally happen)
        if (charIdx > lineLen) {
            col += charIdx - lineLen;
        }
        return col;
    }

    /// <summary>
    /// Returns the number of spaces needed to pad from the end of the line content
    /// to the target column. Returns 0 if the line already reaches or exceeds the column.
    /// </summary>
    public static int PaddingNeeded(PieceTable table, int line, int targetCol, int tabSize) {
        var lineStart = table.LineStartOfs(line);
        var lineEnd = LineContentEnd(table, line);
        var lineLen = (int)(lineEnd - lineStart);
        var endCol = 0;
        for (var i = 0; i < lineLen; i++) {
            var ch = table.CharAt(lineStart + i);
            if (ch == '\t') {
                endCol += tabSize - (endCol % tabSize);
            } else {
                endCol++;
            }
        }
        return Math.Max(0, targetCol - endCol);
    }

    /// <summary>
    /// Returns the tab-aware column at the end of the given line's content.
    /// </summary>
    public static int EndOfLineCol(PieceTable table, int line, int tabSize) {
        var lineEnd = LineContentEnd(table, line);
        return OfsToCol(table, lineEnd, tabSize);
    }

    /// <summary>
    /// From a starting column on a given line, finds the column of the next
    /// word boundary. <paramref name="direction"/> is −1 (left) or +1 (right).
    /// </summary>
    public static int FindWordBoundaryCol(PieceTable table, int line, int startCol, int direction, int tabSize) {
        var lineStart = table.LineStartOfs(line);
        var lineEnd = LineContentEnd(table, line);
        var lineLen = (int)(lineEnd - lineStart);
        var charIdx = ColToCharIdx(table, lineStart, lineLen, startCol, tabSize);
        if (direction < 0) {
            var i = charIdx - 1;
            while (i >= 0 && char.IsWhiteSpace(table.CharAt(lineStart + i))) i--;
            while (i >= 0 && !char.IsWhiteSpace(table.CharAt(lineStart + i))) i--;
            return OfsToCol(table, lineStart + i + 1, tabSize);
        } else {
            var i = charIdx;
            while (i < lineLen && !char.IsWhiteSpace(table.CharAt(lineStart + i))) i++;
            while (i < lineLen && char.IsWhiteSpace(table.CharAt(lineStart + i))) i++;
            return OfsToCol(table, lineStart + i, tabSize);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns the character offset of the end of line content (excluding any
    /// trailing \r\n, \n, or \r).
    /// </summary>
    internal static long LineContentEnd(PieceTable table, int line) {
        long lineEnd;
        if (line + 1 < table.LineCount) {
            lineEnd = table.LineStartOfs(line + 1);
            // Strip trailing line ending
            if (lineEnd > 0 && table.CharAt(lineEnd - 1) == '\n') {
                lineEnd--;
            }
            if (lineEnd > 0 && table.CharAt(lineEnd - 1) == '\r') {
                lineEnd--;
            }
        } else {
            lineEnd = table.Length;
        }
        return lineEnd;
    }
}
