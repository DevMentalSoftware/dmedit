using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;
using DMEdit.Core.IO;

namespace DMEdit.App.Services;

/// <summary>
/// Persists and restores the set of open tabs (including dirty documents'
/// edit history) across application restarts.
/// Storage location: <c>%LOCALAPPDATA%/DMEdit/session/</c>.
/// </summary>
public static class SessionStore {
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DMEdit", "session");

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
        public double ScrollOffsetX { get; set; }
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
                if (tab.IsSettings || tab.IsLocked) {
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
                    ScrollOffsetX = tab.ScrollOffsetX,
                    ScrollOffsetY = tab.ScrollOffsetY,
                    WinTopLine = tab.WinTopLine,
                    WinScrollOffset = tab.WinScrollOffset,
                    WinRenderOffsetY = tab.WinRenderOffsetY,
                    WinFirstLineHeight = tab.WinFirstLineHeight,
                    SavePointDepth = tab.Document.History.SavePointDepth,
                };
                manifest.Tabs.Add(entry);
                referencedIds.Add(tab.Id);

                // Skip edit serialization for tabs still loading — edits
                // haven't been replayed yet, so the previous session's
                // edit file (if any) is still valid.
                if (tab.IsLoading) {
                    continue;
                }

                // Persist only edits above the save point — the saved file
                // on disk is the correct base for replay on next restore.
                var savePointDepth = tab.Document.History.SavePointDepth;
                var undoEntries = tab.Document.History.GetUndoEntries();
                var redoEntries = tab.Document.History.GetRedoEntries();

                IReadOnlyList<EditHistory.HistoryEntry> persistUndo;
                IReadOnlyList<EditHistory.HistoryEntry> persistRedo;

                if (savePointDepth > 0 && undoEntries.Count >= savePointDepth) {
                    persistUndo = undoEntries.Skip(savePointDepth).ToList();
                    persistRedo = redoEntries;
                    entry.SavePointDepth = 0;
                } else if (savePointDepth > 0 && undoEntries.Count < savePointDepth) {
                    persistUndo = Array.Empty<EditHistory.HistoryEntry>();
                    persistRedo = Array.Empty<EditHistory.HistoryEntry>();
                    entry.SavePointDepth = 0;
                    entry.IsDirty = false;
                } else {
                    persistUndo = undoEntries;
                    persistRedo = redoEntries;
                }

                var editsPath = Path.Combine(SessionDir, $"{tab.Id}.edits.json");
                var addBufPath = Path.Combine(SessionDir, $"{tab.Id}.addBuf");
                if (persistUndo.Count > 0 || persistRedo.Count > 0) {
                    var editsJson = EditSerializer.Serialize(persistUndo, persistRedo, tab.Document.Table);
                    File.WriteAllText(editsPath, editsJson);
                    // Persist the active (small) add buffer for typing edits.
                    // Large paste buffers are already on disk as .addBuf.N files.
                    var addBuf = tab.Document.Table.AddBuffer;
                    if (addBuf.CharLength > 0) {
                        using var fs = File.Create(addBufPath);
                        addBuf.WriteTo(fs);
                    } else if (File.Exists(addBufPath)) {
                        File.Delete(addBufPath);
                    }
                } else {
                    if (File.Exists(editsPath)) File.Delete(editsPath);
                    if (File.Exists(addBufPath)) File.Delete(addBufPath);
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
    // Load — two-phase restore
    // -----------------------------------------------------------------

    /// <summary>
    /// Phase 1: reads the manifest only (no file I/O). Returns the tab
    /// entries and active index, or null if no session exists.
    /// </summary>
    public static Task<(List<TabEntry> Entries, int ActiveTabIndex)?> LoadManifestAsync() {
        try {
            if (!File.Exists(ManifestPath)) {
                return Task.FromResult<(List<TabEntry>, int)?>(null);
            }
            var json = File.ReadAllText(ManifestPath);
            var manifest = JsonSerializer.Deserialize<SessionManifest>(json, JsonOpts);
            if (manifest is null || manifest.Tabs.Count == 0) {
                return Task.FromResult<(List<TabEntry>, int)?>(null);
            }
            return Task.FromResult<(List<TabEntry>, int)?>((manifest.Tabs, manifest.ActiveTabIndex));
        } catch {
            return Task.FromResult<(List<TabEntry>, int)?>(null);
        }
    }

    /// <summary>
    /// Phase 2a: creates a <see cref="TabState"/> from a manifest entry.
    /// For file-backed tabs that exist on disk, the background scan starts
    /// immediately but the method returns without waiting for it to finish.
    /// The tab's <see cref="TabState.IsLoading"/> is <c>true</c> until
    /// <see cref="FinishLoadAsync"/> completes.
    /// </summary>
    public static TabState CreateTabFromEntry(TabEntry entry) {
        Document doc;
        FileConflict? conflict = null;
        var isLoading = false;
        LoadResult? loadResult = null;

        if (entry.FilePath is not null) {
            if (!File.Exists(entry.FilePath)) {
                conflict = new FileConflict {
                    Kind = FileConflictKind.Missing,
                    FilePath = entry.FilePath,
                    ExpectedSha1 = entry.BaseSha1,
                };
                doc = new Document();
            } else {
                // Start loading — returns immediately, scan runs on background thread.
                try {
                    loadResult = FileLoader.LoadAsync(entry.FilePath).GetAwaiter().GetResult();
                    doc = loadResult.Document;
                    isLoading = true;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // Treat like a missing file — show conflict so the user can relocate.
                    conflict = new FileConflict {
                        Kind = FileConflictKind.Missing,
                        FilePath = entry.FilePath,
                        ExpectedSha1 = entry.BaseSha1,
                    };
                    doc = new Document();
                }
            }
        } else {
            // Untitled tab — replay edits from empty, no loading needed.
            doc = new Document();
            conflict = ReplayEdits(doc, entry);
            RestoreSelection(doc, entry);
        }

        bool ro = false;
        if (entry.FilePath is not null && conflict is null) {
            try {
                ro = (File.GetAttributes(entry.FilePath) & FileAttributes.ReadOnly) != 0;
            } catch {
            }
        }

        var hasPendingEdits = isLoading &&
            File.Exists(Path.Combine(SessionDir, $"{entry.Id}.edits.json"));

        return new TabState(doc, entry.FilePath, entry.DisplayName) {
            Id = entry.Id,
            BaseSha1 = entry.BaseSha1,
            IsDirty = entry.IsDirty,
            IsReadOnly = ro,
            Conflict = conflict,
            IsLoading = isLoading,
            HasPendingEdits = hasPendingEdits,
            LoadResult = loadResult,
            ScrollOffsetX = entry.ScrollOffsetX,
            ScrollOffsetY = entry.ScrollOffsetY,
            WinTopLine = entry.WinTopLine,
            WinScrollOffset = entry.WinScrollOffset,
            WinRenderOffsetY = entry.WinRenderOffsetY,
            WinFirstLineHeight = entry.WinFirstLineHeight,
        };
    }

    /// <summary>
    /// Phase 2b: performs conflict detection and edit replay after the
    /// file scan has completed. Must be called on the UI thread (edit
    /// replay mutates the document) and only after
    /// <c>tab.LoadResult.Loaded</c> has finished. Calls
    /// <see cref="TabState.FinishLoading"/> when done.
    /// </summary>
    public static void FinishLoad(TabState tab, TabEntry entry) {
        if (tab.LoadResult is null) {
            tab.FinishLoading();
            return;
        }

        // SHA-1 is now available from the completed scan.
        var currentSha1 = tab.LoadResult.BaseSha1;
        tab.Document.LineEndingInfo = tab.LoadResult.Document.LineEndingInfo;

        if (entry.BaseSha1 is not null && currentSha1 != entry.BaseSha1) {
            if (!entry.IsDirty) {
                // No unsaved edits — silently accept the disk version.
                tab.BaseSha1 = currentSha1 ?? entry.BaseSha1;
            } else {
                // Unsaved edits exist — flag the conflict so the user
                // can decide whether to keep their edits or load the
                // disk version.
                tab.Conflict = new FileConflict {
                    Kind = FileConflictKind.Changed,
                    FilePath = entry.FilePath!,
                    ExpectedSha1 = entry.BaseSha1,
                    ActualSha1 = currentSha1,
                };
                // Keep the original BaseSha1 so the conflict persists
                // across restarts until the user explicitly resolves it.
                tab.BaseSha1 = entry.BaseSha1;
            }
            // Disk version is already loaded; don't replay edits.
        } else {
            // SHA-1 match — safe to replay edits.
            tab.BaseSha1 = currentSha1 ?? entry.BaseSha1;
            var replayConflict = ReplayEdits(tab.Document, entry);
            if (replayConflict != null) {
                tab.Conflict = replayConflict;
            }
        }

        RestoreSelection(tab.Document, entry);
        tab.HasPendingEdits = false;
        tab.FinishLoading();
    }

    private static void RestoreSelection(Document doc, TabEntry entry) {
        var docLen = doc.Table.DocLength;
        doc.Selection = new Selection(
            Math.Clamp(entry.CaretAnchor, 0, docLen),
            Math.Clamp(entry.CaretActive, 0, docLen));
    }

    /// <summary>
    /// Replays serialized edits onto the document.  Returns a <see cref="FileConflict"/>
    /// if replay fails (so the caller can surface it to the user), or null on success.
    /// </summary>
    private static FileConflict? ReplayEdits(Document doc, TabEntry entry) {
        var editsPath = Path.Combine(SessionDir, $"{entry.Id}.edits.json");
        if (!File.Exists(editsPath)) {
            return null;
        }
        try {
            // Load the base add buffer (small edits from typing) into the
            // active in-memory add buffer.  This keeps it at buffer index 1
            // so edits with the default BufIdx (-1) reference it directly,
            // new typing appends after the old content, and on the next save
            // the full combined buffer is persisted correctly.
            var addBufPath = Path.Combine(SessionDir, $"{entry.Id}.addBuf");
            if (File.Exists(addBufPath)) {
                var bytes = File.ReadAllBytes(addBufPath);
                doc.Table.AddBuffer.AppendUtf8(bytes);
            }

            // Load large-paste add buffers (.addBuf.N files).
            // These were written at paste time and are already on disk.
            // Register them at the same buffer indices they had originally
            // (the base .addBuf no longer consumes a slot, so indices match).
            foreach (var file in Directory.GetFiles(SessionDir, $"{entry.Id}.addBuf.*")) {
                var ext = Path.GetExtension(file);
                // Parse the index from the extension (e.g., ".2" from ".addBuf.2").
                var idxStr = ext.TrimStart('.');
                if (!int.TryParse(idxStr, out _)) continue;
                LoadPagedBuffer(doc, file);
            }

            var json = File.ReadAllText(editsPath);
            var (undo, redo) = EditSerializer.Deserialize(json);
            // The line tree must already be installed before we get here.
            // MainWindow installs it from the paged buffer scan results
            // before calling FinishLoad.  If the tree isn't present,
            // Insert/Delete/CaptureLineInfo silently skip line-tree
            // maintenance, corrupting undo state.
            doc.History.RestoreEntries(doc.Table, undo, redo, entry.SavePointDepth);
            return null;
        } catch {
            // Edit replay failed — document stays at base content.
            return new FileConflict {
                Kind = FileConflictKind.EditReplayFailed,
                FilePath = entry.FilePath ?? "(untitled)",
                ExpectedSha1 = entry.BaseSha1,
            };
        }
    }

    // -----------------------------------------------------------------
    // Cleanup
    // -----------------------------------------------------------------

    /// <summary>
    /// Loads a file as a PagedFileBuffer and registers it in the table's buffer list.
    /// Returns the buffer index, or -1 if the file is empty.
    /// </summary>
    private static int LoadPagedBuffer(Document doc, string path) {
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= 0) return -1;
        var paged = new PagedFileBuffer(path, byteLen);
        var tcs = new TaskCompletionSource();
        paged.LoadComplete += () => tcs.TrySetResult();
        paged.StartLoading(default);
        tcs.Task.GetAwaiter().GetResult();
        return doc.Table.RegisterBuffer(paged);
    }

    /// <summary>
    /// Returns a path for a new add buffer file in the session directory.
    /// Creates the directory if needed.
    /// </summary>
    public static string AllocateAddBufPath(string tabId, int index) {
        Directory.CreateDirectory(SessionDir);
        return Path.Combine(SessionDir, $"{tabId}.addBuf.{index}");
    }

    /// <summary>Deletes session files for a single tab (on tab close).</summary>
    public static void DeleteTabFiles(string tabId) {
        try {
            var editsPath = Path.Combine(SessionDir, $"{tabId}.edits.json");
            if (File.Exists(editsPath)) File.Delete(editsPath);
            // Delete .addBuf and all .addBuf.N files.
            foreach (var f in Directory.GetFiles(SessionDir, $"{tabId}.addBuf*")) {
                File.Delete(f);
            }
        } catch {
            // Best-effort.
        }
    }

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
            foreach (var file in Directory.GetFiles(SessionDir, "*.addBuf*")) {
                // Extract tab ID: "abc123.addBuf" or "abc123.addBuf.2"
                var name = Path.GetFileName(file);
                var dotIdx = name.IndexOf('.');
                var id = dotIdx >= 0 ? name[..dotIdx] : name;
                if (!referencedIds.Contains(id)) {
                    File.Delete(file);
                }
            }
        } catch {
            // Best-effort.
        }
    }
}
