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
    // Helpers
    // -------------------------------------------------------------------------

    private static Document MakeDoc(string content) => new Document(content);
}
