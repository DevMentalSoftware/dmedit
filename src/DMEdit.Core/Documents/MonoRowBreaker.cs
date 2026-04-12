namespace DMEdit.Core.Documents;

/// <summary>
/// Shared word-break row-break helper used by every monospace plain-text
/// paginator/renderer in the codebase.  Previously duplicated in
/// <c>MonoLineLayout.NextRow</c> (DMEdit.Rendering) and
/// <c>WpfPrintService.PlainTextPaginator.NextRow</c> (DMEdit.Windows) as
/// comment-documented mirrors — every fix to one had to be manually mirrored
/// to the other.  Hoisting to Core guarantees pagination and on-screen/print
/// wrapping stay byte-identical.
/// </summary>
public static class MonoRowBreaker {
    /// <summary>
    /// Computes the next row break for a monospace word-wrap layout.
    ///
    /// <para>Rules:</para>
    /// <list type="bullet">
    ///   <item>If the remaining content fits in <paramref name="charsPerRow"/>,
    ///     the remainder is one row and <c>NextStart == line.Length</c>.</item>
    ///   <item>Otherwise, scan backward from the hard limit for a space; if
    ///     found, break at the last space — the space itself is dropped from
    ///     the drawn row (<c>DrawLen</c> excludes it) and <c>NextStart</c>
    ///     is the position after the space.</item>
    ///   <item>If no space exists inside the row width (long unbroken token),
    ///     fall back to a hard mid-token break at exactly
    ///     <paramref name="charsPerRow"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="line">The line being laid out.</param>
    /// <param name="rowStart">Starting position of the row inside <paramref name="line"/>.</param>
    /// <param name="charsPerRow">Maximum characters for this row (first-row width
    ///   or continuation-row width after hanging-indent subtraction).</param>
    /// <returns>
    /// <c>DrawLen</c>: number of characters to draw on this row.
    /// <c>NextStart</c>: position to begin the next row (past a soft-break
    /// space, or equal to <c>rowStart + charsPerRow</c> on a hard break).
    /// </returns>
    public static (int DrawLen, int NextStart) NextRow(string line, int rowStart, int charsPerRow) {
        var remaining = line.Length - rowStart;
        if (remaining <= charsPerRow) {
            return (remaining, line.Length);
        }
        var hardLimit = rowStart + charsPerRow;
        for (var i = hardLimit - 1; i > rowStart; i--) {
            if (line[i] == ' ') {
                return (i - rowStart, i + 1);
            }
        }
        return (charsPerRow, hardLimit);
    }

    // -----------------------------------------------------------------
    // Tab-aware variants
    //
    // These track COLUMNS (screen positions) instead of characters.
    // A tab at column C expands to column ((C / tabWidth) + 1) * tabWidth.
    // All other characters advance by one column.
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns the column width of one character at the given column.
    /// Tabs expand to the next <paramref name="tabWidth"/> boundary.
    /// </summary>
    public static int CharColumns(char c, int col, int tabWidth) {
        if (c == '\t' && tabWidth > 0) {
            return (col / tabWidth + 1) * tabWidth - col;
        }
        return 1;
    }

    /// <summary>
    /// Computes the column (screen position) of character at
    /// <paramref name="charIdx"/> within the range starting at
    /// <paramref name="start"/>, accounting for tab expansion.
    /// </summary>
    public static int ColumnOfChar(string text, int start, int charIdx, int tabWidth) {
        var col = 0;
        for (var i = start; i < charIdx && i < text.Length; i++) {
            col += CharColumns(text[i], col, tabWidth);
        }
        return col;
    }

    /// <summary>
    /// Tab-aware row break.  Walks characters from <paramref name="rowStart"/>,
    /// accumulating columns until hitting <paramref name="colsPerRow"/>.
    /// Word-break logic is the same as <see cref="NextRow"/> but applied
    /// after tab expansion.
    /// </summary>
    public static (int DrawLen, int NextStart) NextRowTabAware(
            string line, int rowStart, int colsPerRow, int tabWidth) {
        var remaining = line.Length - rowStart;
        if (remaining <= 0) return (0, line.Length);

        // Walk characters, accumulating columns.
        var col = 0;
        var lastSpace = -1;
        var charCount = 0;
        for (var i = rowStart; i < line.Length; i++) {
            var c = line[i];
            var cw = CharColumns(c, col, tabWidth);
            if (col + cw > colsPerRow && charCount > 0) {
                // This character would exceed the row width.
                // Try to break at a word boundary.
                if (lastSpace >= 0) {
                    var drawLen = lastSpace - rowStart;
                    return (drawLen, lastSpace + 1);
                }
                // No space — hard break at current position.
                return (charCount, i);
            }
            col += cw;
            charCount++;
            if (c == ' ') lastSpace = i;
        }
        // Everything fits.
        return (remaining, line.Length);
    }

    /// <summary>
    /// Tab-aware row count.  Uses <see cref="NextRowTabAware"/> for the
    /// row-break logic.
    /// </summary>
    public static int CountRowsTabAware(string line, int firstRowCols,
            int contRowCols, int tabWidth) {
        if (line.Length == 0) return 1;
        firstRowCols = System.Math.Max(1, firstRowCols);
        contRowCols = System.Math.Max(1, contRowCols);

        var rows = 0;
        var pos = 0;
        while (pos < line.Length) {
            var cols = rows == 0 ? firstRowCols : contRowCols;
            var (_, nextStart) = NextRowTabAware(line, pos, cols, tabWidth);
            rows++;
            if (nextStart <= pos) break;
            pos = nextStart;
        }
        return System.Math.Max(1, rows);
    }

    /// <summary>
    /// Tab-aware row-of-char.  Returns the zero-based row containing
    /// <paramref name="charInLine"/>.
    /// </summary>
    public static int RowOfCharTabAware(string line, int charInLine,
            int firstRowCols, int contRowCols, int tabWidth) {
        if (line.Length == 0 || charInLine <= 0) return 0;
        firstRowCols = System.Math.Max(1, firstRowCols);
        contRowCols = System.Math.Max(1, contRowCols);

        var pos = 0;
        var row = 0;
        while (pos < line.Length) {
            var cols = row == 0 ? firstRowCols : contRowCols;
            var (_, nextStart) = NextRowTabAware(line, pos, cols, tabWidth);
            if (charInLine < nextStart) return row;
            row++;
            if (nextStart <= pos) break;
            pos = nextStart;
        }
        return System.Math.Max(0, row - 1);
    }

    // -----------------------------------------------------------------
    // Leading-indent helper — used for per-line hanging-indent offset
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns the number of leading whitespace columns in
    /// <paramref name="text"/>, accounting for tab expansion at
    /// <paramref name="tabWidth"/> boundaries.  Stops at the first
    /// non-whitespace character.
    /// </summary>
    public static int LeadingIndentColumns(string text, int tabWidth) {
        var col = 0;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == ' ') col++;
            else if (text[i] == '\t') col = (col / tabWidth + 1) * tabWidth;
            else break;
        }
        return col;
    }

    // -----------------------------------------------------------------
    // Original (non-tab) variants — used for lines without tabs
    // -----------------------------------------------------------------

    /// <summary>
    /// Counts the total number of rows a line would occupy when laid out
    /// with the given row widths.  The first row uses
    /// <paramref name="firstRowChars"/>; continuation rows use
    /// <paramref name="contRowChars"/> (smaller when a hanging indent is
    /// in effect).  Returns 1 for an empty line.  Must match
    /// <c>MonoLineLayout.TryBuild</c>'s row-counting exactly so per-line
    /// row positions computed here line up with the rendered layout.
    /// </summary>
    public static int CountRows(string line, int firstRowChars, int contRowChars) {
        if (line.Length == 0) return 1;
        firstRowChars = System.Math.Max(1, firstRowChars);
        contRowChars = System.Math.Max(1, contRowChars);
        if (line.Length <= firstRowChars) return 1;

        var rows = 1; // first row is a given
        var pos = NextRow(line, 0, firstRowChars).NextStart;
        while (pos < line.Length) {
            rows++;
            var (_, nextStart) = NextRow(line, pos, contRowChars);
            if (nextStart <= pos) break; // safety
            pos = nextStart;
        }
        return rows;
    }

    /// <summary>
    /// Returns the zero-based row index within a line that contains the
    /// character at offset <paramref name="charInLine"/>.  Uses the same
    /// row-break rules as <see cref="NextRow"/>, so results line up with
    /// <see cref="MonoLineLayout.TryBuild"/>'s row spans exactly.
    ///
    /// <para>Used by scroll targeting to translate "sel.Start is on char N
    /// of line L" into "sel.Start is on row R of line L", so we can pin
    /// the specific row (not the logical-line start) to a viewport edge.</para>
    ///
    /// <para>Boundary: a <c>charInLine</c> that sits exactly at the start
    /// of a continuation row (because the previous row broke right before
    /// it) returns the continuation row, not the previous row.  For the
    /// "last word of row" case in scroll targeting, callers should pass
    /// the last-character offset of the selection (<c>sel.End − 1</c>),
    /// not the past-the-end caret offset — the past-the-end offset sits
    /// on the next row and would pull scroll to the wrong row.</para>
    /// </summary>
    public static int RowOfChar(string line, int charInLine,
            int firstRowChars, int contRowChars) {
        if (line.Length == 0) return 0;
        if (charInLine <= 0) return 0;
        firstRowChars = System.Math.Max(1, firstRowChars);
        contRowChars = System.Math.Max(1, contRowChars);

        var pos = 0;
        var row = 0;
        while (pos < line.Length) {
            var cpr = row == 0 ? firstRowChars : contRowChars;
            var (_, nextStart) = NextRow(line, pos, cpr);
            // If the target char is before nextStart, it's on the current row.
            if (charInLine < nextStart) return row;
            row++;
            if (nextStart <= pos) break; // safety
            pos = nextStart;
        }
        // Past the end of the line — return the last row.
        return System.Math.Max(0, row - 1);
    }
}
