using System;
using System.Collections.Generic;
using DMEdit.App.Services;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;

namespace DMEdit.App;

/// <summary>
/// Per-tab state for the tabbed document interface. Holds the document,
/// file path, scroll position, and dirty flag for one open tab.
/// </summary>
public sealed class TabState {
    /// <summary>
    /// Stable identifier for this tab, used to name session-persistence files.
    /// 12-char lowercase hex derived from a GUID.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    public Document Document { get; }
    public string? FilePath { get; set; }
    public string DisplayName { get; set; }
    public LoadResult? LoadResult { get; set; }
    public bool IsDirty { get; set; }
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// True for internal documents (Manual, About) that must never be edited
    /// or saved. Unlike <see cref="IsReadOnly"/>, locked state cannot be
    /// toggled off by the user.
    /// </summary>
    public bool IsLocked { get; init; }

    public bool IsSettings { get; init; }

    /// <summary>
    /// When true, the tab is pinned: it shows a pin icon, groups left
    /// after the Settings tab, and is exempt from "Close Unpinned" commands.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// SHA-1 hash of the file's raw bytes at load time (lowercase hex).
    /// <c>null</c> for untitled documents. Used by session persistence
    /// to detect external modifications between sessions.
    /// </summary>
    public string? BaseSha1 { get; set; }

    /// <summary>
    /// Last-write time of the file when loaded or saved. Used by the file
    /// watcher for cheap change detection (paired with <see cref="BaseFileSize"/>).
    /// </summary>
    public DateTime BaseLastWriteTimeUtc { get; set; }

    /// <summary>
    /// File size in bytes when loaded or saved. Used with
    /// <see cref="BaseLastWriteTimeUtc"/> for cheap change detection.
    /// </summary>
    public long BaseFileSize { get; set; }

    /// <summary>
    /// Non-null when the base file was missing or changed at session restore.
    /// The tab bar draws an error icon and offers resolution via context menu.
    /// </summary>
    public FileConflict? Conflict { get; set; }

    /// <summary>
    /// When set to a future time, the tab bar briefly shows the loading spinner
    /// to indicate the file was modified externally. Resets naturally when the
    /// time passes.
    /// </summary>
    public DateTime FlashReloadUntil { get; set; }

    /// <summary>
    /// True while the file's background scan is still in progress.
    /// The tab bar shows a spinner and the editor blocks input.
    /// </summary>
    public bool IsLoading { get; set; }

    /// <summary>
    /// True when this tab's document should use character-wrapping mode.
    /// Set after load completes based on file size and longest line.
    /// </summary>
    public bool CharWrapMode { get; set; }

    /// <summary>
    /// True when the tab has session edits waiting to be replayed after load.
    /// Suppresses incremental rendering so the user doesn't see the base file
    /// content flash before edits are applied.
    /// </summary>
    public bool HasPendingEdits { get; set; }

    /// <summary>
    /// Fires on the UI thread when loading finishes and the tab becomes
    /// interactive (conflict detection and edit replay are complete).
    /// </summary>
    public event Action? LoadCompleted;

    /// <summary>
    /// True while an auto-reload is in progress. Prevents re-entrant
    /// reloads when external changes arrive faster than we can process.
    /// </summary>
    public bool ReloadInProgress { get; set; }

    /// <summary>
    /// UTC time when the last auto-reload finished. Used with
    /// <see cref="Services.AppSettings.TailReloadCooldownMs"/> to
    /// enforce a minimum interval between reloads.
    /// </summary>
    public DateTime LastReloadFinishedUtc { get; set; }

    // Scroll / windowed-layout state, saved when leaving the tab so
    // returning does not produce a visual jump.
    public double ScrollOffsetX { get; set; }
    public double ScrollOffsetY { get; set; }
    public long WinTopLine { get; set; } = -1;
    public double WinScrollOffset { get; set; }
    public double WinRenderOffsetY { get; set; }
    public double WinFirstLineHeight { get; set; }

    /// <summary>Marks loading complete and fires <see cref="LoadCompleted"/>.</summary>
    public void FinishLoading() {
        IsLoading = false;
        LoadCompleted?.Invoke();
    }

    public TabState(Document document, string? filePath, string displayName) {
        Document = document;
        FilePath = filePath;
        DisplayName = displayName;
    }

    /// <summary>
    /// Creates a new tab for a blank untitled document. Picks the lowest
    /// available "Untitled" name that isn't already used by an open tab.
    /// </summary>
    public static TabState CreateUntitled(IReadOnlyList<TabState> existingTabs) {
        var used = new HashSet<int>();
        foreach (var tab in existingTabs) {
            if (tab.DisplayName == "Untitled") {
                used.Add(1);
            } else if (tab.DisplayName.StartsWith("Untitled ")
                       && int.TryParse(tab.DisplayName.Substring(9), out var n)) {
                used.Add(n);
            }
        }
        var num = 1;
        while (used.Contains(num)) num++;
        var name = num == 1 ? "Untitled" : $"Untitled {num}";
        return new TabState(new Document(), null, name);
    }

    /// <summary>
    /// Creates the singleton Settings tab.
    /// </summary>
    public static TabState CreateSettings() =>
        new(new Document(), null, "Settings") { IsSettings = true };
}
