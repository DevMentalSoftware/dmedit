using System;
using System.Collections.Generic;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App;

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
    public bool IsSettings { get; init; }

    /// <summary>
    /// SHA-1 hash of the file's raw bytes at load time (lowercase hex).
    /// <c>null</c> for untitled documents. Used by session persistence
    /// to detect external modifications between sessions.
    /// </summary>
    public string? BaseSha1 { get; set; }

    // Scroll / windowed-layout state, saved when leaving the tab so
    // returning does not produce a visual jump.
    public double ScrollOffsetY { get; set; }
    public long WinTopLine { get; set; } = -1;
    public double WinScrollOffset { get; set; }
    public double WinRenderOffsetY { get; set; }
    public double WinFirstLineHeight { get; set; }

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
