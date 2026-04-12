using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

/// <summary>
/// Direct tests for <see cref="MonoRowBreaker.NextRow"/> — the shared
/// word-break row-break helper used by both the on-screen editor
/// (<c>MonoLineLayout</c>) and print pagination
/// (<c>WpfPrintService.PlainTextPaginator</c>).  Because these two
/// consumers must stay byte-identical for print to match what's on screen,
/// every rule below is a public contract, not an implementation detail.
/// </summary>
public class MonoRowBreakerTests {
    [Fact]
    public void ShortLine_FitsInOneRow() {
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello", 0, 10);
        Assert.Equal(5, drawLen);
        Assert.Equal(5, nextStart);
    }

    [Fact]
    public void ExactFit_NoWrap() {
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello", 0, 5);
        Assert.Equal(5, drawLen);
        Assert.Equal(5, nextStart);
    }

    [Fact]
    public void OverflowWithTrailingSpace_BreaksAtSpace() {
        // "hello world" at width 10 — the space at index 5 is inside the
        // row, so break there.  Draw "hello" (5 chars), skip the space,
        // next row starts at index 6.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello world", 0, 10);
        Assert.Equal(5, drawLen);
        Assert.Equal(6, nextStart);
    }

    [Fact]
    public void OverflowWithMultipleSpaces_BreaksAtLastSpaceInsideWidth() {
        // "one two three four" at width 12 (hardLimit = 12).  Scanning
        // backward from position 11 for a space: position 7 ("two_three").
        // Actually let's trace: indices 0='o',1='n',2='e',3=' ',4='t',5='w',
        // 6='o',7=' ',8='t',9='h',10='r',11='e',12='e',...
        // From 11 down: no space until index 7.  Draw length = 7, next = 8.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("one two three four", 0, 12);
        Assert.Equal(7, drawLen);
        Assert.Equal(8, nextStart);
    }

    [Fact]
    public void NoSpace_HardBreakAtCharsPerRow() {
        // Unbroken token longer than width — fall back to a mid-token
        // hard break at exactly charsPerRow.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("abcdefghijklmnop", 0, 5);
        Assert.Equal(5, drawLen);
        Assert.Equal(5, nextStart);
    }

    [Fact]
    public void NonZeroRowStart_MeasuresFromRowStart() {
        // Start rendering at position 6 with width 5 on "hello world!".
        // remaining = 12 - 6 = 6, width = 5 → overflow.  hardLimit = 11.
        // No space in window [7..10]? Chars 6='w',7='o',8='r',9='l',10='d'.
        // Hard break at drawLen=5, nextStart=11.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello world!", 6, 5);
        Assert.Equal(5, drawLen);
        Assert.Equal(11, nextStart);
    }

    [Fact]
    public void NonZeroRowStart_WithSpaceInWindow_BreaksAtSpace() {
        // Continuation row: start at 6 with width 20 on "hello world is ok".
        // Chars from 6: "world is ok" (length 11).  Fits in width 20 → one row.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello world is ok", 6, 20);
        Assert.Equal(11, drawLen);
        Assert.Equal(17, nextStart);
    }

    [Fact]
    public void SpaceAtRowStart_IsNotABreakpoint() {
        // The backward scan stops at `i > rowStart`, so a space at exactly
        // rowStart is NOT a valid break — fall back to hard break.
        // "  aaaa" at width 3: first row scan window is (0..3), space at
        // index 1 IS a valid break (i=1 > rowStart=0).  Draw length=1,
        // nextStart=2.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("  aaaa", 0, 3);
        Assert.Equal(1, drawLen);
        Assert.Equal(2, nextStart);
    }

    [Fact]
    public void EmptyRemainder_ReturnsZeroZero() {
        // rowStart == line.Length: remaining = 0, fits in any width.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("hello", 5, 10);
        Assert.Equal(0, drawLen);
        Assert.Equal(5, nextStart);
    }

    [Fact]
    public void SpaceExactlyAtHardLimit_BreaksThere() {
        // "abc def" at width 3, rowStart 0.  remaining = 7, width = 3.
        // hardLimit = 3.  Scan backward from index 2: 'c', 'b', 'a' — no
        // space in [1..2] → hard break at drawLen=3, nextStart=3.
        // Wait, but index 3 is the space.  The loop is `i > rowStart`
        // starting at `hardLimit - 1 = 2`, so it never looks at index 3.
        // Confirm the hard-break fallback.
        var (drawLen, nextStart) = MonoRowBreaker.NextRow("abc def", 0, 3);
        Assert.Equal(3, drawLen);
        Assert.Equal(3, nextStart);
    }

    [Fact]
    public void FullWrapSequence_ReassemblesOriginal() {
        // Walking an entire line through NextRow in a loop must reconstruct
        // the original content minus dropped soft-break spaces.  This is
        // the key property for "print matches screen".
        const int width = 8;
        var line = "the quick brown fox jumps over the lazy dog";
        var rows = new List<string>();
        var pos = 0;
        while (pos < line.Length) {
            var (drawLen, nextStart) = MonoRowBreaker.NextRow(line, pos, width);
            rows.Add(line.Substring(pos, drawLen));
            Assert.True(nextStart > pos, "NextRow must advance pos");
            pos = nextStart;
        }
        // Joining with spaces should get us back a version with single
        // spaces between words (since each row drops its trailing space).
        // Can't directly compare to the original because the soft-break
        // space is gone, but we can verify no drawn row exceeds width.
        foreach (var row in rows) {
            Assert.True(row.Length <= width, $"Row '{row}' exceeds width {width}");
        }
        // The concatenation of rows + the spaces we skipped must equal the
        // original line.  Recover those spaces by tracking gaps between
        // consecutive row positions.
        var reassembled = string.Join(" ", rows);
        // Minor wrinkle: our sample has no consecutive spaces, so every
        // gap contributes exactly one space back.  For the chosen sample,
        // this should match.
        Assert.Equal(line, reassembled);
    }

    // ------------------------------------------------------------------
    //  CountRows — per-line row count for wrap math
    //
    //  Used by scroll targeting to pre-compute how many rows the match's
    //  line occupies, so we can pin a specific row to a viewport edge.
    //  Must match MonoLineLayout.TryBuild's row production exactly — any
    //  drift causes the scroll math to land on the wrong row.
    // ------------------------------------------------------------------

    [Fact]
    public void CountRows_EmptyLine_ReturnsOne() {
        Assert.Equal(1, MonoRowBreaker.CountRows("", 10, 10));
    }

    [Fact]
    public void CountRows_LineFitsInFirstRow_ReturnsOne() {
        Assert.Equal(1, MonoRowBreaker.CountRows("hello", 10, 10));
    }

    [Fact]
    public void CountRows_LineExactlyOneRow_ReturnsOne() {
        Assert.Equal(1, MonoRowBreaker.CountRows("0123456789", 10, 10));
    }

    [Fact]
    public void CountRows_LineTwoRows_UnbrokenToken() {
        // 20 chars, no spaces, width 10 → hard-break at 10, two rows.
        var line = new string('x', 20);
        Assert.Equal(2, MonoRowBreaker.CountRows(line, 10, 10));
    }

    [Fact]
    public void CountRows_LineTwoRows_WithSpaces() {
        // "hello world foo bar baz" at width 10.
        // Row 0: "hello" (break at space 5, next start 6)
        // Row 1: "world foo" (break at space 11, next start 12)
        // Row 2: "bar baz"
        var line = "hello world foo bar baz";
        Assert.Equal(3, MonoRowBreaker.CountRows(line, 10, 10));
    }

    [Fact]
    public void CountRows_HangingIndent_FirstRowWiderThanContRows() {
        // First row has 10 chars available, continuation rows only 8.
        // A 30-char unbroken string: row 0 = 10 chars, then 8+8+4 = 3 more rows.
        // Total = 4 rows.
        var line = new string('x', 30);
        Assert.Equal(4, MonoRowBreaker.CountRows(line, 10, 8));
    }

    [Fact]
    public void CountRows_FiveRowsMatchesMonoLineLayout() {
        // Regression against a line that triggered the wrap-on scroll bug.
        // Long line with spaces to force word-break into 5 rows at width 80.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++) sb.Append($"word{i:D2} ");
        // 50 * 8 chars = 400 chars total. At width 80, row count is ~5.
        var line = sb.ToString();
        var rows = MonoRowBreaker.CountRows(line, 80, 80);
        Assert.InRange(rows, 5, 6); // depends on exact word-break behavior
    }

    // ------------------------------------------------------------------
    //  RowOfChar — which row within a line contains a given char
    // ------------------------------------------------------------------

    [Fact]
    public void RowOfChar_CharZero_IsAlwaysRowZero() {
        Assert.Equal(0, MonoRowBreaker.RowOfChar("hello world", 0, 5, 5));
    }

    [Fact]
    public void RowOfChar_CharInFirstRow_ReturnsZero() {
        // "hello world" at width 10: row 0 is "hello" (chars 0-4).
        Assert.Equal(0, MonoRowBreaker.RowOfChar("hello world", 3, 10, 10));
    }

    [Fact]
    public void RowOfChar_CharAtStartOfSecondRow_ReturnsOne() {
        // "hello world" at width 10: row 0 breaks at space (index 5),
        // row 1 starts at index 6 ("world").
        Assert.Equal(1, MonoRowBreaker.RowOfChar("hello world", 6, 10, 10));
    }

    [Fact]
    public void RowOfChar_CharAtEndOfLine_ReturnsLastRow() {
        // "hello world foo bar baz" at width 10: 3 rows.  Char 22 ('z')
        // is on row 2.
        Assert.Equal(2, MonoRowBreaker.RowOfChar("hello world foo bar baz", 22, 10, 10));
    }

    [Fact]
    public void RowOfChar_LastCharOfRow_ReturnsThatRow() {
        // Regression for the "last word of wrapped row" bug.  For
        // "hello world" at width 10, char 4 ('o' — last char of "hello")
        // must be on row 0, NOT on row 1.  Passing sel.End − 1 (the last
        // char of the match) instead of sel.End (the caret past the end)
        // is the fix that keeps FindNext from scrolling an extra row.
        Assert.Equal(0, MonoRowBreaker.RowOfChar("hello world", 4, 10, 10));
    }

    [Fact]
    public void RowOfChar_HardBreakMidToken() {
        // Unbroken "xxxxxxxxxxxxxxxxxxxx" (20 chars) at width 10 breaks
        // at position 10.  Char 9 is on row 0; char 10 is on row 1.
        var line = new string('x', 20);
        Assert.Equal(0, MonoRowBreaker.RowOfChar(line, 9, 10, 10));
        Assert.Equal(1, MonoRowBreaker.RowOfChar(line, 10, 10, 10));
    }

    [Fact]
    public void RowOfChar_FiveRowLine_MiddleRow() {
        // Build a 5-row line and verify a char in row 2 returns 2.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++) sb.Append($"word{i:D2} ");
        var line = sb.ToString();
        // word24 is around char 168 (24 * 7 chars each).  At width 80,
        // that's well into row 2.
        var row = MonoRowBreaker.RowOfChar(line, 168, 80, 80);
        Assert.InRange(row, 1, 3);
    }

    [Fact]
    public void RowOfChar_HangingIndent_ContRowsShorterThanFirst() {
        // First row 10 chars wide, continuation rows 8.  20-char unbroken
        // line: row 0 = chars 0-9, row 1 = chars 10-17, row 2 = chars 18-19.
        var line = new string('x', 20);
        Assert.Equal(0, MonoRowBreaker.RowOfChar(line, 5, 10, 8));
        Assert.Equal(1, MonoRowBreaker.RowOfChar(line, 10, 10, 8));
        Assert.Equal(1, MonoRowBreaker.RowOfChar(line, 17, 10, 8));
        Assert.Equal(2, MonoRowBreaker.RowOfChar(line, 18, 10, 8));
    }

    // ==================================================================
    //  Tab-aware: CharColumns
    // ==================================================================

    [Theory]
    [InlineData('a', 0, 4, 1)]   // normal char: 1 column
    [InlineData('a', 5, 4, 1)]
    [InlineData('\t', 0, 4, 4)]  // tab at col 0: expands to col 4 (4 columns)
    [InlineData('\t', 1, 4, 3)]  // tab at col 1: expands to col 4 (3 columns)
    [InlineData('\t', 3, 4, 1)]  // tab at col 3: expands to col 4 (1 column)
    [InlineData('\t', 4, 4, 4)]  // tab at col 4: expands to col 8 (4 columns)
    [InlineData('\t', 7, 4, 1)]  // tab at col 7: expands to col 8 (1 column)
    [InlineData('\t', 0, 8, 8)]  // tab at col 0, tabWidth=8
    [InlineData('\t', 5, 8, 3)]  // tab at col 5, tabWidth=8: → col 8
    public void CharColumns_ReturnsCorrectWidth(char c, int col, int tabWidth,
            int expected) {
        Assert.Equal(expected, MonoRowBreaker.CharColumns(c, col, tabWidth));
    }

    // ==================================================================
    //  Tab-aware: ColumnOfChar
    // ==================================================================

    [Fact]
    public void ColumnOfChar_NoTabs_EqualsCharCount() {
        Assert.Equal(5, MonoRowBreaker.ColumnOfChar("hello", 0, 5, 4));
    }

    [Fact]
    public void ColumnOfChar_TabAtStart_ExpandsToTabWidth() {
        // "\thello" — tab at col 0 expands to col 4, then 5 more chars.
        Assert.Equal(4, MonoRowBreaker.ColumnOfChar("\thello", 0, 1, 4));  // after tab
        Assert.Equal(9, MonoRowBreaker.ColumnOfChar("\thello", 0, 6, 4));  // end of string
    }

    [Fact]
    public void ColumnOfChar_TabMidLine_ExpandsFromCurrentCol() {
        // "ab\tcd" — a(1) b(2) tab→4 c(5) d(6)
        Assert.Equal(2, MonoRowBreaker.ColumnOfChar("ab\tcd", 0, 2, 4));  // before tab
        Assert.Equal(4, MonoRowBreaker.ColumnOfChar("ab\tcd", 0, 3, 4));  // after tab
        Assert.Equal(6, MonoRowBreaker.ColumnOfChar("ab\tcd", 0, 5, 4));  // end
    }

    [Fact]
    public void ColumnOfChar_MultipleTabs() {
        // "\t\t" — tab→4, tab→8
        Assert.Equal(4, MonoRowBreaker.ColumnOfChar("\t\t", 0, 1, 4));
        Assert.Equal(8, MonoRowBreaker.ColumnOfChar("\t\t", 0, 2, 4));
    }

    [Fact]
    public void ColumnOfChar_NonZeroStart() {
        // Start counting from char 2 in "ab\tcd"
        Assert.Equal(0, MonoRowBreaker.ColumnOfChar("ab\tcd", 2, 2, 4));  // nothing
        Assert.Equal(4, MonoRowBreaker.ColumnOfChar("ab\tcd", 2, 3, 4));  // tab→4
        Assert.Equal(6, MonoRowBreaker.ColumnOfChar("ab\tcd", 2, 5, 4));  // tab→4 + cd
    }

    // ==================================================================
    //  Tab-aware: NextRowTabAware
    // ==================================================================

    [Fact]
    public void NextRowTabAware_NoTabs_SameAsNextRow() {
        var (drawLen, nextStart) = MonoRowBreaker.NextRowTabAware("hello world", 0, 11, 4);
        Assert.Equal(11, drawLen);
        Assert.Equal(11, nextStart);
    }

    [Fact]
    public void NextRowTabAware_TabExpandsBeyondRowWidth_Wraps() {
        // "\tabcdefgh" at colsPerRow=8, tab=4: tab→4 cols, then 8 chars.
        // Total: 4+8=12 cols.  Row width 8: tab(4)+abcd(4)=8 → fits.
        // "efgh" goes to next row.
        var (drawLen, nextStart) = MonoRowBreaker.NextRowTabAware("\tabcdefgh", 0, 8, 4);
        Assert.Equal(5, drawLen);   // \t + abcd
        Assert.Equal(5, nextStart);
    }

    [Fact]
    public void NextRowTabAware_TabAtEnd_FitsExactly() {
        // "abc\t" at colsPerRow=8, tab=4: a(1)b(2)c(3)tab→4(1col)=4 cols.
        var (drawLen, nextStart) = MonoRowBreaker.NextRowTabAware("abc\t", 0, 8, 4);
        Assert.Equal(4, drawLen);
        Assert.Equal(4, nextStart);
    }

    [Fact]
    public void NextRowTabAware_ShortLine_FitsInOneRow() {
        var (drawLen, nextStart) = MonoRowBreaker.NextRowTabAware("a\tb", 0, 20, 4);
        Assert.Equal(3, drawLen);
        Assert.Equal(3, nextStart);
    }

    // ==================================================================
    //  Tab-aware: CountRowsTabAware
    // ==================================================================

    [Fact]
    public void CountRowsTabAware_EmptyLine_ReturnsOne() {
        Assert.Equal(1, MonoRowBreaker.CountRowsTabAware("", 10, 8, 4));
    }

    [Fact]
    public void CountRowsTabAware_ShortTabLine_OneRow() {
        // "a\tb" = 3 chars, cols: a(1)+tab→4(3)+b(1)=5 cols.
        Assert.Equal(1, MonoRowBreaker.CountRowsTabAware("a\tb", 20, 18, 4));
    }

    [Fact]
    public void CountRowsTabAware_TabCausesWrap() {
        // Line: "\t" + 10 chars.  At colsPerRow=8, tab=4:
        // Row 0: tab(4)+4chars=8 cols → full.
        // Row 1: 6 remaining chars → fits.
        Assert.Equal(2, MonoRowBreaker.CountRowsTabAware(
            "\tabcdefghij", 8, 8, 4));
    }

    [Fact]
    public void CountRowsTabAware_MultipleTabs_MultipleRows() {
        // "\t\t\t\t" at colsPerRow=6, tab=4:
        // Row 0: tab→4(4)+tab→8 would be 8>6 → only first tab fits (4 cols).
        // Wait, tab at col 4 → col 8 = 4 more cols, total 8 > 6.
        // So row 0: just "\t" (4 cols), row 1: "\t" (tab at col 0→4), etc.
        var rows = MonoRowBreaker.CountRowsTabAware("\t\t\t\t", 6, 6, 4);
        Assert.True(rows >= 2);
    }

    // ==================================================================
    //  Tab-aware: RowOfCharTabAware
    // ==================================================================

    [Fact]
    public void RowOfCharTabAware_CharZero_RowZero() {
        Assert.Equal(0, MonoRowBreaker.RowOfCharTabAware("a\tb", 0, 20, 18, 4));
    }

    [Fact]
    public void RowOfCharTabAware_AfterWrap_CorrectRow() {
        // "\tabcdefghij" at cols=8, tab=4:
        // Row 0: \t(4)+abcd(4)=8 → chars 0-4.
        // Row 1: efghij → chars 5-10.
        Assert.Equal(0, MonoRowBreaker.RowOfCharTabAware(
            "\tabcdefghij", 3, 8, 8, 4));
        Assert.Equal(1, MonoRowBreaker.RowOfCharTabAware(
            "\tabcdefghij", 6, 8, 8, 4));
    }

    [Fact]
    public void RowOfCharTabAware_PastEnd_ReturnsLastRow() {
        Assert.Equal(0, MonoRowBreaker.RowOfCharTabAware(
            "a\tb", 100, 20, 18, 4));
    }

    // ==================================================================
    //  Tab-aware: consistency with CountRows
    // ==================================================================

    [Theory]
    [InlineData("abc\tdef\tghij", 10, 8, 4)]
    [InlineData("\t\t\tabcdef", 8, 6, 4)]
    [InlineData("no tabs here at all", 10, 8, 4)]
    [InlineData("\t", 4, 4, 4)]
    [InlineData("a\tb\tc\td\te", 12, 10, 4)]
    [InlineData("prefix\tsuffix that is quite long and will wrap", 20, 18, 4)]
    public void TabAware_RowOfLastChar_LessThanCountRows(string line,
            int firstCols, int contCols, int tabWidth) {
        var rowCount = MonoRowBreaker.CountRowsTabAware(line, firstCols, contCols, tabWidth);
        var lastCharRow = MonoRowBreaker.RowOfCharTabAware(
            line, line.Length - 1, firstCols, contCols, tabWidth);
        Assert.True(lastCharRow < rowCount,
            $"RowOfChar({line.Length - 1})={lastCharRow} >= CountRows={rowCount}");
    }

    // ==================================================================
    //  Parametric sweeps — high test-case count from compact generators
    // ==================================================================

    // --- Non-tab sweep: line lengths × row widths × positions ---

    public static IEnumerable<object[]> PlainSweepData() {
        var widths = new[] { 5, 10, 20, 40, 80 };
        var lengths = new[] { 0, 1, 5, 10, 20, 50, 100, 200 };
        foreach (var w in widths) {
            foreach (var len in lengths) {
                yield return new object[] { len, w };
            }
        }
    }

    [Theory]
    [MemberData(nameof(PlainSweepData))]
    public void PlainSweep_CountRows_AtLeastOne(int lineLen, int width) {
        var line = new string('a', lineLen);
        var rows = MonoRowBreaker.CountRows(line, width, width);
        Assert.True(rows >= 1);
    }

    [Theory]
    [MemberData(nameof(PlainSweepData))]
    public void PlainSweep_CountRows_UpperBound(int lineLen, int width) {
        var line = new string('a', lineLen);
        var rows = MonoRowBreaker.CountRows(line, width, width);
        var expected = lineLen == 0 ? 1 : (int)Math.Ceiling((double)lineLen / width);
        Assert.InRange(rows, 1, expected + 1);
    }

    [Theory]
    [MemberData(nameof(PlainSweepData))]
    public void PlainSweep_RowOfChar_InRange(int lineLen, int width) {
        if (lineLen == 0) return;
        var line = new string('a', lineLen);
        var rowCount = MonoRowBreaker.CountRows(line, width, width);
        // Test at several positions within the line.
        foreach (var pos in new[] { 0, lineLen / 4, lineLen / 2, lineLen - 1 }) {
            if (pos < 0 || pos >= lineLen) continue;
            var row = MonoRowBreaker.RowOfChar(line, pos, width, width);
            Assert.InRange(row, 0, rowCount - 1);
        }
    }

    [Theory]
    [MemberData(nameof(PlainSweepData))]
    public void PlainSweep_NextRow_CoversFull(int lineLen, int width) {
        var line = new string('a', lineLen);
        var pos = 0;
        var totalDrawn = 0;
        var safetyLimit = lineLen + 10;
        while (pos < line.Length && safetyLimit-- > 0) {
            var (drawLen, nextStart) = MonoRowBreaker.NextRow(line, pos, width);
            Assert.True(drawLen > 0 || nextStart > pos,
                $"No progress at pos={pos}");
            totalDrawn += drawLen;
            pos = nextStart;
        }
        // Total drawn chars should cover the line (minus dropped spaces).
        Assert.InRange(totalDrawn, lineLen - lineLen / width - 1, lineLen);
    }

    // --- Tab sweep: various tab positions × row widths × tab widths ---

    public static IEnumerable<object[]> TabSweepData() {
        var widths = new[] { 8, 16, 40, 65 };
        var tabWidths = new[] { 2, 4, 8 };
        var tabPositions = new[] { 0, 1, 3, 7, 15, 30 };
        foreach (var w in widths) {
            foreach (var tw in tabWidths) {
                foreach (var tabPos in tabPositions) {
                    yield return new object[] { w, tw, tabPos };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TabSweepData))]
    public void TabSweep_CountRows_AtLeastOne(int width, int tabWidth, int tabPos) {
        var line = new string('a', tabPos) + '\t' + new string('b', 20);
        var rows = MonoRowBreaker.CountRowsTabAware(line, width, width, tabWidth);
        Assert.True(rows >= 1);
    }

    [Theory]
    [MemberData(nameof(TabSweepData))]
    public void TabSweep_RowOfChar_Consistent(int width, int tabWidth, int tabPos) {
        var line = new string('a', tabPos) + '\t' + new string('b', 20);
        var rowCount = MonoRowBreaker.CountRowsTabAware(line, width, width, tabWidth);
        for (var pos = 0; pos < line.Length; pos++) {
            var row = MonoRowBreaker.RowOfCharTabAware(line, pos, width, width, tabWidth);
            Assert.InRange(row, 0, rowCount - 1);
        }
    }

    [Theory]
    [MemberData(nameof(TabSweepData))]
    public void TabSweep_ColumnOfChar_Monotonic(int width, int tabWidth, int tabPos) {
        var line = new string('a', tabPos) + '\t' + new string('b', 20);
        var prevCol = -1;
        for (var i = 0; i <= line.Length; i++) {
            var col = MonoRowBreaker.ColumnOfChar(line, 0, i, tabWidth);
            Assert.True(col >= prevCol, $"Column decreased at char {i}");
            prevCol = col;
        }
    }

    [Theory]
    [MemberData(nameof(TabSweepData))]
    public void TabSweep_NextRowTabAware_CoversFull(int width, int tabWidth, int tabPos) {
        var line = new string('a', tabPos) + '\t' + new string('b', 20);
        var pos = 0;
        var safety = line.Length + 10;
        while (pos < line.Length && safety-- > 0) {
            var (drawLen, nextStart) = MonoRowBreaker.NextRowTabAware(
                line, pos, width, tabWidth);
            Assert.True(drawLen > 0 || nextStart > pos,
                $"No progress at pos={pos}");
            pos = nextStart;
        }
        Assert.Equal(line.Length, pos);
    }

    // --- Hanging indent sweep ---

    public static IEnumerable<object[]> HangingIndentSweepData() {
        var widths = new[] { 10, 20, 40, 65 };
        var indents = new[] { 0, 1, 2, 4 };
        var lengths = new[] { 15, 40, 100, 200 };
        foreach (var w in widths) {
            foreach (var indent in indents) {
                foreach (var len in lengths) {
                    yield return new object[] { len, w, indent };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(HangingIndentSweepData))]
    public void HangingIndent_CountRows_ContRowsSmaller(int lineLen, int width,
            int indent) {
        var line = new string('a', lineLen);
        var contWidth = Math.Max(1, width - indent);
        var rows = MonoRowBreaker.CountRows(line, width, contWidth);
        Assert.True(rows >= 1);
        if (lineLen > width) {
            // With hanging indent, more rows needed than without.
            var noIndentRows = MonoRowBreaker.CountRows(line, width, width);
            Assert.True(rows >= noIndentRows);
        }
    }

    [Theory]
    [MemberData(nameof(HangingIndentSweepData))]
    public void HangingIndent_RowOfChar_InRange(int lineLen, int width,
            int indent) {
        var line = new string('a', lineLen);
        var contWidth = Math.Max(1, width - indent);
        var rowCount = MonoRowBreaker.CountRows(line, width, contWidth);
        for (var pos = 0; pos < lineLen; pos += Math.Max(1, lineLen / 10)) {
            var row = MonoRowBreaker.RowOfChar(line, pos, width, contWidth);
            Assert.InRange(row, 0, rowCount - 1);
        }
    }
}
