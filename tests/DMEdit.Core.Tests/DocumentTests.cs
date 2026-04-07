using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

/// <summary>
/// Tests for <see cref="Document"/>-level operations: DeleteLine, SelectWord,
/// TransformCase, MoveLineUp/Down.
/// </summary>
public class DocumentTests {
    private static Document MakeDoc(string content) => new(content);

    // =====================================================================
    // DeleteLine
    // =====================================================================

    [Fact]
    public void DeleteLine_MiddleLine_RemovesLineAndNewline() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(5); // middle of "bbb"
        doc.DeleteLine();
        Assert.Equal("aaa\nccc", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_FirstLine_RemovesLineAndNewline() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(1); // middle of "aaa"
        doc.DeleteLine();
        Assert.Equal("bbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_LastLine_RemovesLineAndPrecedingNewline() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(9); // middle of "ccc"
        doc.DeleteLine();
        Assert.Equal("aaa\nbbb", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_OnlyLine_EmptiesDocument() {
        var doc = MakeDoc("hello");
        doc.Selection = Selection.Collapsed(2);
        doc.DeleteLine();
        Assert.Equal("", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_EmptyDocument_NoOp() {
        var doc = MakeDoc("");
        doc.DeleteLine();
        Assert.Equal("", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_ThenUndo_RestoresOriginal() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(5);
        doc.DeleteLine();
        Assert.Equal("aaa\nccc", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("aaa\nbbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_CRLF_RemovesEntireNewlineSequence() {
        var doc = MakeDoc("aaa\r\nbbb\r\nccc");
        doc.Selection = Selection.Collapsed(6); // middle of "bbb"
        doc.DeleteLine();
        Assert.Equal("aaa\r\nccc", doc.Table.GetText());
    }

    [Fact]
    public void DeleteLine_LastLine_CRLF_RemovesPrecedingCRLF() {
        var doc = MakeDoc("aaa\r\nbbb");
        doc.Selection = Selection.Collapsed(6); // in "bbb"
        doc.DeleteLine();
        Assert.Equal("aaa", doc.Table.GetText());
    }

    // =====================================================================
    // SelectWord
    // =====================================================================

    [Fact]
    public void SelectWord_OnWordMiddle_SelectsEntireWord() {
        var doc = MakeDoc("hello world");
        doc.Selection = Selection.Collapsed(2); // in "hello"
        doc.SelectWord();
        Assert.Equal(0L, doc.Selection.Start);
        Assert.Equal(5L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_OnWordStart_SelectsWord() {
        var doc = MakeDoc("hello world");
        doc.Selection = Selection.Collapsed(0);
        doc.SelectWord();
        Assert.Equal(0L, doc.Selection.Start);
        Assert.Equal(5L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_OnWhitespace_NoOp() {
        var doc = MakeDoc("hello   world");
        doc.Selection = Selection.Collapsed(6); // in the spaces
        doc.SelectWord();
        // Caret on whitespace: expansion stops immediately (no non-whitespace to reach).
        Assert.Equal(6L, doc.Selection.Start);
        Assert.Equal(6L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_OnPunctuation_NoOp() {
        var doc = MakeDoc("foo...bar");
        doc.Selection = Selection.Collapsed(4); // in "..."
        doc.SelectWord();
        // Caret on non-alphanumeric: expansion stops immediately.
        Assert.Equal(4L, doc.Selection.Start);
        Assert.Equal(4L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_EmptyDocument_NoOp() {
        var doc = MakeDoc("");
        doc.SelectWord();
        Assert.True(doc.Selection.IsEmpty);
    }

    [Fact]
    public void SelectWord_SingleCharWord_SelectsSingleChar() {
        var doc = MakeDoc("a b c");
        doc.Selection = Selection.Collapsed(0);
        doc.SelectWord();
        Assert.Equal(0L, doc.Selection.Start);
        Assert.Equal(1L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_WithUnderscores_TreatsAsWordChar() {
        var doc = MakeDoc("hello_world foo");
        doc.Selection = Selection.Collapsed(3); // in "hello_world"
        doc.SelectWord();
        Assert.Equal(0L, doc.Selection.Start);
        Assert.Equal(11L, doc.Selection.End);
    }

    [Fact]
    public void SelectWord_AtEndOfDocument_SelectsLastWord() {
        var doc = MakeDoc("hello world");
        doc.Selection = Selection.Collapsed(11); // at very end
        doc.SelectWord();
        Assert.Equal(6L, doc.Selection.Start);
        Assert.Equal(11L, doc.Selection.End);
    }

    // =====================================================================
    // TransformCase
    // =====================================================================

    [Fact]
    public void TransformCase_Upper_ConvertsToUpperCase() {
        var doc = MakeDoc("hello world");
        doc.Selection = new Selection(0, 5);
        doc.TransformCase(CaseTransform.Upper);
        Assert.Equal("HELLO world", doc.Table.GetText());
    }

    [Fact]
    public void TransformCase_Lower_ConvertsToLowerCase() {
        var doc = MakeDoc("HELLO WORLD");
        doc.Selection = new Selection(0, 5);
        doc.TransformCase(CaseTransform.Lower);
        Assert.Equal("hello WORLD", doc.Table.GetText());
    }

    [Fact]
    public void TransformCase_Proper_CapitalizesFirstLetters() {
        var doc = MakeDoc("hello world foo");
        doc.Selection = new Selection(0, 15);
        doc.TransformCase(CaseTransform.Proper);
        Assert.Equal("Hello World Foo", doc.Table.GetText());
    }

    [Fact]
    public void TransformCase_EmptySelection_NoOp() {
        var doc = MakeDoc("hello");
        doc.Selection = Selection.Collapsed(2);
        doc.TransformCase(CaseTransform.Upper);
        Assert.Equal("hello", doc.Table.GetText());
    }

    [Fact]
    public void TransformCase_AlreadyCorrectCase_NoOp() {
        var doc = MakeDoc("HELLO");
        doc.Selection = new Selection(0, 5);
        doc.TransformCase(CaseTransform.Upper);
        // Text unchanged, no edit pushed
        Assert.Equal("HELLO", doc.Table.GetText());
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void TransformCase_ThenUndo_RestoresOriginal() {
        var doc = MakeDoc("hello world");
        doc.Selection = new Selection(0, 5);
        doc.TransformCase(CaseTransform.Upper);
        Assert.Equal("HELLO world", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("hello world", doc.Table.GetText());
    }

    [Fact]
    public void TransformCase_PreservesSelection() {
        var doc = MakeDoc("hello world");
        doc.Selection = new Selection(0, 5);
        doc.TransformCase(CaseTransform.Upper);
        Assert.Equal(0L, doc.Selection.Start);
        Assert.Equal(5L, doc.Selection.End);
    }

    [Fact]
    public void TransformCase_Proper_HandlesDashesAndUnderscores() {
        var doc = MakeDoc("hello-world_foo");
        doc.Selection = new Selection(0, 15);
        doc.TransformCase(CaseTransform.Proper);
        Assert.Equal("Hello-World_Foo", doc.Table.GetText());
    }

    // =====================================================================
    // MoveLineUp / MoveLineDown
    // =====================================================================

    [Fact]
    public void MoveLineUp_MiddleLine_SwapsWithAbove() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(5); // in "bbb"
        doc.MoveLineUp();
        Assert.Equal("bbb\naaa\nccc", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineDown_MiddleLine_SwapsWithBelow() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(5); // in "bbb"
        doc.MoveLineDown();
        Assert.Equal("aaa\nccc\nbbb", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineUp_FirstLine_NoOp() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(1); // in "aaa"
        doc.MoveLineUp();
        Assert.Equal("aaa\nbbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineDown_LastLine_NoOp() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(9); // in "ccc"
        doc.MoveLineDown();
        Assert.Equal("aaa\nbbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineUp_LastLine_HandlesNoTrailingNewline() {
        var doc = MakeDoc("aaa\nbbb");
        doc.Selection = Selection.Collapsed(5); // in "bbb"
        doc.MoveLineUp();
        Assert.Equal("bbb\naaa", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineDown_FirstLine_HandlesNoTrailingNewline() {
        var doc = MakeDoc("aaa\nbbb");
        doc.Selection = Selection.Collapsed(1); // in "aaa"
        doc.MoveLineDown();
        Assert.Equal("bbb\naaa", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineUp_MultiLineSelection_MovesAllLines() {
        var doc = MakeDoc("aaa\nbbb\nccc\nddd");
        doc.Selection = new Selection(4, 10); // spans "bbb" and "ccc"
        doc.MoveLineUp();
        Assert.Equal("bbb\nccc\naaa\nddd", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineDown_MultiLineSelection_MovesAllLines() {
        var doc = MakeDoc("aaa\nbbb\nccc\nddd");
        doc.Selection = new Selection(4, 10); // spans "bbb" and "ccc"
        doc.MoveLineDown();
        Assert.Equal("aaa\nddd\nbbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineUp_ThenUndo_RestoresOriginal() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = Selection.Collapsed(5);
        doc.MoveLineUp();
        Assert.Equal("bbb\naaa\nccc", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("aaa\nbbb\nccc", doc.Table.GetText());
    }

    [Fact]
    public void MoveLineDown_PreservesSelectionOnMovedText() {
        var doc = MakeDoc("aaa\nbbb\nccc");
        doc.Selection = new Selection(4, 7); // "bbb" fully selected
        doc.MoveLineDown();
        // "bbb" is now on line 2 (after "ccc\n"), selection should follow
        var selectedText = doc.Table.GetText(doc.Selection.Start, (int)doc.Selection.Len);
        Assert.Equal("bbb", selectedText);
    }

    // =====================================================================
    // Surrogate handling — never retain a lone half of a surrogate pair
    // =====================================================================

    // U+1F600 GRINNING FACE — encoded in UTF-16 as the surrogate pair D83D DE00.
    private const string Emoji = "\uD83D\uDE00";

    [Fact]
    public void SanitizeSurrogates_PassesValidPairThrough() {
        Assert.Same(Emoji, Document.SanitizeSurrogates(Emoji));
    }

    [Fact]
    public void SanitizeSurrogates_ReplacesLoneHighSurrogate() {
        Assert.Equal("a\uFFFDb", Document.SanitizeSurrogates("a\uD83Db"));
    }

    [Fact]
    public void SanitizeSurrogates_ReplacesLoneLowSurrogate() {
        Assert.Equal("a\uFFFDb", Document.SanitizeSurrogates("a\uDE00b"));
    }

    [Fact]
    public void SanitizeSurrogates_ReplacesTrailingLoneHighSurrogate() {
        Assert.Equal("ab\uFFFD", Document.SanitizeSurrogates("ab\uD83D"));
    }

    [Fact]
    public void Insert_LoneHighSurrogate_StoredAsReplacementChar() {
        var doc = MakeDoc("");
        doc.Insert("a\uD83Db");
        Assert.Equal("a\uFFFDb", doc.Table.GetText());
    }

    [Fact]
    public void DeleteBackward_AtSurrogatePair_RemovesBothHalves() {
        var doc = MakeDoc("a" + Emoji + "b");
        // Caret immediately after the emoji (offset 3 in UTF-16 char units).
        doc.Selection = Selection.Collapsed(3);
        doc.DeleteBackward();
        Assert.Equal("ab", doc.Table.GetText());
    }

    [Fact]
    public void DeleteForward_AtSurrogatePair_RemovesBothHalves() {
        var doc = MakeDoc("a" + Emoji + "b");
        doc.Selection = Selection.Collapsed(1); // just before the emoji
        doc.DeleteForward();
        Assert.Equal("ab", doc.Table.GetText());
    }

    [Fact]
    public void CodepointBoundary_StepRight_OverPair_AdvancesTwo() {
        var doc = MakeDoc("a" + Emoji + "b");
        Assert.Equal(3, CodepointBoundary.StepRight(doc.Table, 1));
    }

    [Fact]
    public void CodepointBoundary_StepLeft_OverPair_RetreatsTwo() {
        var doc = MakeDoc("a" + Emoji + "b");
        Assert.Equal(1, CodepointBoundary.StepLeft(doc.Table, 3));
    }

    [Fact]
    public void CodepointBoundary_WidthAt_BmpAndPair() {
        var doc = MakeDoc("a" + Emoji + "b");
        Assert.Equal(1, CodepointBoundary.WidthAt(doc.Table, 0));
        Assert.Equal(2, CodepointBoundary.WidthAt(doc.Table, 1));
        Assert.Equal(1, CodepointBoundary.WidthAt(doc.Table, 3));
        Assert.Equal(0, CodepointBoundary.WidthAt(doc.Table, 4));
    }

    [Fact]
    public void DeleteBackward_AtSurrogatePair_UndoRestoresPair() {
        var doc = MakeDoc("a" + Emoji + "b");
        doc.Selection = Selection.Collapsed(3);
        doc.DeleteBackward();
        doc.Undo();
        Assert.Equal("a" + Emoji + "b", doc.Table.GetText());
    }

    [Fact]
    public void SelectWord_StopsAtEmojiBoundary_PairAligned() {
        // Emoji is "Other Symbol" in Unicode — it is a word boundary.
        // The key invariant is that the boundary lands at a pair-aligned
        // offset (3 or 5), never at 4 (inside the pair).
        var doc = MakeDoc("foo" + Emoji + "bar");
        doc.Selection = Selection.Collapsed(0);
        doc.SelectWord();
        Assert.Equal(0, doc.Selection.Start);
        Assert.Equal(3, doc.Selection.End);
        // Now select the word AFTER the emoji — boundary must again be
        // pair-aligned (5, not 4).
        doc.Selection = Selection.Collapsed(6);
        doc.SelectWord();
        Assert.Equal(5, doc.Selection.Start);
        Assert.Equal(8, doc.Selection.End);
    }

    [Fact]
    public void ExpandSelection_Whitespace_StopsAtPairBoundary() {
        // "foo🙂bar" has no whitespace — whitespace expansion should cover
        // the whole thing with pair-aligned endpoints (0 and 8), never
        // stopping at 4 (mid-pair).
        var doc = MakeDoc("foo" + Emoji + "bar");
        doc.Selection = Selection.Collapsed(1);
        doc.ExpandSelection(ExpandSelectionMode.Word);
        Assert.Equal(0, doc.Selection.Start);
        Assert.Equal(8, doc.Selection.End);
    }

    [Fact]
    public void ExpandSelection_SubwordFirst_NeverStopsMidPair() {
        // Old subword code treated a mid-pair position as a boundary
        // (both halves are non-alphanumeric → boundary). The fix must
        // never leave a selection with an odd (mid-pair) offset.
        var doc = MakeDoc("foo" + Emoji + "Bar");
        doc.Selection = Selection.Collapsed(1);
        doc.ExpandSelection(ExpandSelectionMode.SubwordFirst);
        // Both endpoints must not split the pair at offsets 3-4.
        Assert.NotEqual(4, doc.Selection.Start);
        Assert.NotEqual(4, doc.Selection.End);
    }

    [Fact]
    public void CodepointBoundary_SnapToBoundary_MidPair_SnapsForward() {
        var doc = MakeDoc("a" + Emoji + "b");
        // Offset 2 is mid-pair (between high at 1 and low at 2).
        Assert.Equal(3, CodepointBoundary.SnapToBoundary(doc.Table, 2, forward: true));
    }

    [Fact]
    public void CodepointBoundary_SnapToBoundary_MidPair_SnapsBackward() {
        var doc = MakeDoc("a" + Emoji + "b");
        Assert.Equal(1, CodepointBoundary.SnapToBoundary(doc.Table, 2, forward: false));
    }

    [Fact]
    public void CodepointBoundary_SnapToBoundary_OnBoundary_Noop() {
        var doc = MakeDoc("a" + Emoji + "b");
        Assert.Equal(1, CodepointBoundary.SnapToBoundary(doc.Table, 1, forward: true));
        Assert.Equal(3, CodepointBoundary.SnapToBoundary(doc.Table, 3, forward: true));
    }

    [Fact]
    public void ColumnSelection_OfsToCol_MidPair_SnapsBackward() {
        var doc = MakeDoc("a" + Emoji + "b");
        // Offset 2 is mid-pair; OfsToCol should snap to 1 and return col 1.
        Assert.Equal(1, ColumnSelection.OfsToCol(doc.Table, 2, 4));
    }

    [Fact]
    public void ColumnSelection_ColToCharIdx_MidPairCol_SnapsForward() {
        var doc = MakeDoc("a" + Emoji + "b");
        // Column 2 (the low-surrogate position in per-UTF16 counting)
        // should not land on the low half — snap forward to after the pair.
        var charIdx = ColumnSelection.ColToCharIdx(doc.Table, 0, 4, 2, 4);
        Assert.Equal(3, charIdx);
    }
}
