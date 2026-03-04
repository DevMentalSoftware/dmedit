using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App;

/// <summary>
/// Per-tab state for the tabbed document interface. Holds the document,
/// file path, scroll position, and dirty flag for one open tab.
/// </summary>
public sealed class TabState {
    private static int _nextUntitledNumber = 1;

    public Document Document { get; }
    public string? FilePath { get; set; }
    public string DisplayName { get; set; }
    public LoadResult? LoadResult { get; set; }
    public bool IsDirty { get; set; }

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

    /// <summary>Creates a new tab for a blank untitled document.</summary>
    public static TabState CreateUntitled() {
        var num = _nextUntitledNumber++;
        var name = num == 1 ? "Untitled" : $"Untitled {num}";
        return new TabState(new Document(), null, name);
    }
}
