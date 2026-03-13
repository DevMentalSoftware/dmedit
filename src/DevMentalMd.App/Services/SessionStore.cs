using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.Documents.History;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App.Services;

/// <summary>
/// Persists and restores the set of open tabs (including dirty documents'
/// edit history) across application restarts.
/// Storage location: <c>%LOCALAPPDATA%/DevMentalMD/session/</c>.
/// </summary>
public static class SessionStore {
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevMentalMD", "session");

    private static readonly string ManifestPath = Path.Combine(SessionDir, "manifest.json");

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -----------------------------------------------------------------
    // Manifest DTOs
    // -----------------------------------------------------------------

    public sealed class SessionManifest {
        public int ActiveTabIndex { get; set; }
        public List<TabEntry> Tabs { get; set; } = new();
    }

    public sealed class TabEntry {
        public string Id { get; set; } = "";
        public string? FilePath { get; set; }
        public string DisplayName { get; set; } = "";
        public string? BaseSha1 { get; set; }
        public bool IsDirty { get; set; }
        public long CaretAnchor { get; set; }
        public long CaretActive { get; set; }
        public double ScrollOffsetY { get; set; }
        public long WinTopLine { get; set; } = -1;
        public double WinScrollOffset { get; set; }
        public double WinRenderOffsetY { get; set; }
        public double WinFirstLineHeight { get; set; }
        public int SavePointDepth { get; set; }
    }

    // -----------------------------------------------------------------
    // Save
    // -----------------------------------------------------------------

    /// <summary>
    /// Saves the current session (open tabs + dirty edit histories) to disk.
    /// </summary>
    public static void Save(IReadOnlyList<TabState> tabs, int activeTabIndex) {
        try {
            Directory.CreateDirectory(SessionDir);

            var manifest = new SessionManifest { ActiveTabIndex = activeTabIndex };
            var referencedIds = new HashSet<string>();

            foreach (var tab in tabs) {
                if (tab.IsSettings) {
                    continue;
                }

                var entry = new TabEntry {
                    Id = tab.Id,
                    FilePath = tab.FilePath,
                    DisplayName = tab.DisplayName,
                    BaseSha1 = tab.BaseSha1,
                    IsDirty = tab.IsDirty,
                    CaretAnchor = tab.Document.Selection.Anchor,
                    CaretActive = tab.Document.Selection.Active,
                    ScrollOffsetY = tab.ScrollOffsetY,
                    WinTopLine = tab.WinTopLine,
                    WinScrollOffset = tab.WinScrollOffset,
                    WinRenderOffsetY = tab.WinRenderOffsetY,
                    WinFirstLineHeight = tab.WinFirstLineHeight,
                    SavePointDepth = tab.Document.History.SavePointDepth,
                };
                manifest.Tabs.Add(entry);
                referencedIds.Add(tab.Id);

                // Serialize edit history for dirty tabs (or tabs with undo/redo).
                var undoEntries = tab.Document.History.GetUndoEntries();
                var redoEntries = tab.Document.History.GetRedoEntries();
                if (undoEntries.Count > 0 || redoEntries.Count > 0) {
                    var editsJson = EditSerializer.Serialize(undoEntries, redoEntries);
                    var editsPath = Path.Combine(SessionDir, $"{tab.Id}.edits.json");
                    File.WriteAllText(editsPath, editsJson);
                }
            }

            // Write manifest atomically.
            var json = JsonSerializer.Serialize(manifest, JsonOpts);
            var tmpPath = ManifestPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, ManifestPath, overwrite: true);

            // Clean up stale edit files.
            CleanupStaleFiles(referencedIds);
        } catch {
            // Best-effort — non-fatal.
        }
    }

    // -----------------------------------------------------------------
    // Load
    // -----------------------------------------------------------------

    /// <summary>
    /// Result of restoring a single tab from the session.
    /// </summary>
    public sealed class RestoredTab {
        public required TabState Tab { get; init; }

        /// <summary>
        /// Non-null when the base file is missing or its SHA-1 doesn't match.
        /// The UI should show a conflict dialog for this tab.
        /// </summary>
        public FileConflict? Conflict { get; init; }
    }

    public enum FileConflictKind { Missing, Changed }

    public sealed class FileConflict {
        public required FileConflictKind Kind { get; init; }
        public required string FilePath { get; init; }
        public required string? ExpectedSha1 { get; init; }
        public string? ActualSha1 { get; init; }
    }

    /// <summary>
    /// Loads the session manifest and restores tabs. Returns null if no
    /// session exists or the manifest is corrupt.
    /// </summary>
    public static (List<RestoredTab> Tabs, int ActiveTabIndex)? Load() {
        try {
            if (!File.Exists(ManifestPath)) {
                return null;
            }
            var json = File.ReadAllText(ManifestPath);
            var manifest = JsonSerializer.Deserialize<SessionManifest>(json, JsonOpts);
            if (manifest is null || manifest.Tabs.Count == 0) {
                return null;
            }

            var restored = new List<RestoredTab>(manifest.Tabs.Count);
            foreach (var entry in manifest.Tabs) {
                restored.Add(RestoreTab(entry));
            }
            return (restored, manifest.ActiveTabIndex);
        } catch {
            return null;
        }
    }

    private static RestoredTab RestoreTab(TabEntry entry) {
        FileConflict? conflict = null;
        Document doc;

        if (entry.FilePath is not null) {
            // File-backed tab.
            if (!File.Exists(entry.FilePath)) {
                conflict = new FileConflict {
                    Kind = FileConflictKind.Missing,
                    FilePath = entry.FilePath,
                    ExpectedSha1 = entry.BaseSha1,
                };
                // Create empty doc as placeholder; the UI will handle the conflict.
                doc = new Document();
            } else {
                var currentSha1 = FileLoader.ComputeSha1File(entry.FilePath);
                if (entry.BaseSha1 is not null && currentSha1 != entry.BaseSha1) {
                    conflict = new FileConflict {
                        Kind = FileConflictKind.Changed,
                        FilePath = entry.FilePath,
                        ExpectedSha1 = entry.BaseSha1,
                        ActualSha1 = currentSha1,
                    };
                    // Load the current disk version as the base.
                    // Edits from the session won't be replayed (they'd be garbled).
                    doc = LoadDocumentFromDisk(entry.FilePath);
                } else {
                    // SHA-1 match — safe to load and replay edits.
                    doc = LoadDocumentFromDisk(entry.FilePath);
                    ReplayEdits(doc, entry);
                }
            }
        } else {
            // Untitled tab — replay edits from empty.
            doc = new Document();
            ReplayEdits(doc, entry);
        }

        // Restore selection, clamped to the document's actual length.
        // The saved caret may be out of bounds if the base file changed,
        // was missing, or edit replay failed/altered the expected length.
        var len = doc.Table.Length;
        doc.Selection = new Selection(
            Math.Clamp(entry.CaretAnchor, 0, len),
            Math.Clamp(entry.CaretActive, 0, len));

        var tab = new TabState(doc, entry.FilePath, entry.DisplayName) {
            Id = entry.Id,
            BaseSha1 = entry.BaseSha1,
            IsDirty = entry.IsDirty,
            ScrollOffsetY = entry.ScrollOffsetY,
            WinTopLine = entry.WinTopLine,
            WinScrollOffset = entry.WinScrollOffset,
            WinRenderOffsetY = entry.WinRenderOffsetY,
            WinFirstLineHeight = entry.WinFirstLineHeight,
        };

        return new RestoredTab { Tab = tab, Conflict = conflict };
    }

    private static Document LoadDocumentFromDisk(string path) {
        try {
            var result = FileLoader.Load(path);
            return result.Document;
        } catch {
            return new Document();
        }
    }

    private static void ReplayEdits(Document doc, TabEntry entry) {
        var editsPath = Path.Combine(SessionDir, $"{entry.Id}.edits.json");
        if (!File.Exists(editsPath)) {
            return;
        }
        try {
            var json = File.ReadAllText(editsPath);
            var (undo, redo) = EditSerializer.Deserialize(json);
            doc.History.RestoreEntries(doc.Table, undo, redo, entry.SavePointDepth);
        } catch {
            // Edit replay failed — document stays at base content.
        }
    }

    // -----------------------------------------------------------------
    // Cleanup
    // -----------------------------------------------------------------

    /// <summary>Removes the session directory entirely.</summary>
    public static void Clear() {
        try {
            if (Directory.Exists(SessionDir)) {
                Directory.Delete(SessionDir, recursive: true);
            }
        } catch {
            // Best-effort.
        }
    }

    private static void CleanupStaleFiles(HashSet<string> referencedIds) {
        try {
            foreach (var file in Directory.GetFiles(SessionDir, "*.edits.json")) {
                var name = Path.GetFileNameWithoutExtension(file);
                // name is like "a1b2c3d4e5f6.edits" — strip ".edits"
                var id = name.EndsWith(".edits") ? name[..^6] : name;
                if (!referencedIds.Contains(id)) {
                    File.Delete(file);
                }
            }
        } catch {
            // Best-effort.
        }
    }
}
