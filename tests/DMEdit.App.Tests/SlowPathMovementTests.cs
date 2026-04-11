using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for caret movement on lines that contain tabs.  Tabs force the
/// TextLayout slow path (MonoLineLayout rejects tab-containing lines),
/// which has different hit-test and row-counting behavior.  These tests
/// verify that vertical movement, Home/End, Find, and scrolling all work
/// correctly when some lines use the slow path and others use the mono
/// fast path — the mixed-path case is the most fragile because row-count
/// divergence between the two paths causes scroll targeting to miss.
/// </summary>
public class SlowPathMovementTests {
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

    /// <summary>
    /// Creates a mixed-path document: most lines are plain text (mono fast
    /// path), but every <paramref name="tabEvery"/>-th line contains a tab
    /// (TextLayout slow path).
    /// </summary>
    private static Document MakeMixedDoc(int lineCount, int lineLen,
            int tabEvery) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4} ";
            if (i % tabEvery == 0) {
                // Tab line: prefix + tab + padding.
                var afterTab = Math.Max(0, lineLen - prefix.Length - 1);
                sb.Append(prefix);
                sb.Append('\t');
                sb.Append('a', afterTab);
            } else {
                var pad = Math.Max(0, lineLen - prefix.Length);
                sb.Append(prefix);
                sb.Append('a', pad);
            }
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    /// <summary>Creates a document where ALL lines contain tabs.</summary>
    private static Document MakeAllTabDoc(int lineCount, int lineLen) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4}\t";
            var pad = Math.Max(0, lineLen - prefix.Length);
            sb.Append(prefix);
            sb.Append('a', pad);
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

    // ------------------------------------------------------------------
    //  Vertical movement through mixed tab/no-tab lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false, "noWrap")]
    [InlineData(true, "wrap")]
    public void Down_ThroughMixedLines_CaretAlwaysOnScreen(bool wrap,
            string desc) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 5);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        for (var step = 0; step < 100; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} Down step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "noWrap")]
    [InlineData(true, "wrap")]
    public void Up_ThroughMixedLines_CaretAlwaysOnScreen(bool wrap,
            string desc) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 5);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);

        for (var step = 0; step < 100; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(-1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} Up step {step}");
        }
    }

    // ------------------------------------------------------------------
    //  Down/Up round-trip across tab/no-tab boundary
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownUp_AcrossTabBoundary_RoundTrip(bool wrap) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 5);
        var editor = CreateEditor(doc, wrap);
        // Line 4 is mono, line 5 is tab.  Position on line 4.
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(4));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, false); // → line 5 (tab)
        Relayout(editor);
        editor.MoveCaretVerticalForTest(-1, false); // → line 4 (mono)
        Relayout(editor);

        Assert.Equal(before, Caret(editor));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpDown_AcrossTabBoundary_RoundTrip(bool wrap) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 5);
        var editor = CreateEditor(doc, wrap);
        // Line 5 is tab, line 4 is mono.  Position on line 5.
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(5));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(-1, false); // → line 4 (mono)
        Relayout(editor);
        editor.MoveCaretVerticalForTest(+1, false); // → line 5 (tab)
        Relayout(editor);

        Assert.Equal(before, Caret(editor));
    }

    // ------------------------------------------------------------------
    //  Home/End on tab lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void HomeEnd_OnTabLine_CaretOnScreen(bool wrap) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 5);
        var editor = CreateEditor(doc, wrap);
        // Line 10 is a tab line (10 % 5 == 0).
        var lineStart = doc.Table.LineStartOfs(10);
        doc.Selection = Selection.Collapsed(lineStart + 10);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, "End on tab line");

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, "Home on tab line");
    }

    // ------------------------------------------------------------------
    //  Find on tab lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindNext_ThroughTabLines_CaretAlwaysOnScreen(bool wrap) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 3);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "L00";

        // Walk 50 FindNext calls.
        for (var step = 0; step < 50; step++) {
            if (!editor.FindNext()) break;
            Relayout(editor);
            AssertCaretOnScreen(editor, $"FindNext tab step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindPrev_ThroughTabLines_CaretAlwaysOnScreen(bool wrap) {
        var doc = MakeMixedDoc(100, 80, tabEvery: 3);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);
        editor.LastSearchTerm = "L00";

        for (var step = 0; step < 50; step++) {
            if (!editor.FindPrevious()) break;
            Relayout(editor);
            AssertCaretOnScreen(editor, $"FindPrev tab step {step}");
        }
    }

    // ------------------------------------------------------------------
    //  All-tab document
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllTab_Down_CaretAlwaysOnScreen(bool wrap) {
        var doc = MakeAllTabDoc(80, 60);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        for (var step = 0; step < 80; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"AllTab Down step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllTab_DownUp_RoundTrip(bool wrap) {
        var doc = MakeAllTabDoc(80, 60);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(40));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);

        Assert.Equal(before, Caret(editor));
    }

    // ------------------------------------------------------------------
    //  Right/Left walk through tab character
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void Right_ThroughTab_AdvancesOneChar() {
        var doc = MakeAllTabDoc(10, 20);
        var editor = CreateEditor(doc, false);
        // Position just before the tab (at index 5 = after "L0000").
        doc.Selection = Selection.Collapsed(5);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        var before = Caret(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);

        // Tab is one character, caret should advance by 1.
        Assert.Equal(before + 1, Caret(editor));
    }

    [AvaloniaFact]
    public void Left_ThroughTab_RetreatsOneChar() {
        var doc = MakeAllTabDoc(10, 20);
        var editor = CreateEditor(doc, false);
        // Position just after the tab (at index 6 = "L0000\t|").
        doc.Selection = Selection.Collapsed(6);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        var before = Caret(editor);
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);

        Assert.Equal(before - 1, Caret(editor));
    }

    // ------------------------------------------------------------------
    //  PageDown through mixed tab/mono lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageDown_MixedTabDoc_CaretOnScreen(bool wrap) {
        if (wrap) return; // wrap-on page tests excluded (headless limitation)
        var doc = MakeMixedDoc(200, 80, tabEvery: 3);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(50));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        editor.MoveCaretByPageForTest(+1, false);
        Relayout(editor);

        Assert.True(Caret(editor) > doc.Table.LineStartOfs(50));
        AssertCaretOnScreen(editor, "PageDown mixed tab");
    }

    // ------------------------------------------------------------------
    //  GoTo on a tab line
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoTo_TabLine_CaretOnScreen(bool wrap) {
        var doc = MakeMixedDoc(200, 80, tabEvery: 3);
        var editor = CreateEditor(doc, wrap);

        // GoTo a tab line (line 60 = 60 % 3 == 0).
        editor.GoToPosition(doc.Table.LineStartOfs(60));
        Relayout(editor);

        Assert.Equal(doc.Table.LineStartOfs(60), Caret(editor));
        AssertCaretOnScreen(editor, "GoTo tab line");
    }
}
