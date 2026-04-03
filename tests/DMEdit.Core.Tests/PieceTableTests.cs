using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

public class PieceTableTests {
    // -------------------------------------------------------------------------
    // Basic construction
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyDocument_HasZeroLength() {
        var t = new PieceTable("");
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void EmptyDocument_HasOneLine() {
        var t = new PieceTable("");
        Assert.Equal(1, t.LineCount);
    }

    [Fact]
    public void InitialContent_RoundTrips() {
        var content = "Hello, world!";
        var t = new PieceTable(content);
        Assert.Equal(content, t.GetText());
    }

    // -------------------------------------------------------------------------
    // Insert
    // -------------------------------------------------------------------------

    [Fact]
    public void InsertIntoEmpty_ProducesText() {
        var t = new PieceTable("");
        t.Insert(0, "abc");
        Assert.Equal("abc", t.GetText());
    }

    [Fact]
    public void InsertAtStart() {
        var t = new PieceTable("world");
        t.Insert(0, "hello ");
        Assert.Equal("hello world", t.GetText());
    }

    [Fact]
    public void InsertAtEnd() {
        var t = new PieceTable("hello");
        t.Insert(5, " world");
        Assert.Equal("hello world", t.GetText());
    }

    [Fact]
    public void InsertInMiddle_SplitsPiece() {
        var t = new PieceTable("helo");
        t.Insert(3, "l");
        Assert.Equal("hello", t.GetText());
    }

    [Fact]
    public void MultipleInserts_ProduceCorrectText() {
        var t = new PieceTable("");
        t.Insert(0, "a");
        t.Insert(1, "b");
        t.Insert(2, "c");
        Assert.Equal("abc", t.GetText());
    }

    [Fact]
    public void InsertAtBoundary_EmptyString_IsNoOp() {
        var t = new PieceTable("abc");
        t.Insert(1, "");
        Assert.Equal("abc", t.GetText());
        Assert.Equal(3, t.Length);
    }

    // -------------------------------------------------------------------------
    // Delete
    // -------------------------------------------------------------------------

    [Fact]
    public void DeleteFromStart() {
        var t = new PieceTable("hello");
        t.Delete(0, 2);
        Assert.Equal("llo", t.GetText());
    }

    [Fact]
    public void DeleteFromEnd() {
        var t = new PieceTable("hello");
        t.Delete(3, 2);
        Assert.Equal("hel", t.GetText());
    }

    [Fact]
    public void DeleteMiddle() {
        var t = new PieceTable("hello");
        t.Delete(1, 3);
        Assert.Equal("ho", t.GetText());
    }

    [Fact]
    public void DeleteAll() {
        var t = new PieceTable("hello");
        t.Delete(0, 5);
        Assert.Equal("", t.GetText());
        Assert.Equal(0, t.Length);
    }

    [Fact]
    public void DeleteZeroLength_IsNoOp() {
        var t = new PieceTable("abc");
        t.Delete(1, 0);
        Assert.Equal("abc", t.GetText());
    }

    [Fact]
    public void DeleteAcrossPieceBoundary() {
        var t = new PieceTable("hello");
        t.Insert(3, "XXX");   // he|lo → heXXXllo... wait, inserting after 'l': "helXXXlo"
        // Now the doc is "helXXXlo" — pieces: orig[0..3], add[0..3], orig[3..5]
        // Delete "lXX" (positions 2..4 inclusive = offset 2, len 3)
        t.Delete(2, 3);
        Assert.Equal("heXlo", t.GetText());
    }

    // -------------------------------------------------------------------------
    // GetText(start, len)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetTextSubrange() {
        var t = new PieceTable("hello world");
        Assert.Equal("world", t.GetText(6, 5));
    }

    [Fact]
    public void GetTextSubrange_ZeroLen_ReturnsEmpty() {
        var t = new PieceTable("hello");
        Assert.Equal("", t.GetText(2, 0));
    }

    // -------------------------------------------------------------------------
    // ForEachPiece
    // -------------------------------------------------------------------------

    [Fact]
    public void ForEachPiece_ConcatenatesCorrectly() {
        var t = new PieceTable("hello");
        t.Insert(2, "XY");
        // doc is "heXYllo"
        var sb = new System.Text.StringBuilder();
        t.ForEachPiece(0, t.Length, span => sb.Append(span));
        Assert.Equal(t.GetText(), sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Line index
    // -------------------------------------------------------------------------

    [Fact]
    public void NoNewlines_HasOneLine() {
        var t = new PieceTable("hello");
        Assert.Equal(1, t.LineCount);
    }

    [Fact]
    public void OneNewline_HasTwoLines() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(2, t.LineCount);
    }

    [Fact]
    public void TrailingNewline_ExtraEmptyLine() {
        var t = new PieceTable("hello\n");
        Assert.Equal(2, t.LineCount);
    }

    [Fact]
    public void LineStartOfs_FirstLine_IsZero() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(0, t.LineStartOfs(0));
    }

    [Fact]
    public void LineStartOfs_SecondLine_AfterNewline() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(6, t.LineStartOfs(1));
    }

    [Fact]
    public void LineFromOfs_InFirstLine() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(0, t.LineFromOfs(3));
    }

    [Fact]
    public void LineFromOfs_InSecondLine() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(1, t.LineFromOfs(7));
    }

    [Fact]
    public void LineFromOfs_AtNewline_IsFirstLine() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal(0, t.LineFromOfs(5)); // '\n' is still on line 0
    }

    [Fact]
    public void GetLine_ReturnsTextWithoutNewline() {
        var t = new PieceTable("hello\nworld");
        Assert.Equal("hello", t.GetLine(0));
        Assert.Equal("world", t.GetLine(1));
    }

    [Fact]
    public void LineIndex_UpdatesAfterInsertNewline() {
        var t = new PieceTable("helloworld");
        Assert.Equal(1, t.LineCount);
        t.Insert(5, "\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal("hello", t.GetLine(0));
        Assert.Equal("world", t.GetLine(1));
    }

    [Fact]
    public void LineIndex_UpdatesAfterDeleteNewline() {
        var t = new PieceTable("hello\nworld");
        t.Delete(5, 1); // remove '\n'
        Assert.Equal(1, t.LineCount);
        Assert.Equal("helloworld", t.GetLine(0));
    }

    [Fact]
    public void CrLf_CountedAsSingleNewline() {
        var t = new PieceTable("hello\r\nworld");
        Assert.Equal(2, t.LineCount);
        Assert.Equal("hello", t.GetLine(0));
        Assert.Equal("world", t.GetLine(1));
    }

    // -------------------------------------------------------------------------
    // Undo/redo via EditHistory
    // -------------------------------------------------------------------------

    [Fact]
    public void InsertAndUndo_RestoresOriginal() {
        var doc = MakeDoc("hello");
        doc.Selection = Selection.Collapsed(5); // move caret to end
        doc.Insert(" world");
        Assert.Equal("hello world", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("hello", doc.Table.GetText());
    }

    [Fact]
    public void DeleteAndUndo_RestoresOriginal() {
        var doc = MakeDoc("hello world");
        doc.Selection = Selection.Collapsed(5);
        doc.DeleteForward(); // deletes space
        Assert.Equal("helloworld", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("hello world", doc.Table.GetText());
    }

    [Fact]
    public void UndoThenRedo() {
        var doc = MakeDoc("");
        doc.Insert("abc");
        doc.Undo();
        Assert.Equal("", doc.Table.GetText());
        doc.Redo();
        Assert.Equal("abc", doc.Table.GetText());
    }

    // -------------------------------------------------------------------------
    // LineContentLength
    // -------------------------------------------------------------------------

    // (Pseudo-line tests removed — pseudo-line system no longer exists.)

    private static PieceTable MakeTable(string content) {
        return new PieceTable(content);
    }

    private const int T = 10; // test line length for content-length tests

    // -------------------------------------------------------------------------
    // LineContentLength
    // -------------------------------------------------------------------------

// -------------------------------------------------------------------------
    // LineContentLength (with MaxPseudoLine = 10 for easy testing)
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentLength_Empty() {
        var t = MakeTable("");
        Assert.Equal(1, t.LineCount);
        Assert.Equal(0, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_OneChar() {
        var t = MakeTable("a");
        Assert.Equal(1, t.LineCount);
        Assert.Equal(1, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_OneChar_LF() {
        var t = MakeTable("a\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(1, t.LineContentLength(0));
        Assert.Equal(0, t.LineContentLength(1));
        Assert.Equal(LineTerminatorType.LF, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(1));
    }

    [Fact]
    public void ContentLength_OneChar_CRLF() {
        var t = MakeTable("a\r\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(1, t.LineContentLength(0));
        Assert.Equal(0, t.LineContentLength(1));
        Assert.Equal(LineTerminatorType.CRLF, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(1));
    }

    [Fact]
    public void ContentLength_OneChar_CR() {
        var t = MakeTable("a\r");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(1, t.LineContentLength(0));
        Assert.Equal(0, t.LineContentLength(1));
        Assert.Equal(LineTerminatorType.CR, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_ExactlyMPL_NoTerminator() {
        // 10 chars, no newline — single line
        var t = MakeTable(new string('a', T));
        Assert.Equal(1, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_ExactlyMPL_LF() {
        // 10 content + LF = 11 total, no pseudo-split needed
        var t = MakeTable(new string('a', T) + "\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.LF, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_ExactlyMPL_CRLF() {
        // 10 content + CRLF = 12 total, no pseudo-split needed
        var t = MakeTable(new string('a', T) + "\r\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.CRLF, t.GetLineTerminator(0));
    }

[Fact]
    public void ContentLength_MultipleLines_Mixed() {
        // "hello\nworld\r\n!" → LF, CRLF, None
        var t = MakeTable("hello\nworld\r\n!");
        Assert.Equal(3, t.LineCount);
        Assert.Equal(5, t.LineContentLength(0));
        Assert.Equal(5, t.LineContentLength(1));
        Assert.Equal(1, t.LineContentLength(2));
        Assert.Equal(LineTerminatorType.LF, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.CRLF, t.GetLineTerminator(1));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(2));
    }

// -------------------------------------------------------------------------
    // Dead zone navigation (line terminator skipping)
    // -------------------------------------------------------------------------

    // Simulates arrow-right: move one position, snap out of dead zone forward.
    private static long ArrowRight(PieceTable t, long caret) {
        var newCaret = Math.Min(caret + 1, t.Length);
        return SnapForward(t, newCaret);
    }

    // Simulates arrow-left: move one position, snap out of dead zone backward.
    private static long ArrowLeft(PieceTable t, long caret) {
        var newCaret = Math.Max(caret - 1, 0);
        return SnapBackward(t, newCaret);
    }

    // Simulates End key: go to content end of current line.
    private static long EndKey(PieceTable t, long caret) {
        var line = (int)t.LineFromOfs(Math.Min(caret, t.Length));
        return t.LineStartOfs(line) + t.LineContentLength(line);
    }

    // Simulates Home key: go to start of current line.
    private static long HomeKey(PieceTable t, long caret) {
        var line = (int)t.LineFromOfs(Math.Min(caret, t.Length));
        return t.LineStartOfs(line);
    }

    private static long SnapForward(PieceTable t, long ofs) {
        if (ofs <= 0 || ofs >= t.Length) return ofs;
        var line = (int)t.LineFromOfs(ofs);
        var lineStart = t.LineStartOfs(line);
        var contentEnd = lineStart + t.LineContentLength(line);
        if (ofs > contentEnd) {
            return line + 1 < t.LineCount ? t.LineStartOfs(line + 1) : t.Length;
        }
        return ofs;
    }

    private static long SnapBackward(PieceTable t, long ofs) {
        if (ofs <= 0 || ofs >= t.Length) return ofs;
        var line = (int)t.LineFromOfs(ofs);
        var lineStart = t.LineStartOfs(line);
        var contentEnd = lineStart + t.LineContentLength(line);
        if (ofs > contentEnd) {
            return contentEnd;
        }
        return ofs;
    }

    [Fact]
    public void Nav_LF_ArrowRightSkipsTerminator() {
        // "ab\ncd" → line 0: content "ab", line 1: content "cd"
        var t = MakeTable("ab\ncd");
        // At 'b' (offset 1), arrow right → offset 2 (content end of line 0)
        Assert.Equal(2L, ArrowRight(t, 1));
        // At content end (offset 2), arrow right → offset 3 (start of line 1, 'c')
        Assert.Equal(3L, ArrowRight(t, 2));
    }

    [Fact]
    public void Nav_CRLF_ArrowRightSkipsTerminator() {
        // "ab\r\ncd" → line 0: "ab" (offsets 0-1), dead zone 2-3, line 1: "cd" (offsets 4-5)
        var t = MakeTable("ab\r\ncd");
        // At 'b' (offset 1), arrow right → offset 2 (content end)
        Assert.Equal(2L, ArrowRight(t, 1));
        // At content end (offset 2), arrow right → offset 4 (skip \r\n)
        Assert.Equal(4L, ArrowRight(t, 2));
    }

    [Fact]
    public void Nav_CRLF_ArrowLeftSkipsTerminator() {
        // "ab\r\ncd"
        var t = MakeTable("ab\r\ncd");
        // At 'c' (offset 4), arrow left → offset 2 (content end of line 0)
        Assert.Equal(2L, ArrowLeft(t, 4));
    }

    [Fact]
    public void Nav_LF_ArrowLeftSkipsTerminator() {
        // "ab\ncd"
        var t = MakeTable("ab\ncd");
        // At 'c' (offset 3), arrow left → offset 2 (content end of line 0)
        Assert.Equal(2L, ArrowLeft(t, 3));
    }

[Fact]
    public void Nav_CRLF_EndKey() {
        // "hello\r\nworld"
        var t = MakeTable("hello\r\nworld");
        // Line 0, End → offset 5 (after 'o', before \r)
        Assert.Equal(5L, EndKey(t, 0));
        // Line 1, End → offset 12 (after 'd')
        Assert.Equal(12L, EndKey(t, 7));
    }

    [Fact]
    public void Nav_CRLF_HomeKey() {
        // "hello\r\nworld"
        var t = MakeTable("hello\r\nworld");
        // Line 0, Home → 0
        Assert.Equal(0L, HomeKey(t, 3));
        // Line 1, Home → 7
        Assert.Equal(7L, HomeKey(t, 10));
    }

    [Fact]
    public void Nav_EndThenArrowRight_GoesToNextLine() {
        // "ab\r\ncd" — End on line 0, then arrow right → start of line 1
        var t = MakeTable("ab\r\ncd");
        var pos = EndKey(t, 0);   // → 2
        Assert.Equal(2L, pos);
        pos = ArrowRight(t, pos);  // → 4 (skip dead zone)
        Assert.Equal(4L, pos);
    }

    [Fact]
    public void Nav_HomeThenArrowLeft_GoesToPrevLineEnd() {
        // "ab\r\ncd" — Home on line 1, then arrow left → content end of line 0
        var t = MakeTable("ab\r\ncd");
        var pos = HomeKey(t, 4);   // → 4
        Assert.Equal(4L, pos);
        pos = ArrowLeft(t, pos);   // → 2 (content end of line 0, skipping dead zone)
        Assert.Equal(2L, pos);
    }

    [Fact]
    public void Nav_WalkEntireDocument_LF() {
        // Walk through "ab\ncd\n" with arrow right, verify we hit every content position
        var t = MakeTable("ab\ncd\n");
        var visited = new List<long>();
        var pos = 0L;
        visited.Add(pos);
        while (pos < t.Length) {
            pos = ArrowRight(t, pos);
            visited.Add(pos);
        }
        // Should visit: 0, 1, 2, 3, 4, 5, 6
        // Content: a(0), b(1), end-of-line(2), c(3), d(4), end-of-line(5), end-of-doc(6)
        // Note: offset 2 is the \n position but content end is at 2.
        // ArrowRight from 2: ofs=3. line=1 (LineFromOfs(3)=1), lineStart=3, contentEnd=5. 3 <= 5 → no snap. Returns 3.
        Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5, 6 }, visited);
    }

    [Fact]
    public void Nav_WalkEntireDocument_CRLF() {
        // Walk through "ab\r\ncd" with arrow right
        var t = MakeTable("ab\r\ncd");
        var visited = new List<long>();
        var pos = 0L;
        visited.Add(pos);
        while (pos < t.Length) {
            pos = ArrowRight(t, pos);
            visited.Add(pos);
        }
        // Should visit: 0, 1, 2, 4, 5, 6
        // Offset 3 (\n) is skipped along with offset 2→4 jump
        // Wait: ArrowRight from 1 → 2 (content end). ArrowRight from 2 → 3. SnapForward(3):
        // line=0 (LineFromOfs(3)=0 because line 0 has fullLen 4, prefix=4, target=4 → line 0).
        // contentEnd = 0 + 2 = 2. ofs=3 > 2 → snap forward to LineStartOfs(1) = 4.
        Assert.Equal(new long[] { 0, 1, 2, 4, 5, 6 }, visited);
    }

// -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Document MakeDoc(string content) => new Document(content);
}
