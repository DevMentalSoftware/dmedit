using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.Documents.History;

namespace DevMentalMd.App.Services;

/// <summary>
/// JSON round-trip serializer for <see cref="IDocumentEdit"/> trees and
/// <see cref="EditHistory"/> stacks.  Produces compact JSON suitable for
/// session-persistence files.
/// </summary>
public static class EditSerializer {
    /// <summary>
    /// Maximum total delete text we are willing to materialize from piece table
    /// buffers during serialization.  Deletes above this threshold are serialized
    /// without their text — Apply (forward replay) still works, but Revert (undo
    /// past that point after restore) will not restore the deleted content.
    /// Insert text is always included because it already exists as a string.
    /// </summary>
    private const long MaxMaterializeBytes = 16 * 1024 * 1024; // 16 MB

    // -----------------------------------------------------------------
    // Serialize
    // -----------------------------------------------------------------

    /// <summary>
    /// Serializes a complete edit history (undo + redo stacks) to JSON.
    /// All entries are always serialized — no entries are ever dropped.
    /// Large deletes may omit their text to avoid memory spikes; those edits
    /// still replay correctly but cannot be undone after restore.
    /// </summary>
    public static string Serialize(
        IReadOnlyList<EditHistory.HistoryEntry> undoEntries,
        IReadOnlyList<EditHistory.HistoryEntry> redoEntries,
        PieceTable table) {

        long budget = MaxMaterializeBytes;
        var root = new JsonObject {
            ["undo"] = SerializeStack(undoEntries, table, ref budget),
            ["redo"] = SerializeStack(redoEntries, table, ref budget),
        };
        return root.ToJsonString(JsonOpts);
    }

    private static JsonArray SerializeStack(
        IReadOnlyList<EditHistory.HistoryEntry> entries, PieceTable table, ref long budget) {

        var arr = new JsonArray();
        foreach (var entry in entries) {
            arr.Add(SerializeEntry(entry, table, ref budget));
        }
        return arr;
    }

    private static JsonObject SerializeEntry(
        EditHistory.HistoryEntry entry, PieceTable table, ref long budget) {

        var obj = SerializeEdit(entry.Edit, table, ref budget);
        obj["selAnchor"] = entry.SelectionBefore.Anchor;
        obj["selActive"] = entry.SelectionBefore.Active;
        return obj;
    }

    private static JsonObject SerializeEdit(IDocumentEdit edit, PieceTable table, ref long budget) {
        switch (edit) {
            case InsertEdit ins:
                // Insert text already exists as a string — always include it.
                return new JsonObject {
                    ["type"] = "insert",
                    ["ofs"] = ins.Ofs,
                    ["text"] = ins.Text,
                };
            case DeleteEdit del: {
                var cost = del.Len * 2L;
                if (cost > budget) {
                    // Too large to materialize — serialize without text.
                    // Apply still works (delete by ofs+len); undo past this
                    // point won't restore the deleted content after restore.
                    budget = 0;
                    return new JsonObject {
                        ["type"] = "delete",
                        ["ofs"] = del.Ofs,
                        ["len"] = del.Len,
                        ["text"] = "",
                    };
                }
                budget -= cost;
                return new JsonObject {
                    ["type"] = "delete",
                    ["ofs"] = del.Ofs,
                    ["len"] = del.Len,
                    ["text"] = del.MaterializeText(table),
                };
            }
            case CompoundEdit comp:
                return SerializeCompound(comp, table, ref budget);
            default:
                throw new NotSupportedException($"Unknown edit type: {edit.GetType().Name}");
        }
    }

    private static JsonObject SerializeCompound(CompoundEdit comp, PieceTable table, ref long budget) {
        var edits = new JsonArray();
        foreach (var e in comp.Edits) {
            edits.Add(SerializeEdit(e, table, ref budget));
        }
        return new JsonObject {
            ["type"] = "compound",
            ["edits"] = edits,
        };
    }

    // -----------------------------------------------------------------
    // Deserialize
    // -----------------------------------------------------------------

    /// <summary>
    /// Deserializes a JSON string produced by <see cref="Serialize"/> back
    /// into undo and redo entry lists.
    /// </summary>
    public static (IReadOnlyList<EditHistory.HistoryEntry> Undo,
                    IReadOnlyList<EditHistory.HistoryEntry> Redo)
        Deserialize(string json) {

        var root = JsonNode.Parse(json)!.AsObject();
        var undo = DeserializeStack(root["undo"]!.AsArray());
        var redo = DeserializeStack(root["redo"]!.AsArray());
        return (undo, redo);
    }

    private static List<EditHistory.HistoryEntry> DeserializeStack(JsonArray arr) {
        var list = new List<EditHistory.HistoryEntry>(arr.Count);
        foreach (var node in arr) {
            list.Add(DeserializeEntry(node!.AsObject()));
        }
        return list;
    }

    private static EditHistory.HistoryEntry DeserializeEntry(JsonObject obj) {
        var edit = DeserializeEdit(obj);
        var sel = new Selection(
            obj["selAnchor"]!.GetValue<long>(),
            obj["selActive"]!.GetValue<long>());
        return new EditHistory.HistoryEntry(edit, sel);
    }

    private static IDocumentEdit DeserializeEdit(JsonObject obj) {
        var type = obj["type"]!.GetValue<string>();
        return type switch {
            "insert" => new InsertEdit(
                obj["ofs"]!.GetValue<long>(),
                obj["text"]!.GetValue<string>()),
            "delete" => new DeleteEdit(
                obj["ofs"]!.GetValue<long>(),
                obj["len"]!.GetValue<long>(),
                obj["text"]!.GetValue<string>()),
            "compound" => DeserializeCompound(obj),
            _ => throw new NotSupportedException($"Unknown edit type: {type}")
        };
    }

    private static CompoundEdit DeserializeCompound(JsonObject obj) {
        var arr = obj["edits"]!.AsArray();
        var edits = new List<IDocumentEdit>(arr.Count);
        foreach (var node in arr) {
            edits.Add(DeserializeEdit(node!.AsObject()));
        }
        return new CompoundEdit(edits);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = false,
    };
}
