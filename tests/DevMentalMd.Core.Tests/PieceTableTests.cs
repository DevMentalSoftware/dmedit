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

    // Use M as shorthand for MaxPseudoLine in test calculations.
    private const int M = PieceTable.MaxPseudoLine;

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
    public void PseudoNewline_NoEntryExceedsMaxPseudoLine_ExactBoundary() {
        var content = new string('y', M) + "\n" + "end";
        var t = new PieceTable(content);
        var snapshot = t.SnapshotLineLengths();
        foreach (var len in snapshot) {
            Assert.True(len <= M,
                $"Line entry {len} exceeds MaxPseudoLine ({M})");
        }
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
            Assert.True(len <= PieceTable.MaxGetTextLength,
                $"Line {i}: length {len} exceeds MaxGetTextLength ({PieceTable.MaxGetTextLength})");
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
    // Helpers
    // -------------------------------------------------------------------------

    private static Document MakeDoc(string content) => new Document(content);
}
