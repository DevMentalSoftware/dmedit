using DevMentalMd.Core.Documents;
using DevMentalMd.Core.Documents.History;

namespace DevMentalMd.Core.Tests;

public class EditHistorySerializationTests {
    [Fact]
    public void GetUndoEntries_ReturnsBottomToTop() {
        var doc = new Document("abc");
        doc.Selection = Selection.Collapsed(3);
        doc.Insert("d"); // undo[0]
        doc.Insert("e"); // undo[1]

        var entries = doc.History.GetUndoEntries();
        Assert.Equal(2, entries.Count);
        // First entry is the oldest (insert "d")
        var first = Assert.IsType<InsertEdit>(entries[0].Edit);
        Assert.Equal("d", first.Text);
        var second = Assert.IsType<InsertEdit>(entries[1].Edit);
        Assert.Equal("e", second.Text);
    }

    [Fact]
    public void GetRedoEntries_ReturnsBottomToTop() {
        var doc = new Document("abc");
        doc.Selection = Selection.Collapsed(3);
        doc.Insert("d");
        doc.Insert("e");
        doc.Undo(); // "e" goes to redo
        doc.Undo(); // "d" goes to redo

        var redo = doc.History.GetRedoEntries();
        Assert.Equal(2, redo.Count);
        // Bottom = oldest undo = "e", top = "d"
        var first = Assert.IsType<InsertEdit>(redo[0].Edit);
        Assert.Equal("e", first.Text);
        var second = Assert.IsType<InsertEdit>(redo[1].Edit);
        Assert.Equal("d", second.Text);
    }

    [Fact]
    public void RestoreEntries_ReplaysUndoOntoTable() {
        // Build entries: insert "X" at 0, insert "Y" at 1.
        var undoEntries = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "X"), Selection.Collapsed(0)),
            new(new InsertEdit(1, "Y"), Selection.Collapsed(1)),
        };

        var doc = new Document("abc");
        doc.History.RestoreEntries(doc.Table, undoEntries,
            new List<EditHistory.HistoryEntry>(), savePointDepth: 0);

        Assert.Equal("XYabc", doc.Table.GetText());
        Assert.True(doc.CanUndo);
    }

    [Fact]
    public void RestoreEntries_UndoRevertsCorrectly() {
        var undoEntries = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "X"), Selection.Collapsed(0)),
        };

        var doc = new Document("abc");
        doc.History.RestoreEntries(doc.Table, undoEntries,
            new List<EditHistory.HistoryEntry>(), savePointDepth: 0);

        Assert.Equal("Xabc", doc.Table.GetText());
        doc.Undo();
        Assert.Equal("abc", doc.Table.GetText());
    }

    [Fact]
    public void RestoreEntries_RedoAppliesCorrectly() {
        var redoEntries = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "Z"), Selection.Collapsed(0)),
        };

        var doc = new Document("abc");
        doc.History.RestoreEntries(doc.Table,
            new List<EditHistory.HistoryEntry>(), redoEntries, savePointDepth: 0);

        Assert.Equal("abc", doc.Table.GetText()); // Redo not yet applied
        doc.Redo();
        Assert.Equal("Zabc", doc.Table.GetText());
    }

    [Fact]
    public void RestoreEntries_PreservesSavePointDepth() {
        var undoEntries = new List<EditHistory.HistoryEntry> {
            new(new InsertEdit(0, "A"), Selection.Collapsed(0)),
            new(new InsertEdit(1, "B"), Selection.Collapsed(1)),
        };

        var doc = new Document("x");
        doc.History.RestoreEntries(doc.Table, undoEntries,
            new List<EditHistory.HistoryEntry>(), savePointDepth: 1);

        // Save point at depth 1 means after first edit = saved.
        // We have 2 edits on undo stack, so we're 1 edit past save point.
        Assert.False(doc.IsAtSavePoint);

        doc.Undo(); // Now undo stack depth = 1 = save point
        Assert.True(doc.IsAtSavePoint);
    }

    [Fact]
    public void FullRoundTrip_EditUndoRedoPreserved() {
        // Create a document, make edits, undo some.
        var original = new Document("hello");
        original.Selection = Selection.Collapsed(5);
        original.Insert(" world");  // "hello world"
        original.Insert("!");       // "hello world!"
        original.MarkSavePoint();
        original.Insert(" extra");  // "hello world! extra"
        original.Undo();            // back to "hello world!"

        // Snapshot the history.
        var undoEntries = original.History.GetUndoEntries();
        var redoEntries = original.History.GetRedoEntries();
        var savePointDepth = original.History.SavePointDepth;

        // Restore onto a fresh document with the same base content.
        var restored = new Document("hello");
        restored.History.RestoreEntries(restored.Table, undoEntries, redoEntries, savePointDepth);

        Assert.Equal("hello world!", restored.Table.GetText());
        Assert.True(restored.IsAtSavePoint);

        // Redo should bring back " extra"
        restored.Redo();
        Assert.Equal("hello world! extra", restored.Table.GetText());
        Assert.False(restored.IsAtSavePoint);

        // Undo all the way back
        restored.Undo(); // "hello world!"
        restored.Undo(); // "hello world"
        restored.Undo(); // "hello"
        Assert.Equal("hello", restored.Table.GetText());
        Assert.False(restored.CanUndo);
    }

    // -----------------------------------------------------------------
    // IsAtSavePoint + compound edits
    // -----------------------------------------------------------------

    [Fact]
    public void IsAtSavePoint_ReturnsFalse_DuringOpenCompoundEdit() {
        // Simulates the coalescing path: BeginCompound → Insert → check dirty.
        var doc = new Document("hello");
        doc.MarkSavePoint();
        Assert.True(doc.IsAtSavePoint);

        // Open a compound (as Coalesce does before each keystroke group).
        doc.BeginCompound();

        // Insert while compound is open — edit goes into the compound list.
        doc.Selection = Selection.Collapsed(5);
        doc.Insert(" world");

        // The document must NOT be at the save point: there are pending edits.
        Assert.False(doc.IsAtSavePoint);

        // Close the compound — edits commit to the undo stack.
        doc.EndCompound();
        Assert.False(doc.IsAtSavePoint);
    }

    [Fact]
    public void IsAtSavePoint_ReturnsTrue_AfterCompoundUndone() {
        var doc = new Document("hello");
        doc.MarkSavePoint();

        doc.BeginCompound();
        doc.Selection = Selection.Collapsed(5);
        doc.Insert(" world");
        doc.EndCompound();

        Assert.False(doc.IsAtSavePoint);

        doc.Undo();
        Assert.True(doc.IsAtSavePoint);
        Assert.Equal("hello", doc.Table.GetText());
    }

    [Fact]
    public void IsAtSavePoint_ReturnsTrue_WhenEmptyCompoundOpened() {
        // Opening a compound without pushing any edits should not
        // change the save-point status.
        var doc = new Document("hello");
        doc.MarkSavePoint();

        doc.BeginCompound();
        Assert.True(doc.IsAtSavePoint);

        doc.EndCompound();
        Assert.True(doc.IsAtSavePoint);
    }

    [Fact]
    public void ChangedEvent_FiresDuringCompound_AllowsDirtyTracking() {
        // Verifies that Document.Changed fires inside an open compound,
        // and that IsAtSavePoint is false at that point — this is the
        // exact pattern the UI uses to track dirty state.
        var doc = new Document("hello");
        doc.MarkSavePoint();

        bool changedFired = false;
        bool wasDirtyAtChange = false;

        doc.Changed += (_, _) => {
            changedFired = true;
            wasDirtyAtChange = !doc.IsAtSavePoint;
        };

        doc.BeginCompound();
        doc.Selection = Selection.Collapsed(5);
        doc.Insert("!");
        doc.EndCompound();

        Assert.True(changedFired);
        Assert.True(wasDirtyAtChange);
    }
}
