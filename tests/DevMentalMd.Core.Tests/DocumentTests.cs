using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Tests;

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
}
