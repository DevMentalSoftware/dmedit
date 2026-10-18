using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tab / Smart Indent over a multi-line selection shift every line one
/// level and keep the block's relative indentation (GH #24).
/// </summary>
public class MultiLineIndentTests {
    private const string Source =
        "if (x) {\n" +
        "    a();\n" +
        "    if (y) {\n" +
        "        b();\n" +
        "    }\n" +
        "}\n";

    private static EditorControl CreateEditor(string content) {
        var doc = new TextDocument();
        doc.Insert(content);
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = 600,
            Height = 400,
            WrapLines = false,
        };
        editor.Measure(new Size(600, 400));
        editor.Arrange(new Rect(0, 0, 600, 400));
        return editor;
    }

    private static string[] Lines(EditorControl e) {
        var table = e.Document!.Table;
        return table.GetText(0, (int)table.Length).Split('\n');
    }

    private static int Depth(string line) {
        var n = 0;
        foreach (var ch in line) {
            if (ch == ' ') {
                n++;
            } else if (ch == '\t') {
                n += 4;
            } else {
                break;
            }
        }
        return n;
    }

    private static void AssertEveryLineShiftedOnce(string[] before, string[] after) {
        Assert.Equal(before.Length, after.Length);
        var shift = Depth(after[0]) - Depth(before[0]);
        Assert.True(shift > 0, "first line was not indented");
        // Lines 0..4 were selected; every one moves by the same amount.
        for (var i = 0; i <= 4; i++) {
            Assert.Equal(before[i].TrimStart(), after[i].TrimStart());
            Assert.Equal(Depth(before[i]) + shift, Depth(after[i]));
        }
        // The closing brace on line 5 was outside the selection.
        Assert.Equal(before[5], after[5]);
    }

    private static void SelectFirstFiveLines(EditorControl editor) {
        var doc = editor.Document!;
        var end = doc.Table.LineStartOfs(5);
        doc.Selection = new Selection(0, end);
    }

    [AvaloniaFact]
    public void SmartIndent_MultiLine_PreservesRelativeIndent() {
        var editor = CreateEditor(Source);
        var before = Lines(editor);
        SelectFirstFiveLines(editor);

        editor.SmartIndentForTest();

        AssertEveryLineShiftedOnce(before, Lines(editor));
    }

    [AvaloniaFact]
    public void Tab_MultiLine_IndentsInsteadOfReplacing() {
        var editor = CreateEditor(Source);
        var before = Lines(editor);
        SelectFirstFiveLines(editor);

        editor.TabForTest();

        AssertEveryLineShiftedOnce(before, Lines(editor));
    }

    [AvaloniaFact]
    public void Tab_SingleLineSelection_ReplacesWithTab() {
        var editor = CreateEditor("abc def\n");
        editor.Document!.Selection = new Selection(0, 3);

        editor.TabForTest();

        Assert.Equal("\t def\n", string.Join('\n', Lines(editor)));
    }

    [AvaloniaFact]
    public void SmartIndent_MultiLine_UndoIsSingleStep() {
        var editor = CreateEditor(Source);
        SelectFirstFiveLines(editor);

        editor.SmartIndentForTest();
        editor.Document!.Undo();

        Assert.Equal(Source, string.Join('\n', Lines(editor)));
    }
}
