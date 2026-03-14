using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Tests;

public class IndentInfoTests {
    // =====================================================================
    // FromCounts
    // =====================================================================

    [Fact]
    public void FromCounts_AllZero_ReturnsDefault() {
        var info = IndentInfo.FromCounts(0, 0);
        Assert.Equal(IndentStyle.Spaces, info.Dominant);
        Assert.False(info.IsMixed);
    }

    [Fact]
    public void FromCounts_OnlySpaces_ReturnsSpaces() {
        var info = IndentInfo.FromCounts(10, 0);
        Assert.Equal(IndentStyle.Spaces, info.Dominant);
        Assert.False(info.IsMixed);
    }

    [Fact]
    public void FromCounts_OnlyTabs_ReturnsTabs() {
        var info = IndentInfo.FromCounts(0, 5);
        Assert.Equal(IndentStyle.Tabs, info.Dominant);
        Assert.False(info.IsMixed);
    }

    [Fact]
    public void FromCounts_MoreTabs_ReturnsTabs_Mixed() {
        var info = IndentInfo.FromCounts(3, 10);
        Assert.Equal(IndentStyle.Tabs, info.Dominant);
        Assert.True(info.IsMixed);
    }

    [Fact]
    public void FromCounts_MoreSpaces_ReturnsSpaces_Mixed() {
        var info = IndentInfo.FromCounts(10, 3);
        Assert.Equal(IndentStyle.Spaces, info.Dominant);
        Assert.True(info.IsMixed);
    }

    [Fact]
    public void Label_Spaces() {
        Assert.Equal("Spaces", new IndentInfo(IndentStyle.Spaces, false).Label);
    }

    [Fact]
    public void Label_Tabs() {
        Assert.Equal("Tabs", new IndentInfo(IndentStyle.Tabs, false).Label);
    }

    // =====================================================================
    // ConvertIndentation
    // =====================================================================

    [Fact]
    public void ConvertIndentation_TabsToSpaces() {
        var doc = new Document("\tfoo\n\t\tbar\nbaz\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        Assert.Equal("    foo\n        bar\nbaz\n", doc.Table.GetText());
        Assert.Equal(IndentStyle.Spaces, doc.IndentInfo.Dominant);
    }

    [Fact]
    public void ConvertIndentation_SpacesToTabs() {
        var doc = new Document("    foo\n        bar\nbaz\n");
        doc.ConvertIndentation(IndentStyle.Tabs, tabSize: 4);
        Assert.Equal("\tfoo\n\t\tbar\nbaz\n", doc.Table.GetText());
        Assert.Equal(IndentStyle.Tabs, doc.IndentInfo.Dominant);
    }

    [Fact]
    public void ConvertIndentation_MixedToSpaces() {
        var doc = new Document("\t foo\n  \tbar\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        // \t = 4 spaces + 1 space = 5 spaces for first line
        // 2 spaces + \t = 2 + 4 = 6 spaces -> 1 tab + 2 spaces for "to tabs"
        Assert.Equal("     foo\n      bar\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_MixedToTabs() {
        var doc = new Document("        foo\n    bar\n  baz\n");
        doc.ConvertIndentation(IndentStyle.Tabs, tabSize: 4);
        // 8 spaces → 2 tabs, 4 spaces → 1 tab, 2 spaces → 2 remainder spaces
        Assert.Equal("\t\tfoo\n\tbar\n  baz\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_NoOp_DoesNotModifyContent() {
        var doc = new Document("    foo\n    bar\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        // Already all spaces — no change to content.
        Assert.Equal("    foo\n    bar\n", doc.Table.GetText());
        Assert.Equal(IndentStyle.Spaces, doc.IndentInfo.Dominant);
    }

    [Fact]
    public void ConvertIndentation_PreservesLineEndings() {
        var doc = new Document("\tfoo\r\n\tbar\r\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        Assert.Equal("    foo\r\n    bar\r\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_IsUndoable() {
        var doc = new Document("\tfoo\n");
        doc.ConvertIndentation(IndentStyle.Spaces, tabSize: 4);
        Assert.Equal("    foo\n", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("\tfoo\n", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_EmptyDocument() {
        var doc = new Document("");
        doc.ConvertIndentation(IndentStyle.Tabs, tabSize: 4);
        Assert.Equal("", doc.Table.GetText());
    }

    [Fact]
    public void ConvertIndentation_NoIndentation() {
        var doc = new Document("foo\nbar\n");
        doc.ConvertIndentation(IndentStyle.Tabs, tabSize: 4);
        Assert.Equal("foo\nbar\n", doc.Table.GetText());
    }
}
