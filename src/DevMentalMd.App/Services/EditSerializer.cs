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
    // -----------------------------------------------------------------
    // Serialize
    // -----------------------------------------------------------------

    /// <summary>
    /// Serializes a complete edit history (undo + redo stacks) to JSON.
    /// </summary>
    public static string Serialize(
        IReadOnlyList<EditHistory.HistoryEntry> undoEntries,
        IReadOnlyList<EditHistory.HistoryEntry> redoEntries) {

        var root = new JsonObject {
            ["undo"] = SerializeStack(undoEntries),
            ["redo"] = SerializeStack(redoEntries),
        };
        return root.ToJsonString(JsonOpts);
    }

    private static JsonArray SerializeStack(IReadOnlyList<EditHistory.HistoryEntry> entries) {
        var arr = new JsonArray();
        foreach (var entry in entries) {
            arr.Add(SerializeEntry(entry));
        }
        return arr;
    }

    private static JsonObject SerializeEntry(EditHistory.HistoryEntry entry) {
        var obj = SerializeEdit(entry.Edit);
        obj["selAnchor"] = entry.SelectionBefore.Anchor;
        obj["selActive"] = entry.SelectionBefore.Active;
        return obj;
    }

    private static JsonObject SerializeEdit(IDocumentEdit edit) => edit switch {
        InsertEdit ins => new JsonObject {
            ["type"] = "insert",
            ["ofs"] = ins.Ofs,
            ["text"] = ins.Text,
        },
        DeleteEdit del => new JsonObject {
            ["type"] = "delete",
            ["ofs"] = del.Ofs,
            ["len"] = del.Len,
            ["text"] = del.DeletedText ?? "",
        },
        CompoundEdit comp => SerializeCompound(comp),
        _ => throw new NotSupportedException($"Unknown edit type: {edit.GetType().Name}")
    };

    private static JsonObject SerializeCompound(CompoundEdit comp) {
        var edits = new JsonArray();
        foreach (var e in comp.Edits) {
            edits.Add(SerializeEdit(e));
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
