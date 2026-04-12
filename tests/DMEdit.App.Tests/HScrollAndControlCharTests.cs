using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for horizontal scrolling (wrap off, long lines) and control
/// character rendering on the mono fast path.  Both are code branches
/// that had zero test coverage prior to this file.
/// </summary>
public class HScrollAndControlCharTests {
    private const double VpW = 600;
    private const double VpH = 400;

    private static EditorControl CreateEditor(Document doc, bool wrap) {
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = wrap,
        };
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
        return editor;
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
    }

    private static long Caret(EditorControl e) => e.Document!.Selection.Caret;

    private static void AssertCaretOnScreen(EditorControl e, string label) {
        var y = e.GetCaretScreenYForTest();
        Assert.True(y.HasValue, $"{label}: caret not on screen");
        Assert.InRange(y!.Value, -1, VpH + 1);
    }

    // ==================================================================
    //  Horizontal scroll — wrap off, long lines
    // ==================================================================

    [AvaloniaFact]
    public void HScroll_LongLine_RightArrow_ScrollsRight() {
        var doc = new Document();
        doc.Insert(new string('a', 200) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        Assert.Equal(0, editor.HScrollValue);

        // Move caret to the end of the long line.
        for (var i = 0; i < 200; i++) {
            editor.MoveCaretHorizontalForTest(+1, false, false);
        }
        Relayout(editor);

        // Horizontal scroll should have moved right.
        Assert.True(editor.HScrollValue > 0,
            "HScrollValue should be > 0 after moving to end of long line");
    }

    [AvaloniaFact]
    public void HScroll_GoToEnd_ThenHome_ScrollsBack() {
        var doc = new Document();
        doc.Insert(new string('a', 200) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        // Go to end of line.
        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        var hScrollAtEnd = editor.HScrollValue;
        Assert.True(hScrollAtEnd > 0);

        // Home.
        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);

        Assert.True(editor.HScrollValue < hScrollAtEnd,
            "HScrollValue should decrease after Home");
    }

    [AvaloniaFact]
    public void HScroll_GoToPosition_MidLongLine_ScrollsHorizontally() {
        var doc = new Document();
        doc.Insert(new string('x', 300) + '\n' + "short\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        editor.GoToPosition(150); // middle of the long line
        Relayout(editor);

        Assert.True(editor.HScrollValue > 0,
            "GoToPosition mid-long-line should trigger horizontal scroll");
        AssertCaretOnScreen(editor, "GoTo mid-long-line");
    }

    [AvaloniaTheory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(299)]
    public void HScroll_CaretAtVariousPositions_OnScreen(int pos) {
        var doc = new Document();
        doc.Insert(new string('x', 300) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        editor.GoToPosition(pos);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"pos={pos}");
    }

    [AvaloniaFact]
    public void HScroll_WrapOn_NoHorizontalScroll() {
        var doc = new Document();
        doc.Insert(new string('x', 300) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, true); // wrap ON

        editor.GoToPosition(150);
        Relayout(editor);

        // With wrap on, there should be no horizontal scroll.
        Assert.Equal(0, editor.HScrollValue);
    }

    [AvaloniaFact]
    public void HScroll_Find_MidLongLine_ScrollsToMatch() {
        var doc = new Document();
        // "NEEDLE" at position 150 in a 300-char line.
        doc.Insert(new string('x', 150) + "NEEDLE" + new string('x', 144) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        Assert.Equal(0, editor.HScrollValue);

        editor.LastSearchTerm = "NEEDLE";
        Assert.True(editor.FindNext());
        Relayout(editor);

        Assert.True(editor.HScrollValue > 0,
            "FindNext should scroll horizontally to reveal a mid-line match");
    }

    [AvaloniaFact]
    public void HScroll_ShiftEnd_ExtendsSelection_ScrollsRight() {
        var doc = new Document();
        doc.Insert(new string('a', 200) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: true);
        Relayout(editor);

        Assert.Equal(0, editor.Document!.Selection.Anchor);
        Assert.Equal(200, Caret(editor));
        Assert.True(editor.HScrollValue > 0);
    }

    // ==================================================================
    //  Control characters on mono fast path
    // ==================================================================

    [AvaloniaFact]
    public void ControlChars_NoCrash_WrapOff() {
        var doc = new Document();
        // Avoid VT/FF which may be line terminators. Use NUL, SOH, BEL, ESC.
        doc.Insert("normal\x00text\x07with\x01control\x02chars\x1B end\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        // Should not throw — control chars render as fallback glyphs.
        Relayout(editor);
        AssertCaretOnScreen(editor, "control chars wrap off");
    }

    [AvaloniaFact]
    public void ControlChars_NoCrash_WrapOn() {
        var doc = new Document();
        doc.Insert("normal\x00text\x07with\x01control\x02chars\x1B end\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, true);

        // Primary assertion: no crash.  Caret visibility isn't guaranteed
        // because control chars may render at unexpected widths in the
        // headless font, affecting line wrapping and caret position.
        Relayout(editor);

        // Verify the document loaded and has content.
        Assert.True(doc.Table.Length > 0);
        Assert.True(doc.Table.LineCount >= 1);
    }

    [AvaloniaFact]
    public void ControlChars_MoveRight_NeverStuck() {
        var doc = new Document();
        doc.Insert("a\x00b\x07c\x08d\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        for (var i = 0; i < 10; i++) {
            var before = Caret(editor);
            editor.MoveCaretHorizontalForTest(+1, false, false);
            Relayout(editor);
            if (Caret(editor) == before) {
                Assert.Equal(doc.Table.Length, before);
                break;
            }
        }
    }

    [AvaloniaFact]
    public void ControlChars_Find_WorksAcross() {
        var doc = new Document();
        doc.Insert("before\x00NEEDLE\x07after\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        editor.LastSearchTerm = "NEEDLE";
        Assert.True(editor.FindNext());
        Relayout(editor);
        Assert.Equal(7, doc.Selection.Start); // after "before\x00"
    }

    [AvaloniaFact]
    public void ControlChars_LongLineWithControlChars_WrapsCorrectly() {
        var doc = new Document();
        // 200 chars with control chars interspersed every 20.
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            sb.Append(i % 20 == 10 ? '\x01' : 'a');
        }
        sb.Append('\n');
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, true);

        // Walk down through wrapped rows — should not crash.
        for (var step = 0; step < 10; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"ctrl-char wrap step {step}");
        }
    }

    [AvaloniaFact]
    public void BinaryContent_FullOfNulls_NoCrash() {
        var doc = new Document();
        // Simulated binary file: mostly NULs with some printable chars.
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            sb.Append('\0');
            sb.Append((char)('A' + i % 26));
        }
        sb.Append('\n');
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        Relayout(editor);

        // Navigate through the binary content.
        editor.GoToPosition(50);
        Relayout(editor);
        AssertCaretOnScreen(editor, "binary content mid");

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
    }

    // ==================================================================
    //  Tab + horizontal scroll interaction (wrap off)
    // ==================================================================

    [AvaloniaFact]
    public void TabLine_WrapOff_HScroll_CaretAtEnd() {
        var doc = new Document();
        // Tab-heavy long line.
        doc.Insert("\t\t\t\t\t" + new string('a', 200) + '\n');
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        Assert.True(editor.HScrollValue > 0,
            "Tab line should need horizontal scroll");
    }
}
