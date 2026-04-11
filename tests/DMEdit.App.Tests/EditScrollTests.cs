using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests that editing operations (insert, delete, paste) keep the caret
/// on screen and don't corrupt scroll state.  Also covers very long
/// single lines, selection across viewport boundaries, mixed line
/// endings, and viewport resize with wrapped content.
/// </summary>
public class EditScrollTests {
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

    private static void RelayoutSize(EditorControl editor, double w, double h) {
        editor.Width = w;
        editor.Height = h;
        editor.Measure(new Size(w, h));
        editor.Arrange(new Rect(0, 0, w, h));
    }

    private static Document MakeDoc(int lineCount, int lineLen = 20) {
        var sb = new StringBuilder(lineCount * (lineLen + 1));
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D5} ";
            var padLen = Math.Max(0, lineLen - prefix.Length);
            sb.Append(prefix);
            sb.Append('a', padLen);
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    private static long Caret(EditorControl e) => e.Document!.Selection.Caret;

    private static void AssertCaretOnScreen(EditorControl e, string label) {
        var y = e.GetCaretScreenYForTest();
        Assert.True(y.HasValue, $"{label}: caret not on screen");
        Assert.InRange(y!.Value, -1, VpH + 1);
    }

    private static void AssertScrollConsistent(EditorControl e, string label) {
        Assert.True(e.ScrollValue >= 0,
            $"{label}: ScrollValue negative ({e.ScrollValue:F1})");
        Assert.True(e.ScrollValue <= e.ScrollMaximum + 1,
            $"{label}: ScrollValue ({e.ScrollValue:F1}) > ScrollMaximum ({e.ScrollMaximum:F1})");
    }

    // ==================================================================
    //  Insert text mid-document — caret stays on screen
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Insert_MidDoc_CaretOnScreen(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        // Position at line 100.
        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);

        // Insert text at the caret.
        doc.Insert("INSERTED TEXT ");
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after insert");
        AssertScrollConsistent(editor, $"wrap={wrap} after insert");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertNewline_MidDoc_CaretOnScreen(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        editor.GoToPosition(doc.Table.LineStartOfs(100) + 10);
        Relayout(editor);

        doc.Insert("\n");
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after newline insert");
        AssertScrollConsistent(editor, $"wrap={wrap} after newline insert");
    }

    // ==================================================================
    //  Delete at various positions — scroll stays consistent
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_MidDoc_ScrollConsistent(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);

        // Delete 5 characters forward.
        doc.Selection = new Selection(
            doc.Table.LineStartOfs(100),
            doc.Table.LineStartOfs(100) + 5);
        doc.DeleteForward();
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after delete");
        AssertScrollConsistent(editor, $"wrap={wrap} after delete");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteLine_NearEnd_ScrollConsistent(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        // Position near the end.
        editor.GoToPosition(doc.Table.LineStartOfs(198));
        Relayout(editor);

        // Delete the entire line.
        var lineStart = doc.Table.LineStartOfs(198);
        var lineEnd = doc.Table.LineStartOfs(199);
        doc.Selection = new Selection(lineStart, lineEnd);
        doc.DeleteForward();
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after line delete near end");
        AssertScrollConsistent(editor, $"wrap={wrap} after line delete near end");
    }

    // ==================================================================
    //  Large paste — scroll extent updates correctly
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargePaste_ExtentGrows(bool wrap) {
        var doc = MakeDoc(50, 20);
        var editor = CreateEditor(doc, wrap);
        Relayout(editor);
        var maxBefore = editor.ScrollMaximum;

        // Paste 100 new lines at the end.
        doc.Selection = Selection.Collapsed(doc.Table.Length);
        var paste = new StringBuilder();
        for (var i = 0; i < 100; i++) paste.Append($"PASTE{i:D3} line\n");
        doc.Insert(paste.ToString());
        editor.ScrollCaretIntoView();
        Relayout(editor);

        Assert.True(editor.ScrollMaximum > maxBefore,
            $"wrap={wrap}: extent didn't grow after paste");
        AssertScrollConsistent(editor, $"wrap={wrap} after paste");
    }

    // ==================================================================
    //  Very long single line (5000+ chars) — wrapping
    // ==================================================================

    [AvaloniaFact]
    public void VeryLongLine_WrapOn_DownWalk_CaretOnScreen() {
        var doc = new Document();
        // One line of 5000 chars — wraps to ~75 visual rows at 65 cpr.
        doc.Insert(new string('a', 5000) + "\nshort\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, true);

        for (var step = 0; step < 100; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"long-line Down step {step}");
        }
    }

    [AvaloniaFact]
    public void VeryLongLine_WrapOn_FindInLongLine_CaretOnScreen() {
        var doc = new Document();
        // Plant a needle deep in a 5000-char line.
        var line = new string('x', 2000) + "NEEDLE" + new string('x', 2994);
        doc.Insert(line + "\nshort\n");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, true);

        editor.LastSearchTerm = "NEEDLE";
        Assert.True(editor.FindNext());
        Relayout(editor);

        AssertCaretOnScreen(editor, "Find in long line");
        Assert.Equal(2000, doc.Selection.Start);
    }

    [AvaloniaFact]
    public void VeryLongLine_WrapOn_HomeEnd_CaretOnScreen() {
        var doc = new Document();
        doc.Insert(new string('a', 5000) + "\nshort\n");
        doc.Selection = Selection.Collapsed(2000); // mid-long-line
        var editor = CreateEditor(doc, true);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, "End on long line");

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, "Home on long line");
    }

    // ==================================================================
    //  Mixed line endings — scroll math correctness
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLineEndings_DownWalk_CaretOnScreen(bool wrap) {
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            var prefix = $"L{i:D4} ";
            var pad = new string('a', 20 - prefix.Length);
            sb.Append(prefix + pad);
            // Alternate between \n, \r\n, and \r.
            var ending = (i % 3) switch { 0 => "\n", 1 => "\r\n", _ => "\r" };
            sb.Append(ending);
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, wrap);

        for (var step = 0; step < 100; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"mixed-endings step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrLfLines_FindWalk_CaretOnScreen(bool wrap) {
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            sb.Append($"L{i:D4} NEEDLE padding text here\r\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        for (var step = 0; step < 50; step++) {
            if (!editor.FindNext()) break;
            Relayout(editor);
            AssertCaretOnScreen(editor, $"crlf-find step {step}");
        }
    }

    // ==================================================================
    //  Viewport resize with wrapped content
    // ==================================================================

    [AvaloniaFact]
    public void Resize_Narrower_WrappedDoc_CaretOnScreen() {
        var doc = MakeDoc(100, 80);
        var editor = CreateEditor(doc, true);

        // Position mid-document.
        editor.GoToPosition(doc.Table.LineStartOfs(50));
        Relayout(editor);
        AssertCaretOnScreen(editor, "before resize");

        // Shrink to half width — more wrapping.
        RelayoutSize(editor, 300, VpH);

        editor.ScrollCaretIntoView();
        RelayoutSize(editor, 300, VpH);
        AssertCaretOnScreen(editor, "after narrow resize");
        AssertScrollConsistent(editor, "after narrow resize");
    }

    [AvaloniaFact]
    public void Resize_Shorter_CaretOnScreen() {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, false);

        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);

        // Shrink viewport height — fewer visible rows.
        RelayoutSize(editor, VpW, 200);

        editor.ScrollCaretIntoView();
        RelayoutSize(editor, VpW, 200);

        var caretY = editor.GetCaretScreenYForTest();
        Assert.True(caretY.HasValue, "caret not on screen after height shrink");
        Assert.InRange(caretY!.Value, -1, 201);
        AssertScrollConsistent(editor, "after height shrink");
    }

    // ==================================================================
    //  Selection across viewport boundary
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectDown_AcrossViewport_ScrollFollows(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        // Start at line 10.
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(10));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        // Shift+Down 50 times — selection grows, caret scrolls down.
        for (var i = 0; i < 50; i++) {
            editor.MoveCaretVerticalForTest(+1, extend: true);
            Relayout(editor);
        }

        // Caret should be on screen (scrolled to follow the extending selection).
        AssertCaretOnScreen(editor, $"wrap={wrap} after 50 Shift+Down");
        // Anchor should still be at line 10.
        Assert.Equal(doc.Table.LineStartOfs(10), editor.Document!.Selection.Anchor);
        // Selection should span 50 lines.
        Assert.True(editor.Document.Selection.Len > 0);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectUp_AcrossViewport_ScrollFollows(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(100));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        for (var i = 0; i < 50; i++) {
            editor.MoveCaretVerticalForTest(-1, extend: true);
            Relayout(editor);
        }

        AssertCaretOnScreen(editor, $"wrap={wrap} after 50 Shift+Up");
        Assert.Equal(doc.Table.LineStartOfs(100), editor.Document!.Selection.Anchor);
    }

    // ==================================================================
    //  Scroll at document boundaries
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollToMax_ThenDown_NoOverscroll(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        // Scroll to the very bottom.
        editor.ScrollValue = editor.ScrollMaximum;
        Relayout(editor);

        // Position caret at the last line.
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(199));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        // Press Down — should be a no-op at the last line.
        var scrollBefore = editor.ScrollValue;
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);

        Assert.InRange(editor.ScrollValue, scrollBefore - 1, scrollBefore + 1);
        AssertScrollConsistent(editor, "at max + Down");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollAtZero_ThenUp_NoUnderscroll(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        Assert.InRange(editor.ScrollValue, -0.1, 0.1);

        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);

        Assert.InRange(editor.ScrollValue, -0.1, 0.1);
    }

    // ==================================================================
    //  Undo preserves scroll position
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_AfterInsert_CaretOnScreen(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);

        // Insert text.
        doc.Insert("hello");
        editor.ScrollCaretIntoView();
        Relayout(editor);

        // Undo.
        doc.Undo();
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after undo");
        AssertScrollConsistent(editor, $"wrap={wrap} after undo");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redo_AfterUndo_CaretOnScreen(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);

        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);

        doc.Insert("hello");
        editor.ScrollCaretIntoView();
        Relayout(editor);

        doc.Undo();
        editor.ScrollCaretIntoView();
        Relayout(editor);

        doc.Redo();
        editor.ScrollCaretIntoView();
        Relayout(editor);

        AssertCaretOnScreen(editor, $"wrap={wrap} after redo");
        AssertScrollConsistent(editor, $"wrap={wrap} after redo");
    }

    // ==================================================================
    //  ScrollMaximum stability across edits
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollMax_StableAcrossSmallEdits(bool wrap) {
        var doc = MakeDoc(200, 20);
        var editor = CreateEditor(doc, wrap);
        Relayout(editor);
        var initialMax = editor.ScrollMaximum;

        // Small edit mid-document shouldn't wildly change extent.
        editor.GoToPosition(doc.Table.LineStartOfs(100));
        Relayout(editor);
        doc.Insert("x");
        editor.ScrollCaretIntoView();
        Relayout(editor);

        // Extent should change by at most one row height.
        var rh = editor.RowHeightValue;
        Assert.InRange(editor.ScrollMaximum,
            initialMax - rh * 2, initialMax + rh * 2);
    }
}
