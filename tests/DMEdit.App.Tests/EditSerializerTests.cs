using System.Linq;
using System.Reflection;
using DMEdit.App.Services;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;

namespace DMEdit.App.Tests;

public class EditSerializerTests {
    /// <summary>Helper: creates a PieceTable and appends text to its add buffer.</summary>
    private static (PieceTable table, long bufStart) MakeTable(string text) {
        var table = new PieceTable();
        var bufStart = table.AppendToAddBuffer(text);
        return (table, bufStart);
    }

    [Fact]
    public void RoundTrip_SpanInsertEdit() {
        var (table, bufStart) = MakeTable("hello");
        var undo = new List<EditHistory.HistoryEntry> {
            new(new SpanInsertEdit(10, bufStart, 5), new Selection(10, 10)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, table);
        var (u, r) = EditSerializer.Deserialize(json);

        Assert.Single(u);
        Assert.Empty(r);
        var entry = u[0];
        var ins = Assert.IsType<SpanInsertEdit>(entry.Edit);
        Assert.Equal(10, ins.Ofs);
        Assert.Equal(bufStart, ins.AddBufStart);
        Assert.Equal(5, ins.Len);
        Assert.Equal(new Selection(10, 10), entry.SelectionBefore);
    }

    [Fact]
    public void RoundTrip_DeleteEdit() {
        var table = new PieceTable();
        var del = new DeleteEdit(5, 5, Array.Empty<Piece>());
        var undo = new List<EditHistory.HistoryEntry> {
            new(del, new Selection(5, 10)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, table);
        var (u, _) = EditSerializer.Deserialize(json);

        var restored = Assert.IsType<DeleteEdit>(u[0].Edit);
        Assert.Equal(5, restored.Ofs);
        Assert.Equal(5, restored.Len);
        Assert.Equal(new Selection(5, 10), u[0].SelectionBefore);
    }

    [Fact]
    public void RoundTrip_CompoundEdit() {
        var (table, bufStart) = MakeTable("A");
        var compound = new CompoundEdit(new List<IDocumentEdit> {
            new SpanInsertEdit(0, bufStart, 1),
            new DeleteEdit(1, 1, Array.Empty<Piece>()),
        });
        var undo = new List<EditHistory.HistoryEntry> {
            new(compound, new Selection(0, 0)),
        };
        var redo = new List<EditHistory.HistoryEntry>();

        var json = EditSerializer.Serialize(undo, redo, table);
        var (u, _) = EditSerializer.Deserialize(json);

        var comp = Assert.IsType<CompoundEdit>(u[0].Edit);
        Assert.Equal(2, comp.Edits.Count);
        Assert.IsType<SpanInsertEdit>(comp.Edits[0]);
        Assert.IsType<DeleteEdit>(comp.Edits[1]);
    }

    [Fact]
    public void RoundTrip_NestedCompound() {
        var (table, bufStart) = MakeTable("inner");
        var inner = new CompoundEdit(new List<IDocumentEdit> {
            new SpanInsertEdit(0, bufStart, 5),
        });
        var outer = new CompoundEdit(new List<IDocumentEdit> {
            inner,
            new DeleteEdit(5, 1, Array.Empty<Piece>()),
        });
        var undo = new List<EditHistory.HistoryEntry> {
            new(outer, Selection.Collapsed(0)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), table);
        var (u, _) = EditSerializer.Deserialize(json);

        var outerR = Assert.IsType<CompoundEdit>(u[0].Edit);
        Assert.Equal(2, outerR.Edits.Count);
        var innerR = Assert.IsType<CompoundEdit>(outerR.Edits[0]);
        Assert.Single(innerR.Edits);
    }

    [Fact]
    public void RoundTrip_BothStacks() {
        var table = new PieceTable();
        var a = table.AppendToAddBuffer("A");
        var b = table.AppendToAddBuffer("B");
        var c = table.AppendToAddBuffer("C");
        var undo = new List<EditHistory.HistoryEntry> {
            new(new SpanInsertEdit(0, a, 1), Selection.Collapsed(0)),
            new(new SpanInsertEdit(1, b, 1), Selection.Collapsed(1)),
        };
        var redo = new List<EditHistory.HistoryEntry> {
            new(new SpanInsertEdit(2, c, 1), Selection.Collapsed(2)),
        };

        var json = EditSerializer.Serialize(undo, redo, table);
        var (u, r) = EditSerializer.Deserialize(json);

        Assert.Equal(2, u.Count);
        Assert.Single(r);
    }

    [Fact]
    public void RoundTrip_DeleteEdit_PreservesLen() {
        var table = new PieceTable();
        var del = new DeleteEdit(100, 5_000_000, Array.Empty<Piece>());
        var undo = new List<EditHistory.HistoryEntry> {
            new(del, Selection.Collapsed(100)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), table);
        var (u, _) = EditSerializer.Deserialize(json);

        var restored = Assert.IsType<DeleteEdit>(u[0].Edit);
        Assert.Equal(100, restored.Ofs);
        Assert.Equal(5_000_000, restored.Len);
    }

    [Fact]
    public void RoundTrip_SpecialCharacters() {
        var text = "line1\nline2\ttab\"quote\\backslash";
        var (table, bufStart) = MakeTable(text);
        var undo = new List<EditHistory.HistoryEntry> {
            new(new SpanInsertEdit(0, bufStart, text.Length),
                Selection.Collapsed(0)),
        };

        var json = EditSerializer.Serialize(undo, new List<EditHistory.HistoryEntry>(), table);
        var (u, _) = EditSerializer.Deserialize(json);

        var ins = Assert.IsType<SpanInsertEdit>(u[0].Edit);
        Assert.Equal(text.Length, ins.Len);
        Assert.Equal(bufStart, ins.AddBufStart);
    }

    // ------------------------------------------------------------------
    // Reflection smoke test: every concrete IDocumentEdit subclass must
    // have a serialize + deserialize path (TRIAGE Priority 2 gap).
    //
    // Without this, adding a new IDocumentEdit subclass in Core would
    // silently drop out of session persistence — Serialize would throw
    // NotSupportedException on a user's undo stack and lose their work.
    //
    // New subclasses must be added to the factory table below so the
    // test enumerates them.  A subclass that exists in the assembly but
    // is missing from the factory fails this test with a clear message.
    // ------------------------------------------------------------------

    [Fact]
    public void EditSerializer_RecognizesEveryConcreteIDocumentEditSubclass() {
        var table = new PieceTable();
        var bufStart = table.AppendToAddBuffer("x");

        // Factory table: each entry is (subclass type, minimal instance).
        // Add a new entry when you introduce a new IDocumentEdit subclass.
        var factories = new Dictionary<System.Type, IDocumentEdit> {
            [typeof(SpanInsertEdit)] = new SpanInsertEdit(0, bufStart, 1),
            [typeof(DeleteEdit)] = new DeleteEdit(0, 1, System.Array.Empty<Piece>()),
            [typeof(CompoundEdit)] = new CompoundEdit(new List<IDocumentEdit> {
                new SpanInsertEdit(0, bufStart, 1),
            }),
            [typeof(UniformBulkReplaceEdit)] = new UniformBulkReplaceEdit(
                System.Array.Empty<long>(), 0, string.Empty,
                System.Array.Empty<Piece>(), System.Array.Empty<int>(), 0L),
            [typeof(VaryingBulkReplaceEdit)] = new VaryingBulkReplaceEdit(
                System.Array.Empty<(long Pos, int Len)>(),
                System.Array.Empty<string>(),
                System.Array.Empty<Piece>(), System.Array.Empty<int>(), 0L),
        };

        // Discover every concrete IDocumentEdit in the Core assembly.  If
        // the factory is missing one, fail loudly — don't silently skip.
        var coreAsm = typeof(IDocumentEdit).Assembly;
        var concreteSubclasses = coreAsm.GetTypes()
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && typeof(IDocumentEdit).IsAssignableFrom(t))
            .ToList();

        var missingFromFactory = concreteSubclasses
            .Where(t => !factories.ContainsKey(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(missingFromFactory.Count == 0,
            $"New IDocumentEdit subclasses without a factory entry: " +
            $"{string.Join(", ", missingFromFactory)}.  " +
            $"Add them to the factory table AND to EditSerializer.");

        // Every subclass in the factory must round-trip through the
        // serializer without hitting the NotSupportedException default.
        var unrecognized = new List<string>();
        foreach (var (type, instance) in factories) {
            var undo = new List<EditHistory.HistoryEntry> {
                new(instance, Selection.Collapsed(0)),
            };
            try {
                var json = EditSerializer.Serialize(undo, [], table);
                var (u, _) = EditSerializer.Deserialize(json);
                Assert.Single(u);
                Assert.Equal(type, u[0].Edit.GetType());
            } catch (NotSupportedException ex) {
                unrecognized.Add($"{type.Name}: {ex.Message}");
            }
        }

        Assert.True(unrecognized.Count == 0,
            $"EditSerializer does not recognize: {string.Join(", ", unrecognized)}");
    }
}
