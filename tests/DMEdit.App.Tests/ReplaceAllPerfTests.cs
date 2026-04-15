using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// End-to-end tests for ReplaceAll via EditorControl: correctness after
/// bulk replace, editor state (not blocked, can edit), scrolling through
/// a fragmented piece table, and FindNext/FindPrev after replace.
/// </summary>
public class ReplaceAllPerfTests {
    private const double W = 800;
    private const double H = 400;

    private static EditorControl CreateEditor(string text) {
        var doc = new Document();
        doc.Insert(text);
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = W,
            Height = H,
        };
        editor.Measure(new Size(W, H));
        editor.Arrange(new Rect(0, 0, W, H));
        return editor;
    }

    private static void Relayout(EditorControl e) {
        e.Measure(new Size(W, H));
        e.Arrange(new Rect(0, 0, W, H));
    }

    private static EditorControl CreateDocWithNeedle(
            int lineCount, string needle, string filler = "some text here") {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            sb.Append($"L{i:D5} {filler} {needle} {filler}\n");
        }
        return CreateEditor(sb.ToString());
    }

    // =================================================================
    //  ReplaceAll correctness
    // =================================================================

    [AvaloniaFact]
    public async Task ReplaceAll_ReplacesAllOccurrences() {
        var e = CreateDocWithNeedle(100, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        var count = await e.ReplaceAllAsync("REPLACED");
        Assert.Equal(100, count);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_SameLengthReplacement_LengthUnchanged() {
        var e = CreateDocWithNeedle(50, "OLD");
        var lenBefore = e.Document!.Table.Length;
        e.LastSearchTerm = "OLD";
        await e.ReplaceAllAsync("NEW");
        Assert.Equal(lenBefore, e.Document.Table.Length);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_LineCountUnchanged() {
        var e = CreateDocWithNeedle(50, "NEEDLE");
        var linesBefore = e.Document!.Table.LineCount;
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("REPLACED");
        Assert.Equal(linesBefore, e.Document.Table.LineCount);
    }

    // =================================================================
    //  Editor state after ReplaceAll
    // =================================================================

    [AvaloniaFact]
    public async Task ReplaceAll_EditorNotBlocked() {
        var e = CreateDocWithNeedle(10, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("X");
        Assert.False(e.IsEditBlocked);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_CanEditAfter() {
        var e = CreateDocWithNeedle(10, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("X");
        e.Document!.Selection = Selection.Collapsed(0);
        e.Document.Insert("Z");
        Assert.Equal('Z', e.Document.Table.GetText(0, 1)[0]);
    }

    // =================================================================
    //  Scrolling after ReplaceAll (fragmented piece table)
    // =================================================================

    [AvaloniaFact]
    public async Task ReplaceAll_ScrollToMiddle_NoCrash() {
        var e = CreateDocWithNeedle(200, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("X");
        Relayout(e);
        e.ScrollValue = e.ScrollMaximum / 2;
        Relayout(e);
        Assert.True(e.Document!.Table.Length > 0);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_ScrollToEnd_NoCrash() {
        var e = CreateDocWithNeedle(200, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("X");
        Relayout(e);
        e.ScrollValue = e.ScrollMaximum;
        Relayout(e);
        Assert.True(e.Document!.Table.Length > 0);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_ScrollRoundTrip_NoCrash() {
        var e = CreateDocWithNeedle(200, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("X");
        Relayout(e);
        e.ScrollValue = e.ScrollMaximum;
        Relayout(e);
        e.ScrollValue = 0;
        Relayout(e);
        Assert.True(e.Document!.Table.Length > 0);
    }

    [AvaloniaFact]
    public async Task ReplaceAll_ReadLastLine_Correct() {
        var e = CreateDocWithNeedle(100, "NEEDLE");
        e.LastSearchTerm = "NEEDLE";
        await e.ReplaceAllAsync("DONE");

        var table = e.Document!.Table;
        var lastLineIdx = (int)(table.LineCount - 2);
        var lineStart = table.LineStartOfs(lastLineIdx);
        var lineLen = table.LineContentLength(lastLineIdx);
        if (lineLen > 0) {
            var lineText = table.GetText(lineStart, lineLen);
            Assert.Contains("DONE", lineText);
            Assert.DoesNotContain("NEEDLE", lineText);
        }
    }

    // =================================================================
    //  FindNext/FindPrevious after ReplaceAll
    // =================================================================

    [AvaloniaFact]
    public async Task FindNext_AfterReplaceAll_FindsNewText() {
        var e = CreateDocWithNeedle(10, "OLD");
        e.LastSearchTerm = "OLD";
        await e.ReplaceAllAsync("NEW");
        e.LastSearchTerm = "NEW";
        Assert.True(e.FindNext());
        Assert.Equal("NEW", e.Document!.GetSelectedText());
    }

    [AvaloniaFact]
    public async Task FindPrev_AfterReplaceAll_FindsNewText() {
        var e = CreateDocWithNeedle(10, "OLD");
        e.LastSearchTerm = "OLD";
        await e.ReplaceAllAsync("NEW");
        e.Document!.Selection = Selection.Collapsed(e.Document.Table.Length);
        e.LastSearchTerm = "NEW";
        Assert.True(e.FindPrevious());
        Assert.Equal("NEW", e.Document.GetSelectedText());
    }
}
