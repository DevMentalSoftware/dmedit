using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;

namespace DMEdit.App.Services;

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
            case SpanInsertEdit spanIns:
                // Reference buffer offsets — no text materialization.
                var obj = new JsonObject {
                    ["type"] = "bufInsert",
                    ["ofs"] = spanIns.Ofs,
                    ["bufStart"] = spanIns.AddBufStart,
                    ["bufLen"] = spanIns.Len,
                };
                // Only serialize bufIdx when it's not the default add buffer.
                if (spanIns.BufIdx >= 0) {
                    obj["bufIdx"] = spanIns.BufIdx;
                }
                return obj;
            case DeleteEdit del:
                // No text materialization needed — pieces reference the
                // persisted add buffer and original file, so Apply (which
                // recaptures pieces) works after restore.
                return new JsonObject {
                    ["type"] = "delete",
                    ["ofs"] = del.Ofs,
                    ["len"] = del.Len,
                };
            case UniformBulkReplaceEdit ubr: {
                var positions = new JsonArray();
                foreach (var p in ubr.MatchPositions) positions.Add(p);
                return new JsonObject {
                    ["type"] = "bulkUniform",
                    ["matchLen"] = ubr.MatchLen,
                    ["replacement"] = ubr.Replacement,
                    ["positions"] = positions,
                };
            }
            case VaryingBulkReplaceEdit vbr: {
                var items = new JsonArray();
                for (var i = 0; i < vbr.MatchCount; i++) {
                    var m = vbr.Matches[i];
                    items.Add(new JsonObject {
                        ["pos"] = m.Pos,
                        ["len"] = m.Len,
                        ["rep"] = vbr.Replacements[i],
                    });
                }
                return new JsonObject {
                    ["type"] = "bulkVarying",
                    ["matches"] = items,
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
    /// into undo and redo entry lists. When <paramref name="restoredBufIdx"/>
    /// is non-negative, any <c>bufInsert</c> edits that reference the persisted
    /// add buffer are remapped to that buffer index.
    /// </summary>
    public static (IReadOnlyList<EditHistory.HistoryEntry> Undo,
                    IReadOnlyList<EditHistory.HistoryEntry> Redo)
        Deserialize(string json, int restoredBufIdx = -1) {

        var root = JsonNode.Parse(json)!.AsObject();
        var undo = DeserializeStack(root["undo"]!.AsArray(), restoredBufIdx);
        var redo = DeserializeStack(root["redo"]!.AsArray(), restoredBufIdx);
        return (undo, redo);
    }

    private static List<EditHistory.HistoryEntry> DeserializeStack(JsonArray arr, int restoredBufIdx) {
        var list = new List<EditHistory.HistoryEntry>(arr.Count);
        foreach (var node in arr) {
            list.Add(DeserializeEntry(node!.AsObject(), restoredBufIdx));
        }
        return list;
    }

    private static EditHistory.HistoryEntry DeserializeEntry(JsonObject obj, int restoredBufIdx) {
        var edit = DeserializeEdit(obj, restoredBufIdx);
        var sel = new Selection(
            obj["selAnchor"]!.GetValue<long>(),
            obj["selActive"]!.GetValue<long>());
        return new EditHistory.HistoryEntry(edit, sel);
    }

    private static IDocumentEdit DeserializeEdit(JsonObject obj, int restoredBufIdx) {
        var type = obj["type"]!.GetValue<string>();
        return type switch {
            "bufInsert" => DeserializeBufInsert(obj, restoredBufIdx),
            "delete" => new DeleteEdit(
                obj["ofs"]!.GetValue<long>(),
                obj["len"]!.GetValue<long>(),
                Array.Empty<Piece>()),
            "compound" => DeserializeCompound(obj, restoredBufIdx),
            "bulkUniform" => DeserializeUniformBulk(obj),
            "bulkVarying" => DeserializeVaryingBulk(obj),
            _ => throw new NotSupportedException($"Unknown edit type: {type}")
        };
    }

    private static SpanInsertEdit DeserializeBufInsert(JsonObject obj, int restoredBufIdx) {
        var bufIdx = obj["bufIdx"]?.GetValue<int>() ?? -1;
        // Remap: if the edit referenced the persisted add buffer (bufIdx >= 0
        // or default -1 from old format), use the restored paged buffer index.
        if (restoredBufIdx >= 0 && bufIdx <= 0) {
            bufIdx = restoredBufIdx;
        }
        return new SpanInsertEdit(
            obj["ofs"]!.GetValue<long>(),
            obj["bufStart"]!.GetValue<long>(),
            obj["bufLen"]!.GetValue<int>(),
            bufIdx);
    }

    private static UniformBulkReplaceEdit DeserializeUniformBulk(JsonObject obj) {
        var matchLen = obj["matchLen"]!.GetValue<int>();
        var replacement = obj["replacement"]!.GetValue<string>();
        var posArr = obj["positions"]!.AsArray();
        var positions = new long[posArr.Count];
        for (var i = 0; i < posArr.Count; i++) {
            positions[i] = posArr[i]!.GetValue<long>();
        }
        // Saved state is not persisted — undo past a session restore boundary
        // for bulk edits is not supported (same as oversized deletes).
        return new UniformBulkReplaceEdit(
            positions, matchLen, replacement, [], [], 0);
    }

    private static VaryingBulkReplaceEdit DeserializeVaryingBulk(JsonObject obj) {
        var items = obj["matches"]!.AsArray();
        var matches = new (long Pos, int Len)[items.Count];
        var replacements = new string[items.Count];
        for (var i = 0; i < items.Count; i++) {
            var m = items[i]!.AsObject();
            matches[i] = (m["pos"]!.GetValue<long>(), m["len"]!.GetValue<int>());
            replacements[i] = m["rep"]!.GetValue<string>();
        }
        return new VaryingBulkReplaceEdit(
            matches, replacements, [], [], 0);
    }

    private static CompoundEdit DeserializeCompound(JsonObject obj, int restoredBufIdx) {
        var arr = obj["edits"]!.AsArray();
        var edits = new List<IDocumentEdit>(arr.Count);
        foreach (var node in arr) {
            edits.Add(DeserializeEdit(node!.AsObject(), restoredBufIdx));
        }
        return new CompoundEdit(edits);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = false,
    };
}
