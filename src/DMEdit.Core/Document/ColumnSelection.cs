namespace DMEdit.Core.Documents;

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
    /// <remarks>
    /// Hot path for column-mode editing: this is called per cursor for every
    /// call to <see cref="MaterializeCarets"/>, which itself runs many times
    /// per insert via <see cref="DMEdit.Core.Documents.Document.InsertAtCursors"/>.
    /// Tab-free lines (the overwhelming common case) take a fast path that
    /// scans the line for a tab character via SIMD-friendly span IndexOf,
    /// then returns immediately when none is found — col equals char index
    /// directly.  Tab-bearing lines fall through to the character-stepping
    /// loop starting from the first tab position so we don't waste work on
    /// the tab-free prefix.
    /// </remarks>
    public static int ColToCharIdx(PieceTable table, long lineStart, int lineLen, int targetCol, int tabSize) {
        if (targetCol <= 0 || lineLen == 0) {
            return 0;
        }

        // Tab-free fast path: walk the line as native spans and look for
        // the first tab character.  If none is found, the column is the
        // character index (clamped) directly — no per-char CharAt calls.
        // If a tab is found, we know the prefix is tab-free so we can
        // start the slow per-char loop from the tab's position.
        var firstTabIdx = FindFirstTab(table, lineStart, lineLen);

        if (firstTabIdx < 0) {
            // No tabs anywhere on the line — col equals char index.
            return SnapCharIdxToBoundary(table, lineStart, Math.Min(targetCol, lineLen));
        }

        // The first `firstTabIdx` characters are all non-tab, so the column
        // count up to that position is exactly firstTabIdx.  If the target
        // column is reached before the first tab, return immediately.
        if (targetCol <= firstTabIdx) {
            return SnapCharIdxToBoundary(table, lineStart, targetCol);
        }

        // Tab-bearing tail: walk char by char from the first tab.  This is
        // the original slow path, but bounded to the tail rather than the
        // entire line.
        var col = firstTabIdx;
        for (var i = firstTabIdx; i < lineLen; i++) {
            if (col >= targetCol) {
                return SnapCharIdxToBoundary(table, lineStart, i);
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
    /// If <paramref name="charIdx"/> relative to <paramref name="lineStart"/>
    /// lands on the low half of a surrogate pair, snaps it forward to the
    /// position after the pair so the caller never hands a mid-pair offset
    /// to an edit operation.
    /// </summary>
    private static int SnapCharIdxToBoundary(PieceTable table, long lineStart, int charIdx) {
        if (charIdx <= 0) return charIdx;
        var snapped = CodepointBoundary.SnapToBoundary(table, lineStart + charIdx, forward: true);
        return (int)(snapped - lineStart);
    }

    /// <summary>
    /// Converts a character offset to a tab-aware visual column.  If
    /// <paramref name="ofs"/> lands in the middle of a surrogate pair, it
    /// is first snapped backward to the start of the pair — callers never
    /// get a column value that corresponds to a half code point.
    /// </summary>
    public static int OfsToCol(PieceTable table, long ofs, int tabSize) {
        ofs = CodepointBoundary.SnapToBoundary(table, ofs, forward: false);
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
    /// Returns the character index (relative to <paramref name="lineStart"/>)
    /// of the first <c>\t</c> character within the first <paramref name="length"/>
    /// characters of the line, or -1 if none exists.  Walks pieces via
    /// <see cref="PieceTable.ForEachPiece"/> so each piece's text is scanned
    /// with a single SIMD-accelerated <c>IndexOf('\t')</c>.
    /// </summary>
    private static int FindFirstTab(PieceTable table, long lineStart, int length) {
        if (length <= 0) return -1;
        var firstTabIdx = -1;
        var scanned = 0;
        table.ForEachPiece(lineStart, length, span => {
            if (firstTabIdx >= 0) return; // already found
            var idx = span.IndexOf('\t');
            if (idx >= 0) {
                firstTabIdx = scanned + idx;
            }
            scanned += span.Length;
        });
        return firstTabIdx;
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

    /// <summary>
    /// Returns the column reached by moving one character left or right from
    /// <paramref name="currentCol"/> on <paramref name="line"/>. Tabs are
    /// treated as a single character so the caret jumps across the full tab
    /// width. In virtual space (past end of content) the shift is always 1.
    /// </summary>
    public static int NextCharCol(PieceTable table, int line, int currentCol, int direction, int tabSize) {
        var lineStart = table.LineStartOfs(line);
        var lineEnd = LineContentEnd(table, line);
        var lineLen = (int)(lineEnd - lineStart);
        var endCol = EndOfLineCol(table, line, tabSize);

        // In virtual space — shift by 1 column.
        if (currentCol >= endCol) {
            return Math.Max(0, currentCol + direction);
        }

        var charIdx = ColToCharIdx(table, lineStart, lineLen, currentCol, tabSize);
        // Step by a whole code point so an emoji under the caret moves as
        // one unit — direction=+1 over a surrogate pair advances by 2.
        var newCharIdxLong = direction < 0
            ? CodepointBoundary.StepLeft(table, lineStart + charIdx) - lineStart
            : CodepointBoundary.StepRight(table, lineStart + charIdx) - lineStart;
        var newCharIdx = (int)newCharIdxLong;
        if (newCharIdx < 0) return 0;
        if (newCharIdx >= lineLen) return endCol;
        return OfsToCol(table, lineStart + newCharIdx, tabSize);
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
