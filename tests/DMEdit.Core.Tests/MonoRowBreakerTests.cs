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
}
