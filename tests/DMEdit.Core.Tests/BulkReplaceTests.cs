using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;

namespace DMEdit.Core.Tests;

public class BulkReplaceTests {
    // =====================================================================
    // PieceTable — Uniform bulk replace
    // =====================================================================

    [Fact]
    public void Uniform_SingleMatch() {
        var t = new PieceTable("hello world");
        t.BulkReplace([5], 1, "-");
        Assert.Equal("hello-world", t.GetText());
    }

    [Fact]
    public void Uniform_MultipleMatches() {
        var t = new PieceTable("aXbXcXd");
        t.BulkReplace([1, 3, 5], 1, "Y");
        Assert.Equal("aYbYcYd", t.GetText());
    }

    [Fact]
    public void Uniform_AdjacentMatches() {
        var t = new PieceTable("AABBCC");
        t.BulkReplace([0, 2, 4], 2, "x");
        Assert.Equal("xxx", t.GetText());
    }

    [Fact]
    public void Uniform_MatchAtStart() {
        var t = new PieceTable("XXhello");
        t.BulkReplace([0], 2, "YY");
        Assert.Equal("YYhello", t.GetText());
    }

    [Fact]
    public void Uniform_MatchAtEnd() {
        var t = new PieceTable("helloXX");
        t.BulkReplace([5], 2, "YY");
        Assert.Equal("helloYY", t.GetText());
    }

    [Fact]
    public void Uniform_EmptyReplacement_BulkDelete() {
        var t = new PieceTable("a-b-c");
        t.BulkReplace([1, 3], 1, "");
        Assert.Equal("abc", t.GetText());
    }

    [Fact]
    public void Uniform_LongerReplacement() {
        var t = new PieceTable("a.b.c");
        t.BulkReplace([1, 3], 1, "---");
        Assert.Equal("a---b---c", t.GetText());
    }

    [Fact]
    public void Uniform_ShorterReplacement() {
        var t = new PieceTable("a---b---c");
        t.BulkReplace([1, 5], 3, ".");
        Assert.Equal("a.b.c", t.GetText());
    }

    [Fact]
    public void Uniform_EmptyMatchList_NoOp() {
        var t = new PieceTable("hello");
        t.BulkReplace([], 1, "x");
        Assert.Equal("hello", t.GetText());
    }

    [Fact]
    public void Uniform_EntireDocument() {
        var t = new PieceTable("abc");
        t.BulkReplace([0], 3, "xyz");
        Assert.Equal("xyz", t.GetText());
    }

    // =====================================================================
    // PieceTable — Varying bulk replace
    // =====================================================================

    [Fact]
    public void Varying_DifferentLengths() {
        var t = new PieceTable("aXXbYc");
        t.BulkReplace(
            [(0, 1), (1, 2), (3, 1), (4, 1)],
            ["A", "BB", "B", "Y"]);
        Assert.Equal("ABBBYc", t.GetText());
    }

    [Fact]
    public void Varying_DifferentReplacements() {
        var t = new PieceTable("a b c");
        t.BulkReplace(
            [(1, 1), (3, 1)],
            ["-", "+"]);
        Assert.Equal("a-b+c", t.GetText());
    }

    [Fact]
    public void Varying_EmptyMatchList_NoOp() {
        var t = new PieceTable("hello");
        t.BulkReplace(
            Array.Empty<(long, int)>(),
            Array.Empty<string>());
        Assert.Equal("hello", t.GetText());
    }

    // =====================================================================
    // Line tree correctness
    // =====================================================================

    [Fact]
    public void Uniform_LineTreeCorrect_AfterReplace() {
        var t = new PieceTable("aaa\nbbb\nccc\n");
        // Replace "bbb" with "BBBBB"
        t.BulkReplace([4], 3, "BBBBB");
        Assert.Equal("aaa\nBBBBB\nccc\n", t.GetText());
        Assert.Equal(4, t.LineCount);
        Assert.Equal(0, t.LineStartOfs(0));
        Assert.Equal(4, t.LineStartOfs(1));
        Assert.Equal(10, t.LineStartOfs(2));
    }

    [Fact]
    public void Uniform_LineTreeCorrect_NewlineInReplacement() {
        var t = new PieceTable("a.b.c");
        t.BulkReplace([1, 3], 1, "\n");
        Assert.Equal("a\nb\nc", t.GetText());
        Assert.Equal(3, t.LineCount);
    }

    [Fact]
    public void Uniform_LineTreeCorrect_RemoveNewlines() {
        var t = new PieceTable("a\nb\nc");
        t.BulkReplace([1, 3], 1, "");
        Assert.Equal("abc", t.GetText());
        Assert.Equal(1, t.LineCount);
    }

    // =====================================================================
    // Document-level — Undo/Redo
    // =====================================================================

    [Fact]
    public void Document_UniformBulkReplace_Undo() {
        var doc = new Document("hello world hello world");
        doc.BulkReplaceUniform([0, 12], 5, "hi");
        Assert.Equal("hi world hi world", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("hello world hello world", doc.Table.GetText());
    }

    [Fact]
    public void Document_UniformBulkReplace_Redo() {
        var doc = new Document("hello world hello world");
        doc.BulkReplaceUniform([0, 12], 5, "hi");
        doc.Undo();
        doc.Redo();
        Assert.Equal("hi world hi world", doc.Table.GetText());
    }

    [Fact]
    public void Document_VaryingBulkReplace_Undo() {
        var doc = new Document("aXXbYc");
        doc.BulkReplaceVarying(
            [(1, 2), (4, 1)],
            ["--", "+"]);
        Assert.Equal("a--b+c", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("aXXbYc", doc.Table.GetText());
    }

    [Fact]
    public void Document_VaryingBulkReplace_Redo() {
        var doc = new Document("aXXbYc");
        doc.BulkReplaceVarying(
            [(1, 2), (4, 1)],
            ["--", "+"]);
        doc.Undo();
        doc.Redo();
        Assert.Equal("a--b+c", doc.Table.GetText());
    }

    [Fact]
    public void Document_BulkReplace_MultipleUndoRedo() {
        var doc = new Document("abc");
        doc.BulkReplaceUniform([0, 1, 2], 1, "X");
        Assert.Equal("XXX", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("abc", doc.Table.GetText());
        doc.Redo();
        Assert.Equal("XXX", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("abc", doc.Table.GetText());
    }

    [Fact]
    public void Document_BulkReplace_ThenNormalEdit_Undo() {
        var doc = new Document("aXbXc");
        doc.BulkReplaceUniform([1, 3], 1, "Y");
        Assert.Equal("aYbYc", doc.Table.GetText());
        doc.Selection = new Selection(5, 5);
        doc.Insert("!");
        Assert.Equal("aYbYc!", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("aYbYc", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("aXbXc", doc.Table.GetText());
    }

    // =====================================================================
    // Document-level — ConvertIndentation via BulkReplace
    // =====================================================================

    [Fact]
    public void ConvertIndentation_TabsToSpaces_ViaBulk() {
        var doc = new Document("\tfoo\n\t\tbar\nbaz\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        Assert.Equal("    foo\n        bar\nbaz\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_SpacesToTabs_ViaBulk() {
        var doc = new Document("    foo\n        bar\nbaz\n");
        doc.ConvertIndentation(IndentStyle.Tabs, tabSize: 4);
        Assert.Equal("\tfoo\n\t\tbar\nbaz\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_Undo_ViaBulk() {
        var doc = new Document("\tfoo\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        Assert.Equal("    foo\n", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("\tfoo\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_Redo_ViaBulk() {
        var doc = new Document("\tfoo\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        doc.Undo();
        doc.Redo();
        Assert.Equal("    foo\n", doc.Table.GetText());
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Fact]
    public void Uniform_AfterPriorEdits() {
        // Ensure bulk replace works on a table that already has add-buffer content.
        var t = new PieceTable("hello");
        t.Insert(5, " world");
        Assert.Equal("hello world", t.GetText());
        t.BulkReplace([5], 1, "-");
        Assert.Equal("hello-world", t.GetText());
    }

    [Fact]
    public void Uniform_LargeMatchCount() {
        // 1000 matches to verify the algorithm handles many matches.
        var text = string.Join(".", Enumerable.Repeat("x", 1001));
        var t = new PieceTable(text);
        var positions = new long[1000];
        for (var i = 0; i < 1000; i++) {
            positions[i] = i * 2 + 1; // position of each '.'
        }
        t.BulkReplace(positions, 1, "-");
        Assert.Equal(string.Join("-", Enumerable.Repeat("x", 1001)), t.GetText());
    }

    [Fact]
    public void Varying_WholeDocumentReplace() {
        var t = new PieceTable("abcdef");
        t.BulkReplace([(0, 6)], ["REPLACED"]);
        Assert.Equal("REPLACED", t.GetText());
    }

    // =====================================================================
    // PieceTable — BulkReplace precondition validation
    //
    // Both overloads document "matches must be sorted ascending and
    // non-overlapping" but historically did not enforce it.  All current
    // callers (Find/Replace All, ConvertIndentation) produce sorted
    // non-overlapping matches by construction, so the precondition was
    // safe in practice — but a future caller (e.g. multi-cursor "replace
    // at every cursor") could silently corrupt the document.  These tests
    // pin the validation contract so the bug class can't reappear.
    // =====================================================================

    [Fact]
    public void Uniform_UnsortedPositions_Throws() {
        var t = new PieceTable("abcdefghij");
        var ex = Assert.Throws<ArgumentException>(
            () => t.BulkReplace([5, 1, 8], 1, "X"));
        // Document must be untouched after the rejected call.
        Assert.Equal("abcdefghij", t.GetText());
        Assert.Contains("sorted", ex.Message);
    }

    [Fact]
    public void Uniform_OverlappingPositions_Throws() {
        var t = new PieceTable("abcdefghij");
        // matchLen=3 means the match at 0 covers [0..3); the next match at
        // 2 would start inside the first match.
        var ex = Assert.Throws<ArgumentException>(
            () => t.BulkReplace([0, 2], 3, "X"));
        Assert.Equal("abcdefghij", t.GetText());
        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public void Uniform_AdjacentPositions_AreLegal() {
        // Adjacent matches: end of one == start of next.  Not overlapping.
        var t = new PieceTable("aabbccdd");
        t.BulkReplace([0, 2, 4, 6], 2, "X");
        Assert.Equal("XXXX", t.GetText());
    }

    [Fact]
    public void Uniform_SinglePosition_NoValidationNeeded() {
        var t = new PieceTable("abcdef");
        t.BulkReplace([2], 2, "ZZ");
        Assert.Equal("abZZef", t.GetText());
    }

    [Fact]
    public void Varying_UnsortedPositions_Throws() {
        var t = new PieceTable("abcdefghij");
        var ex = Assert.Throws<ArgumentException>(
            () => t.BulkReplace([(5, 1), (1, 1), (8, 1)],
                                ["X", "Y", "Z"]));
        Assert.Equal("abcdefghij", t.GetText());
        Assert.Contains("sorted", ex.Message);
    }

    [Fact]
    public void Varying_OverlappingPositions_Throws() {
        var t = new PieceTable("abcdefghij");
        // First match [0..4), second match starts at 3 — overlap.
        var ex = Assert.Throws<ArgumentException>(
            () => t.BulkReplace([(0, 4), (3, 2)], ["X", "Y"]));
        Assert.Equal("abcdefghij", t.GetText());
        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public void Varying_AdjacentPositions_AreLegal() {
        var t = new PieceTable("aabbccdd");
        t.BulkReplace([(0, 2), (2, 2), (4, 2), (6, 2)],
                      ["W", "X", "Y", "Z"]);
        Assert.Equal("WXYZ", t.GetText());
    }

    [Fact]
    public void Varying_MismatchedArrayLengths_Throws() {
        // Defensive precondition: matches and replacements must have equal
        // length.  Without this check the caller would get an obscure
        // IndexOutOfRangeException deep inside BulkReplaceCore.
        var t = new PieceTable("abcdefghij");
        Assert.Throws<ArgumentException>(
            () => t.BulkReplace([(0, 1), (2, 1), (4, 1)], ["X", "Y"]));
        Assert.Equal("abcdefghij", t.GetText());
    }

    [Fact]
    public void Uniform_EmptyPositions_NoOp() {
        // Empty match list short-circuits before validation runs.
        var t = new PieceTable("abcdef");
        t.BulkReplace([], 1, "X");
        Assert.Equal("abcdef", t.GetText());
    }

    [Fact]
    public void Varying_EmptyMatches_NoOp() {
        var t = new PieceTable("abcdef");
        t.BulkReplace([], []);
        Assert.Equal("abcdef", t.GetText());
    }
}
