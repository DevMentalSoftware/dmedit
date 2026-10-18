using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Touch drags pan the view instead of selecting; a tap places the caret
/// (GH #25).
/// </summary>
public class TouchPanTests {
    private const double VpW = 600;
    private const double VpH = 400;

    private static EditorControl CreateEditor() {
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            sb.Append($"line {i:D3} ").Append('x', 200).Append('\n');
        }
        var doc = new TextDocument();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = false,
        };
        Relayout(editor);
        return editor;
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
    }

    [AvaloniaFact]
    public void Drag_ScrollsWithoutSelecting() {
        var editor = CreateEditor();
        var selBefore = editor.Document!.Selection;

        editor.TouchPressForTest(new Point(300, 300));
        editor.TouchMoveForTest(new Point(300, 250));
        editor.TouchMoveForTest(new Point(300, 100));
        editor.TouchReleaseForTest(new Point(300, 100));

        // Content follows the finger: a 200 DIP upward drag scrolls down
        // by 200, measured from the press point (the slop isn't lost).
        Assert.True(editor.ScrollValue > 0);
        Assert.Equal(200, editor.ScrollValue, precision: 1);
        Assert.Equal(selBefore, editor.Document.Selection);
    }

    [AvaloniaFact]
    public void HorizontalDrag_ScrollsHorizontallyWhenNotWrapping() {
        var editor = CreateEditor();

        editor.TouchPressForTest(new Point(400, 200));
        editor.TouchMoveForTest(new Point(300, 200));
        editor.TouchReleaseForTest(new Point(300, 200));

        Assert.True(editor.HScrollValue > 0);
        Assert.Equal(0, editor.ScrollValue);
    }

    [AvaloniaFact]
    public void Tap_PlacesCaret() {
        var editor = CreateEditor();
        var rh = editor.RowHeightValue;
        var pt = new Point(300, rh * 5 + rh / 2);

        editor.TouchPressForTest(pt);
        editor.TouchReleaseForTest(pt);

        var doc = editor.Document!;
        Assert.True(doc.Selection.IsEmpty);
        Assert.Equal(5, doc.Table.LineFromOfs(doc.Selection.Caret));
        Assert.Equal(0, editor.ScrollValue);
    }

    [AvaloniaFact]
    public void SmallWobble_IsStillATap() {
        var editor = CreateEditor();
        var rh = editor.RowHeightValue;
        var pt = new Point(300, rh * 3 + rh / 2);

        editor.TouchPressForTest(pt);
        editor.TouchMoveForTest(pt + new Vector(3, -2));
        editor.TouchReleaseForTest(pt + new Vector(3, -2));

        var doc = editor.Document!;
        Assert.Equal(3, doc.Table.LineFromOfs(doc.Selection.Caret));
        Assert.Equal(0, editor.ScrollValue);
    }
}
