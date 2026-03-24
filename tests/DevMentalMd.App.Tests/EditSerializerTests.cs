using DevMentalMd.App.Services;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.Documents.History;

namespace DevMentalMd.App.Tests;

public class EditSerializerTests {
    private static readonly PieceTable EmptyTable = new();

    [Fact]
    public void RoundTrip_InsertEdit() {
        var undo = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(10, "hello"), new Selection(10, 10)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, EmptyTable);
        var (u, r) = EditSerializer.Deserialize(json);

        Assert.Single(u);
        Assert.Empty(r);
        var entry = u[0];
        var ins = Assert.IsType<InsertEdit>(entry.Edit);
        Assert.Equal(10, ins.Ofs);
        Assert.Equal("hello", ins.Text);
        Assert.Equal(new Selection(10, 10), entry.SelectionBefore);
    }

    [Fact]
    public void RoundTrip_DeleteEdit() {
        var undo = new List<EditHistory.HistoryEntry> {
            new(new DeleteEdit(5, "world"), new Selection(5, 10)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var del = Assert.IsType<DeleteEdit>(u[0].Edit);
        Assert.Equal(5, del.Ofs);
        Assert.Equal("world", del.DeletedText);
        Assert.Equal(new Selection(5, 10), u[0].SelectionBefore);
    }

    [Fact]
    public void RoundTrip_CompoundEdit() {
        var compound = new CompoundEdit(new List<IDocumentEdit> {
            new InsertEdit(0, "A"),
            new DeleteEdit(1, "B"),
        });
        var undo = new List<EditHistory.HistoryEntry> {
            new(compound, new Selection(0, 0)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var comp = Assert.IsType<CompoundEdit>(u[0].Edit);
        Assert.Equal(2, comp.Edits.Count);
        Assert.IsType<InsertEdit>(comp.Edits[0]);
        Assert.IsType<DeleteEdit>(comp.Edits[1]);
    }

    [Fact]
    public void RoundTrip_NestedCompound() {
        var inner = new CompoundEdit(new List<IDocumentEdit> {
            new InsertEdit(0, "inner"),
        });
        var outer = new CompoundEdit(new List<IDocumentEdit> {
            inner,
            new DeleteEdit(5, "x"),
        });
        var undo = new List<EditHistory.HistoryEntry> {
            new(outer, Selection.Collapsed(0)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var outerR = Assert.IsType<CompoundEdit>(u[0].Edit);
        Assert.Equal(2, outerR.Edits.Count);
        var innerR = Assert.IsType<CompoundEdit>(outerR.Edits[0]);
        Assert.Single(innerR.Edits);
    }

    [Fact]
    public void RoundTrip_BothStacks() {
        var undo = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "A"), Selection.Collapsed(0)),
            new(new InsertEdit(1, "B"), Selection.Collapsed(1)),
        };
        var redo = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(2, "C"), Selection.Collapsed(2)),
        };

        var json = EditSerializer.Serialize(undo, redo, EmptyTable);
        var (u, r) = EditSerializer.Deserialize(json);

        Assert.Equal(2, u.Count);
        Assert.Single(r);
    }

    [Fact]
    public void RoundTrip_DeleteEdit_PreservesLenWhenTextEmpty() {
        // Simulates an oversized delete where text was omitted during serialization.
        // The deserialized DeleteEdit must still have the correct Len for Apply.
        var del = new DeleteEdit(100, 5_000_000, "");
        var undo = new List<EditHistory.HistoryEntry> {
            new(del, Selection.Collapsed(100)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var restored = Assert.IsType<DeleteEdit>(u[0].Edit);
        Assert.Equal(100, restored.Ofs);
        Assert.Equal(5_000_000, restored.Len);
        Assert.Equal("", restored.DeletedText);
    }

    [Fact]
    public void RoundTrip_DeleteEdit_PreservesLenWithText() {
        // Normal case: text is present, Len should match text length.
        var undo = new List<EditHistory.HistoryEntry> {
            new(new DeleteEdit(5, "world"), Selection.Collapsed(5)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var del = Assert.IsType<DeleteEdit>(u[0].Edit);
        Assert.Equal(5, del.Len);
        Assert.Equal("world", del.DeletedText);
    }

    [Fact]
    public void RoundTrip_SpecialCharacters() {
        var undo = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "line1\nline2\ttab\"quote\\backslash"),
                Selection.Collapsed(0)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), EmptyTable);
        var (u, _) = EditSerializer.Deserialize(json);

        var ins = Assert.IsType<InsertEdit>(u[0].Edit);
        Assert.Equal("line1\nline2\ttab\"quote\\backslash", ins.Text);
    }
}
