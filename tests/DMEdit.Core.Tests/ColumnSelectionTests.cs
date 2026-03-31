using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

/// <summary>
/// Tests for <see cref="ColumnSelection"/> materialization, column math,
/// and <see cref="Document"/> multi-cursor edit operations.
/// </summary>
public class ColumnSelectionTests {
    private static Document MakeDoc(string content) => new(content);
    private const int Tab = 4;

    // =====================================================================
    // ColToCharIdx / OfsToCol — no tabs
    // =====================================================================

    [Fact]
    public void ColToCharIdx_NoTabs_ReturnsCharIndex() {
        var table = new PieceTable("abcdef");
        var lineStart = 0L;
        Assert.Equal(0, ColumnSelection.ColToCharIdx(table, lineStart, 6, 0, Tab));
        Assert.Equal(3, ColumnSelection.ColToCharIdx(table, lineStart, 6, 3, Tab));
        Assert.Equal(6, ColumnSelection.ColToCharIdx(table, lineStart, 6, 10, Tab)); // beyond end → clamped
    }

    [Fact]
    public void OfsToCol_NoTabs_ReturnsColumn() {
        var table = new PieceTable("abc\ndef\nghi");
        Assert.Equal(0, ColumnSelection.OfsToCol(table, 0, Tab));  // 'a'
        Assert.Equal(2, ColumnSelection.OfsToCol(table, 2, Tab));  // 'c'
        Assert.Equal(1, ColumnSelection.OfsToCol(table, 5, Tab));  // 'e' on line 1
    }

    // =====================================================================
    // ColToCharIdx / OfsToCol — with tabs
    // =====================================================================

    [Fact]
    public void ColToCharIdx_WithTabs_ExpandsTabStops() {
        // "\txy" → tab occupies cols 0-3, 'x' at col 4, 'y' at col 5
        var table = new PieceTable("\txy");
        var lineStart = 0L;
        var lineLen = 3;
        Assert.Equal(0, ColumnSelection.ColToCharIdx(table, lineStart, lineLen, 0, Tab));
        Assert.Equal(1, ColumnSelection.ColToCharIdx(table, lineStart, lineLen, 4, Tab)); // after tab → char 1
        Assert.Equal(2, ColumnSelection.ColToCharIdx(table, lineStart, lineLen, 5, Tab)); // char 2 = 'y'
    }

    [Fact]
    public void OfsToCol_WithTabs_ExpandsTabStops() {
        var table = new PieceTable("\txy");
        Assert.Equal(0, ColumnSelection.OfsToCol(table, 0, Tab));  // before tab
        Assert.Equal(4, ColumnSelection.OfsToCol(table, 1, Tab));  // after tab
        Assert.Equal(5, ColumnSelection.OfsToCol(table, 2, Tab));  // 'y'
    }

    // =====================================================================
    // PaddingNeeded
    // =====================================================================

    [Fact]
    public void PaddingNeeded_ShortLine_ReturnsSpacesNeeded() {
        var table = new PieceTable("ab\ncd\nef");
        // Line 0 is "ab" (2 chars, col 2). Need padding to reach col 5.
        Assert.Equal(3, ColumnSelection.PaddingNeeded(table, 0, 5, Tab));
    }

    [Fact]
    public void PaddingNeeded_LongEnoughLine_ReturnsZero() {
        var table = new PieceTable("abcdef\ngh");
        Assert.Equal(0, ColumnSelection.PaddingNeeded(table, 0, 3, Tab));
    }

    // =====================================================================
    // Materialize — basic rectangle
    // =====================================================================

    [Fact]
    public void Materialize_BasicRectangle_ReturnsPerLineSelections() {
        var table = new PieceTable("abcdef\nghijkl\nmnopqr");
        var colSel = new ColumnSelection(0, 2, 2, 4);
        var sels = colSel.Materialize(table, Tab);

        Assert.Equal(3, sels.Count);
        // Line 0: chars 2-4 → "cd"
        Assert.Equal(new Selection(2, 4), sels[0]);
        // Line 1: chars 9-11 → "ij" (lineStart=7, +2=9, +4=11)
        Assert.Equal(new Selection(9, 11), sels[1]);
        // Line 2: chars 16-18 → "op" (lineStart=14, +2=16, +4=18)
        Assert.Equal(new Selection(16, 18), sels[2]);
    }

    [Fact]
    public void Materialize_ShortLines_ClampsToLineEnd() {
        var table = new PieceTable("abcdef\ngh\nmnopqr");
        var colSel = new ColumnSelection(0, 2, 2, 5);
        var sels = colSel.Materialize(table, Tab);

        Assert.Equal(3, sels.Count);
        // Line 0: chars 2-5
        Assert.Equal(new Selection(2, 5), sels[0]);
        // Line 1: "gh" only 2 chars, so col 2-5 clamps to 2-2 (collapsed at end)
        Assert.Equal(new Selection(9, 9), sels[1]);
        // Line 2: chars 12-15
        Assert.Equal(new Selection(12, 15), sels[2]);
    }

    [Fact]
    public void Materialize_ZeroWidth_ProducesCollapsedSelections() {
        var table = new PieceTable("abc\ndef\nghi");
        var colSel = new ColumnSelection(0, 2, 2, 2);
        var sels = colSel.Materialize(table, Tab);

        Assert.Equal(3, sels.Count);
        foreach (var s in sels) {
            Assert.True(s.IsEmpty);
        }
    }

    [Fact]
    public void Materialize_SingleLine_ReturnsOneSelection() {
        var table = new PieceTable("abcdef");
        var colSel = new ColumnSelection(0, 1, 0, 4);
        var sels = colSel.Materialize(table, Tab);

        Assert.Single(sels);
        Assert.Equal(new Selection(1, 4), sels[0]);
    }

    // =====================================================================
    // MaterializeCarets
    // =====================================================================

    [Fact]
    public void MaterializeCarets_ReturnsActiveColumnOffsets() {
        var table = new PieceTable("abc\ndef\nghi");
        // Active column = 2
        var colSel = new ColumnSelection(0, 1, 2, 2);
        var carets = colSel.MaterializeCarets(table, Tab);

        Assert.Equal(3, carets.Count);
        Assert.Equal(2, carets[0]); // line 0, col 2 → offset 2
        Assert.Equal(6, carets[1]); // line 1, col 2 → offset 4+2=6
        Assert.Equal(10, carets[2]); // line 2, col 2 → offset 8+2=10
    }

    // =====================================================================
    // InsertAtCursors
    // =====================================================================

    [Fact]
    public void InsertAtCursors_InsertsOnEachLine() {
        var doc = MakeDoc("abc\ndef\nghi");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 1); // zero-width at col 1
        doc.InsertAtCursors("X", Tab);

        Assert.Equal("aXbc\ndXef\ngXhi", doc.Table.GetText());
    }

    [Fact]
    public void InsertAtCursors_ReplacesSelection() {
        var doc = MakeDoc("abcdef\nghijkl\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 3); // cols 1-3 on 3 lines
        doc.InsertAtCursors("X", Tab);

        // "bc" on line 0 replaced by "X" → "aXdef"
        Assert.Equal("aXdef\ngXjkl\nmXpqr", doc.Table.GetText());
    }

    [Fact]
    public void InsertAtCursors_Undo_RevertsAll() {
        var doc = MakeDoc("abc\ndef\nghi");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 1);
        doc.InsertAtCursors("X", Tab);
        Assert.Equal("aXbc\ndXef\ngXhi", doc.Table.GetText());

        doc.Undo();
        Assert.Equal("abc\ndef\nghi", doc.Table.GetText());
    }

    [Fact]
    public void InsertAtCursors_PadsShortLines() {
        var doc = MakeDoc("abcdef\ngh\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 5, 2, 5); // col 5, line 1 is only 2 chars
        doc.InsertAtCursors("X", Tab);

        // Line 1 "gh" padded with 3 spaces to reach col 5, then X inserted
        Assert.Equal("abcdeXf\ngh   X\nmnopqXr", doc.Table.GetText());
    }

    // =====================================================================
    // DeleteBackwardAtCursors
    // =====================================================================

    [Fact]
    public void DeleteBackwardAtCursors_DeletesOnEachLine() {
        var doc = MakeDoc("abc\ndef\nghi");
        doc.ColumnSel = new ColumnSelection(0, 2, 2, 2); // zero-width at col 2
        doc.DeleteBackwardAtCursors(Tab);

        Assert.Equal("ac\ndf\ngi", doc.Table.GetText());
    }

    [Fact]
    public void DeleteBackwardAtCursors_WithSelection_DeletesContent() {
        var doc = MakeDoc("abcd\nefgh\nijkl");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 3); // cols 1-3
        doc.DeleteBackwardAtCursors(Tab);

        // Deletes "bc" from line 0, "fg" from line 1, "jk" from line 2
        Assert.Equal("ad\neh\nil", doc.Table.GetText());
    }

    [Fact]
    public void DeleteBackwardAtCursors_AtCol0_IsNoop() {
        var doc = MakeDoc("abc\ndef");
        doc.ColumnSel = new ColumnSelection(0, 0, 1, 0);
        doc.DeleteBackwardAtCursors(Tab);

        Assert.Equal("abc\ndef", doc.Table.GetText());
    }

    // =====================================================================
    // DeleteForwardAtCursors
    // =====================================================================

    [Fact]
    public void DeleteForwardAtCursors_DeletesOnEachLine() {
        var doc = MakeDoc("abc\ndef\nghi");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 1); // zero-width at col 1
        doc.DeleteForwardAtCursors(Tab);

        Assert.Equal("ac\ndf\ngi", doc.Table.GetText());
    }

    // =====================================================================
    // GetColumnSelectedText
    // =====================================================================

    [Fact]
    public void GetColumnSelectedText_ReturnsPerLineText() {
        var doc = MakeDoc("abcdef\nghijkl\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 2, 2, 4);

        var text = doc.GetColumnSelectedText(Tab);
        // Lines joined by the document's line ending (LF default for content with \n).
        // Document created from string with \n → dominant ending is LF.
        Assert.Contains("cd", text);
        Assert.Contains("ij", text);
        Assert.Contains("op", text);
    }

    [Fact]
    public void GetColumnSelectedText_ShortLines_ReturnsEmptyForShort() {
        var doc = MakeDoc("abcdef\ngh\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 3, 2, 5);

        var text = doc.GetColumnSelectedText(Tab);
        // Line 1 "gh" has nothing in cols 3-5. Verify the parts are correct.
        Assert.Contains("de", text);
        Assert.Contains("pq", text);
    }

    // =====================================================================
    // ClearColumnSelection
    // =====================================================================

    [Fact]
    public void ClearColumnSelection_ExitsColumnMode() {
        var doc = MakeDoc("abc\ndef\nghi");
        doc.ColumnSel = new ColumnSelection(0, 1, 2, 2);
        doc.ClearColumnSelection(Tab);

        Assert.Null(doc.ColumnSel);
    }

    // =====================================================================
    // DeleteColumnSelectionContent
    // =====================================================================

    [Fact]
    public void DeleteColumnSelectionContent_RemovesRectangleText() {
        var doc = MakeDoc("abcdef\nghijkl\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 2, 2, 4);
        doc.DeleteColumnSelectionContent(Tab);

        Assert.Equal("abef\nghkl\nmnqr", doc.Table.GetText());
        // Column selection collapsed to zero width at col 2
        Assert.NotNull(doc.ColumnSel);
        Assert.Equal(2, doc.ColumnSel.Value.LeftCol);
        Assert.Equal(2, doc.ColumnSel.Value.RightCol);
    }

    [Fact]
    public void DeleteColumnSelectionContent_Undo_RevertsAll() {
        var doc = MakeDoc("abcdef\nghijkl\nmnopqr");
        doc.ColumnSel = new ColumnSelection(0, 2, 2, 4);
        doc.DeleteColumnSelectionContent(Tab);
        Assert.Equal("abef\nghkl\nmnqr", doc.Table.GetText());

        doc.Undo();
        Assert.Equal("abcdef\nghijkl\nmnopqr", doc.Table.GetText());
    }
}
