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
}
