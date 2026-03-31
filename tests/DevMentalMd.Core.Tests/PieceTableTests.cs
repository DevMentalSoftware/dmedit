using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Tests;

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
    // Pseudo-newlines (MaxPseudoLine)
    // -------------------------------------------------------------------------

    // Use M as shorthand for the default MaxPseudoLine (500) in test calculations.
    private static readonly int M = PieceTable.MaxPseudoLine;

    [Fact]
    public void PseudoNewline_LongLineSplitInBuildLineTree() {
        var content = new string('a', M * 2 + 1);
        var t = new PieceTable(content);
        Assert.Equal(3, t.LineCount);
        Assert.Equal(0L, t.LineStartOfs(0));
        Assert.Equal((long)M, t.LineStartOfs(1));
        Assert.Equal((long)M * 2, t.LineStartOfs(2));
    }

    [Fact]
    public void PseudoNewline_DocSpaceOffsets_DistinguishLineEndFromNextLineStart() {
        // 20 chars, MaxPseudoLine=M → two pseudo-lines of 10 each.
        var content = new string('a', M * 2);
        var t = new PieceTable(content);
        Assert.Equal(2, t.LineCount);

        // Buf-space: both lines start at M-char boundaries, no gap.
        Assert.Equal(0L, t.LineStartOfs(0));
        Assert.Equal((long)M, t.LineStartOfs(1));

        // Doc-space: pseudo-line 0 has virtual terminator, so line 1 starts at M+1.
        Assert.Equal(0L, t.DocLineStartOfs(0));
        Assert.Equal((long)M + 1, t.DocLineStartOfs(1));

        // DocLength includes the virtual terminator for pseudo-line 0.
        Assert.Equal(M * 2 + 1, t.DocLength);

        // End-key on line 0: docLineStart + contentLen = 0 + M = M.
        // This is the virtual terminator position — AFTER the last content char.
        var endOfLine0 = t.DocLineStartOfs(0) + t.LineContentLength(0);
        Assert.Equal((long)M, endOfLine0);

        // This position is DISTINCT from the start of line 1.
        var startOfLine1 = t.DocLineStartOfs(1);
        Assert.Equal((long)M + 1, startOfLine1);
        Assert.NotEqual(endOfLine0, startOfLine1); // THE KEY ASSERTION

        // LineFromDocOfs distinguishes them.
        Assert.Equal(0, t.LineFromDocOfs(endOfLine0));    // end of line 0
        Assert.Equal(1, t.LineFromDocOfs(startOfLine1));  // start of line 1

        // Translation round-trips.
        // End of line 0 (doc M) → buf M (start of next pseudo content).
        Assert.Equal((long)M, t.DocOfsToBufOfs(endOfLine0));
        // Start of line 1 (doc M+1) → buf M (same buf position, different doc meaning).
        Assert.Equal((long)M, t.DocOfsToBufOfs(startOfLine1));
    }

    [Fact]
    public void PseudoNewline_ExactMultipleProducesNoRemainder() {
        var content = new string('b', M * 2);
        var t = new PieceTable(content);
        Assert.Equal(2, t.LineCount);
    }

    [Fact]
    public void PseudoNewline_GetLineReturnsCorrectContent() {
        var content = new string('c', M + M / 2);
        var t = new PieceTable(content);
        Assert.Equal(2, t.LineCount);
        Assert.Equal(new string('c', M), t.GetLine(0));
        Assert.Equal(new string('c', M / 2), t.GetLine(1));
    }

    [Fact]
    public void PseudoNewline_LineFromOfsMapsThroughBoundaries() {
        var content = new string('d', M * 2 + M / 2);
        var t = new PieceTable(content);
        Assert.Equal(0L, t.LineFromOfs(0));
        Assert.Equal(0L, t.LineFromOfs(M - 1));
        Assert.Equal(1L, t.LineFromOfs(M));
        Assert.Equal(2L, t.LineFromOfs(M * 2));
    }

    [Fact]
    public void PseudoNewline_RealNewlinesMixedWithPseudo() {
        // (M + M/2) chars + \n + 5 chars.
        var content = new string('e', M + M / 2) + "\n" + "hello";
        var t = new PieceTable(content);
        // First real line splits into [M, M/2+1 (including \n)].
        // Second real line: [5].
        Assert.Equal(3, t.LineCount);
        Assert.Equal(new string('e', M), t.GetLine(0));
        Assert.Equal(new string('e', M / 2), t.GetLine(1));
        Assert.Equal("hello", t.GetLine(2));
    }

    [Fact]
    public void PseudoNewline_InsertSplitsLongLine() {
        // Start at exactly MaxPseudoLine, then insert to push it over.
        var content = new string('f', M);
        var t = new PieceTable(content);
        Assert.Equal(1, t.LineCount);
        t.Insert(M / 2, "X"); // now M+1 chars
        Assert.Equal(2, t.LineCount);
        Assert.Equal(M + 1, t.Length);
    }

    [Fact]
    public void PseudoNewline_DeleteMergesAndResplits() {
        var content = new string('g', M * 3);
        var t = new PieceTable(content);
        Assert.Equal(3, t.LineCount);
        // Delete M/2 chars crossing the first pseudo-boundary.
        var delStart = M - M / 4;
        var delLen = M / 2;
        t.Delete(delStart, delLen);
        Assert.Equal(M * 3 - delLen, t.Length);
        Assert.Equal(new string('g', M * 3 - delLen), t.GetText());
    }

    [Fact]
    public void PseudoNewline_InsertRealNewlineInPseudoLine() {
        var content = new string('h', M + M / 2);
        var t = new PieceTable(content);
        Assert.Equal(2, t.LineCount);
        // Insert a real newline at position M/2.
        t.Insert(M / 2, "\n");
        Assert.True(t.LineCount >= 3);
        Assert.Equal(M + M / 2 + 1, t.Length);
    }

    [Fact]
    public void PseudoNewline_UndoRedoPreservesContent() {
        var content = new string('i', M);
        var doc = MakeDoc(content);
        doc.Selection = Core.Documents.Selection.Collapsed(M);
        doc.Insert("X");
        Assert.Equal(M + 1, doc.Table.Length);
        doc.Undo();
        Assert.Equal(M, doc.Table.Length);
        Assert.Equal(content, doc.Table.GetText());
        doc.Redo();
        Assert.Equal(M + 1, doc.Table.Length);
        Assert.Equal(content + "X", doc.Table.GetText());
    }

    [Fact]
    public void PseudoNewline_ContentUnchangedAfterSplit() {
        var content = new string('j', M * 3 + M / 2);
        var t = new PieceTable(content);
        Assert.Equal(content, t.GetText());
    }

    [Fact]
    public void PseudoNewline_NoEntryExceedsMaxPseudoLine() {
        // A line with a real newline beyond MaxPseudoLine.
        var content = new string('x', M + 12) + "\n" + "end";
        var t = new PieceTable(content);
        var snapshot = t.SnapshotLineLengths();
        foreach (var len in snapshot) {
            Assert.True(len <= M,
                $"Line entry {len} exceeds MaxPseudoLine ({M})");
        }
        Assert.Equal(content, t.GetText());
    }

    [Fact]
    public void PseudoNewline_ContentNeverExceedsMaxPseudoLine_ExactBoundary() {
        // 500 content + \n = 501 total.  The tree entry is 501 but content
        // is only 500 — no pseudo-split needed.
        var content = new string('y', M) + "\n" + "end";
        var t = new PieceTable(content);
        // Line 0: 500 content + 1 LF = 501 entry, content = 500 = M (no split)
        Assert.Equal(M, t.LineContentLength(0));
        // Line 1: "end" = 3 content
        Assert.Equal(3, t.LineContentLength(1));
        Assert.Equal(content, t.GetText());
    }

    [Fact]
    public void PseudoNewline_NoEntryExceedsMaxPseudoLine_CrlfBeyondLimit() {
        var content = new string('z', M + 1) + "\r\n" + "end";
        var t = new PieceTable(content);
        var snapshot = t.SnapshotLineLengths();
        foreach (var len in snapshot) {
            Assert.True(len <= M,
                $"Line entry {len} exceeds MaxPseudoLine ({M})");
        }
        Assert.Equal(content, t.GetText());
    }

    [Fact]
    public void PseudoNewline_InstallLineTreeSplitsLongEntries() {
        var totalLen = M * 3;
        var t = new PieceTable(new string('q', totalLen));
        t.InstallLineTree([totalLen]);
        var snapshot = t.SnapshotLineLengths();
        foreach (var len in snapshot) {
            Assert.True(len <= M,
                $"InstallLineTree entry {len} exceeds MaxPseudoLine ({M})");
        }
        Assert.Equal(3, snapshot.Length);
    }

    [Fact]
    public void PseudoNewline_SingleLineFileNoNewlines() {
        var content = new string('m', M + 12);
        var t = new PieceTable(content);
        Assert.True(t.LineCount > 1, $"Expected pseudo-split but got {t.LineCount} line(s)");
        var snapshot = t.SnapshotLineLengths();
        foreach (var len in snapshot) {
            Assert.True(len <= M,
                $"Line entry {len} exceeds MaxPseudoLine ({M})");
        }
        for (var i = 0; i < t.LineCount; i++) {
            var line = t.GetLine(i);
            Assert.True(line.Length <= M);
        }
        Assert.Equal(content, t.GetText());
    }

    [Fact]
    public void PseudoNewline_ShortLinesUnaffected() {
        var t = new PieceTable("hello\nworld\n");
        Assert.Equal(3, t.LineCount);
        Assert.Equal("hello", t.GetLine(0));
        Assert.Equal("world", t.GetLine(1));
        Assert.Equal("", t.GetLine(2));
    }

    [Fact]
    public void PseudoNewline_GetLine_RealNewlines_StillWork() {
        // Verify GetLine still strips \r\n correctly after the CharAt fix.
        var t = new PieceTable("abc\r\ndef\ngh\r");
        Assert.Equal(4, t.LineCount);
        Assert.Equal("abc", t.GetLine(0));
        Assert.Equal("def", t.GetLine(1));
        Assert.Equal("gh", t.GetLine(2));
        Assert.Equal("", t.GetLine(3));
    }

    [Fact]
    public void PseudoNewline_GetLineNeverExceedsMaxGetTextLength() {
        // Simulates the session-restore scenario: a large single-line file
        // where LineCount/LineStartOfs must never serve an unsplit line that
        // exceeds MaxGetTextLength, even before the line tree is built.
        var content = new string('n', M * 5 + 17);
        var t = new PieceTable(content);
        // Every line's content must fit in GetText.
        for (var i = 0; i < t.LineCount; i++) {
            var start = t.LineStartOfs(i);
            var end = i + 1 < t.LineCount
                ? t.LineStartOfs(i + 1)
                : t.Length;
            var len = (int)(end - start);
            Assert.True(len <= t.MaxGetTextLength,
                $"Line {i}: length {len} exceeds MaxGetTextLength ({t.MaxGetTextLength})");
            // Also verify GetLine works without throwing.
            var line = t.GetLine(i);
            Assert.True(line.Length <= M);
        }
    }

    [Fact]
    public void PseudoNewline_ConsecutiveLineStartOfsNeverExceedsMaxPseudoLine() {
        // No two consecutive LineStartOfs values should differ by more than
        // MaxPseudoLine. Tests with various sizes including large single-line.
        foreach (var size in new[] { M - 1, M, M + 1, M * 3, M * 5 + 17 }) {
            var t = new PieceTable(new string('p', size));
            var lc = t.LineCount;
            for (long i = 0; i < lc; i++) {
                var start = t.LineStartOfs(i);
                var end = i + 1 < lc ? t.LineStartOfs(i + 1) : t.Length;
                var gap = end - start;
                Assert.True(gap <= M,
                    $"Size {size}, line {i}: gap {gap} exceeds MaxPseudoLine ({M})");
            }
            Assert.Equal(new string('p', size), t.GetText());
        }
    }

    // -------------------------------------------------------------------------
    // LineContentLength (with MaxPseudoLine = 10 for easy testing)
    // -------------------------------------------------------------------------

    private const int T = 10; // test MaxPseudoLine

    private static PieceTable MakeTable(string content) {
        return new PieceTable(content, T);
    }

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
    public void ContentLength_MPLplus1_NoTerminator_PseudoSplit() {
        // 11 chars → pseudo-split: [10] + [1]
        var t = MakeTable(new string('a', T + 1));
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));  // pseudo-line: 10 content, 0 dead zone
        Assert.Equal(1, t.LineContentLength(1));   // final: 1 content, no terminator
        Assert.Equal(LineTerminatorType.Pseudo, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(1));
    }

    [Fact]
    public void ContentLength_MPLplus1_LF() {
        // 11 content + LF → pseudo-split: [10] + [1 + LF] + [empty]
        var t = MakeTable(new string('a', T + 1) + "\n");
        Assert.Equal(3, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));   // pseudo
        Assert.Equal(1, t.LineContentLength(1));    // content + LF
        Assert.Equal(0, t.LineContentLength(2));    // empty final
        Assert.Equal(LineTerminatorType.Pseudo, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.LF, t.GetLineTerminator(1));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(2));
    }

    [Fact]
    public void ContentLength_MPLplus1_CRLF() {
        // 11 content + CRLF → pseudo-split: [10] + [1 + CRLF] + [empty]
        var t = MakeTable(new string('a', T + 1) + "\r\n");
        Assert.Equal(3, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));   // pseudo
        Assert.Equal(1, t.LineContentLength(1));    // content + CRLF
        Assert.Equal(0, t.LineContentLength(2));    // empty final
        Assert.Equal(LineTerminatorType.Pseudo, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.CRLF, t.GetLineTerminator(1));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(2));
    }

    [Fact]
    public void ContentLength_TwoMPL_NoTerminator() {
        // 20 chars → two pseudo-lines of 10
        var t = MakeTable(new string('a', T * 2));
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(T, t.LineContentLength(1));
        Assert.Equal(LineTerminatorType.Pseudo, t.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, t.GetLineTerminator(1));
    }

    [Fact]
    public void ContentLength_MPLminus1_CRLF() {
        // 9 content + CRLF = 11 total — content under MPL, no pseudo-split
        var t = MakeTable(new string('a', T - 1) + "\r\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T - 1, t.LineContentLength(0));
        Assert.Equal(LineTerminatorType.CRLF, t.GetLineTerminator(0));
    }

    [Fact]
    public void ContentLength_MPLminus2_CRLF() {
        // 8 content + CRLF = 10 total — exactly MPL including terminator
        var t = MakeTable(new string('a', T - 2) + "\r\n");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T - 2, t.LineContentLength(0));
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

    [Fact]
    public void ContentLength_LineStartOfs_ConsistentWithContent() {
        // Verify that LineStartOfs + LineContentLength gives the content end offset
        var content = new string('a', T + 5) + "\r\n" + new string('b', 3);
        var t = MakeTable(content);
        // Pseudo-split [10] + [5 + CRLF] + [3]
        Assert.Equal(3, t.LineCount);

        // Line 0: starts at 0, content length 10
        Assert.Equal(0L, t.LineStartOfs(0));
        Assert.Equal(T, t.LineContentLength(0));

        // Line 1: starts at 10, content length 5
        Assert.Equal((long)T, t.LineStartOfs(1));
        Assert.Equal(5, t.LineContentLength(1));

        // Line 2: starts at 10 + 5 + 2 (CRLF) = 17, content length 3
        Assert.Equal(17L, t.LineStartOfs(2));
        Assert.Equal(3, t.LineContentLength(2));
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
    public void Nav_Pseudo_ArrowRightNoDeadZone() {
        // 11 chars, MPL=10 → pseudo [10] + [1]. No dead zone.
        var t = MakeTable(new string('a', T + 1));
        // At offset 9, arrow right → 10 (start of pseudo-line 1, which is also content end of line 0)
        Assert.Equal(10L, ArrowRight(t, 9));
        // At offset 10, arrow right → 11 (end of doc)... wait, Length is 11
        // offset 10 is first char of line 1, arrow right → 11 (end of doc)
        // But Length check: ofs >= Length → return ofs. 11 >= 11 → return 11.
        // Actually ArrowRight clamps: Math.Min(10+1, 11) = 11. SnapForward(11): 11 >= 11 → return 11.
    }

    [Fact]
    public void Nav_Pseudo_EndKey() {
        // 11 chars, MPL=10 → pseudo [10] + [1]
        var t = MakeTable(new string('a', T + 1));
        // Caret at offset 0 (line 0), End → offset 10.
        // LineFromOfs(10) = line 1 (pseudo-boundary → next line).
        // This is correct: offset 10 IS the start of pseudo-line 1.
        Assert.Equal(10L, EndKey(t, 0));
        // From offset 10 (line 1), End → offset 11 (content end of line 1).
        Assert.Equal(11L, EndKey(t, 10));
    }

    [Fact]
    public void Nav_Pseudo_ArrowRightAcrossBoundary_CorrectChar() {
        // "abcdefghijK" with MPL=10 → pseudo [10] + [1]
        // Line 0: "abcdefghij", Line 1: "K"
        var t = MakeTable("abcdefghijK");
        // Offset 9 = 'j' (last char of line 0 content)
        Assert.Equal('j', t.CharAt(9));
        // Arrow right → offset 10 = 'K' (first char of line 1)
        var next = ArrowRight(t, 9);
        Assert.Equal(10L, next);
        Assert.Equal('K', t.CharAt(10));
    }

    [Fact]
    public void Nav_Pseudo_ArrowLeftAcrossBoundary_CorrectChar() {
        // "abcdefghijK" with MPL=10
        var t = MakeTable("abcdefghijK");
        // Offset 10 = 'K' (first char of line 1)
        Assert.Equal('K', t.CharAt(10));
        // Arrow left → offset 9 = 'j' (last char of line 0)
        var prev = ArrowLeft(t, 10);
        Assert.Equal(9L, prev);
        Assert.Equal('j', t.CharAt(9));
    }

    [Fact]
    public void Nav_Pseudo_ArrowRightLeft_RoundTrip() {
        // Arrow right then left should return to same position
        var t = MakeTable(new string('a', T + 1));
        for (long pos = 0; pos < t.Length; pos++) {
            var right = ArrowRight(t, pos);
            var back = ArrowLeft(t, right);
            Assert.Equal(pos, back);
        }
    }

    [Fact]
    public void Nav_Pseudo_TypingPastMPL_SplitsLine() {
        // Start with exactly MPL chars, type one more → should split
        var t = new PieceTable(new string('a', T), maxPseudoLine: T);
        Assert.Equal(1, t.LineCount);
        // Type one more char at the end
        t.Insert(T, "b");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(1, t.LineContentLength(1));
    }

    [Fact]
    public void Nav_Pseudo_TypingPastMPL_MiddleOfLine_SplitsCorrectly() {
        // Insert in the middle of a long line
        var t = new PieceTable(new string('a', T), maxPseudoLine: T);
        Assert.Equal(1, t.LineCount);
        // Insert at position 5 → line becomes 11 chars → should split
        t.Insert(5, "X");
        Assert.Equal(2, t.LineCount);
        Assert.Equal(T, t.LineContentLength(0));
        Assert.Equal(1, t.LineContentLength(1));
    }

    [Fact]
    public void Nav_Pseudo_EndKey_MovesToNextPseudoLine() {
        // End from line 0 puts caret at offset 10, which is the start of
        // pseudo-line 1.  This is correct — pseudo-lines are real lines.
        var t = MakeTable(new string('a', T + 1));
        var startLine = t.LineFromOfs(0);
        Assert.Equal(0L, startLine);
        var endPos = EndKey(t, 0);
        Assert.Equal(10L, endPos);
        // Offset 10 is line 1 (pseudo-line boundary).
        var endLine = t.LineFromOfs(endPos);
        Assert.Equal(1L, endLine);
    }

    [Fact]
    public void Nav_Pseudo_EndThenEnd_AdvancesToNextLineEnd() {
        // End from line 0 → offset 10 (line 1 start).
        // End again from line 1 → offset 11 (line 1 end).
        // End again from line 1 → offset 11 (idempotent).
        var t = MakeTable(new string('a', T + 1));
        var pos1 = EndKey(t, 0);
        Assert.Equal(10L, pos1);
        var pos2 = EndKey(t, pos1);
        Assert.Equal(11L, pos2);
        var pos3 = EndKey(t, pos2);
        Assert.Equal(pos2, pos3); // idempotent on last line
    }

    [Fact]
    public void Nav_Pseudo_HomeKey() {
        // 11 chars, MPL=10 → pseudo [10] + [1]
        var t = MakeTable(new string('a', T + 1));
        // Caret at offset 5 (line 0), Home → offset 0
        Assert.Equal(0L, HomeKey(t, 5));
        // Caret at offset 10 (line 1), Home → offset 10
        Assert.Equal(10L, HomeKey(t, 10));
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

    [Fact]
    public void Nav_WalkEntireDocument_Pseudo() {
        // Walk through 12 chars, MPL=10 → pseudo [10] + [2]
        var t = MakeTable(new string('a', T + 2));
        var visited = new List<long>();
        var pos = 0L;
        visited.Add(pos);
        while (pos < t.Length) {
            pos = ArrowRight(t, pos);
            visited.Add(pos);
        }
        // Every offset 0..12 should be visited — no dead zones for pseudo-lines
        var expected = Enumerable.Range(0, T + 3).Select(i => (long)i).ToArray();
        Assert.Equal(expected, visited);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Document MakeDoc(string content) => new Document(content);
}
