using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Matrix tests for tab-containing documents through the mono fast path.
/// Same invariants as <see cref="ScrollMatrixTests"/> (caret advances,
/// caret on screen, no ScrollExact, round-trip symmetry) but with lines
/// that contain tabs — exercising the new tab-aware row breaker, column-
/// aware positioning, and GlyphRun segment splitting.
/// </summary>
public class TabMatrixTests {
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
    /// Creates a document where every line has a tab at a varying column.
    /// Line format: "L0000\t" + padding (total ~lineLen visible cols).
    /// </summary>
    private static Document MakeTabDoc(int lineCount, int lineLen = 80) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4}\t";
            var padLen = Math.Max(0, lineLen - prefix.Length - 4); // rough
            sb.Append(prefix);
            sb.Append('a', padLen);
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    /// <summary>Mixed: every 3rd line has a tab, others don't.</summary>
    private static Document MakeMixedTabDoc(int lineCount, int lineLen = 80) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4} ";
            if (i % 3 == 0) {
                sb.Append(prefix + '\t' + new string('b', Math.Max(0, lineLen - prefix.Length - 5)));
            } else {
                sb.Append(prefix + new string('a', Math.Max(0, lineLen - prefix.Length)));
            }
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    private static long Caret(EditorControl e) => e.Document!.Selection.Caret;
    private static long Anchor(EditorControl e) => e.Document!.Selection.Anchor;

    private static void AssertCaretOnScreen(EditorControl e, string label) {
        var y = e.GetCaretScreenYForTest();
        Assert.True(y.HasValue, $"{label}: caret not on screen");
        Assert.InRange(y!.Value, -1, VpH + 1);
    }

    private static EditorControl SetupAtLine(Document doc, bool wrap, int line) {
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(line));
        editor.ScrollCaretIntoView();
        Relayout(editor);
        return editor;
    }

    // ------------------------------------------------------------------
    //  Matrix generator
    // ------------------------------------------------------------------

    private static IEnumerable<(int lines, int len, bool wrap, int pos, string desc)> TabMatrix() {
        var sizes = new[] {
            (50, 80, "tab50"),
            (100, 80, "tab100"),
            (200, 80, "tab200"),
            (100, 150, "tabLong"),
        };
        foreach (var (lines, len, tag) in sizes) {
            foreach (var wrap in new[] { false, true }) {
                var w = wrap ? "W" : "N";
                var positions = new[] { 0, 1, lines/4, lines/2, 3*lines/4, lines-2, lines-1 };
                foreach (var pos in positions) {
                    if (pos < 0 || pos >= lines) continue;
                    yield return (lines, len, wrap, pos, $"{tag}-{w}-{pos}");
                }
            }
        }
    }

    public static IEnumerable<object[]> DownData() =>
        TabMatrix().Where(t => t.pos < t.lines - 1)
            .Select(t => new object[] { t.lines, t.len, t.wrap, t.pos, t.desc });

    public static IEnumerable<object[]> UpData() =>
        TabMatrix().Where(t => t.pos > 0)
            .Select(t => new object[] { t.lines, t.len, t.wrap, t.pos, t.desc });

    public static IEnumerable<object[]> FullData() =>
        TabMatrix().Select(t => new object[] { t.lines, t.len, t.wrap, t.pos, t.desc });

    // ------------------------------------------------------------------
    //  Down
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(DownData))]
    public void Down_CaretAdvances(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.True(Caret(editor) > before, $"{desc}: no advance");
    }

    [AvaloniaTheory]
    [MemberData(nameof(DownData))]
    public void Down_CaretOnScreen(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        AssertCaretOnScreen(editor, desc);
    }

    // ------------------------------------------------------------------
    //  Up
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(UpData))]
    public void Up_CaretRetreats(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.True(Caret(editor) < before, $"{desc}: no retreat");
    }

    [AvaloniaTheory]
    [MemberData(nameof(UpData))]
    public void Up_CaretOnScreen(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        AssertCaretOnScreen(editor, desc);
    }

    // ------------------------------------------------------------------
    //  Down/Up round-trip
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(DownData))]
    public void DownThenUp_RoundTrip(int lines, int len, bool wrap, int pos, string desc) {
        _ = desc;
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(before, Caret(editor));
    }

    // ------------------------------------------------------------------
    //  Home/End
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(FullData))]
    public void End_CaretOnScreen(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} End");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullData))]
    public void Home_CaretOnScreen(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        // Move to mid-line first.
        var lineStart = doc.Table.LineStartOfs(pos);
        doc.Selection = Selection.Collapsed(lineStart + 5);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(true, false);
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} Home");
    }

    // ------------------------------------------------------------------
    //  GoTo
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(FullData))]
    public void GoTo_CaretOnScreen(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = CreateEditor(doc, wrap);
        editor.GoToPosition(doc.Table.LineStartOfs(pos));
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} GoTo");
    }

    // ------------------------------------------------------------------
    //  Find through tab doc
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 80, false, "tab100-noWrap")]
    [InlineData(100, 80, true, "tab100-wrap")]
    [InlineData(200, 80, false, "tab200-noWrap")]
    public void FindNext_TabDoc_CaretOnScreen(int lines, int len, bool wrap, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "L0050";
        Assert.True(editor.FindNext());
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} FindNext");
    }

    // ------------------------------------------------------------------
    //  Mixed tab/mono: Down walk
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(80, 80, false, "mixed-noWrap")]
    [InlineData(80, 80, true, "mixed-wrap")]
    [InlineData(80, 150, true, "mixedLong-wrap")]
    public void MixedTab_DownWalk_CaretOnScreen(int lines, int len, bool wrap, string desc) {
        var doc = MakeMixedTabDoc(lines, len);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        for (var step = 0; step < lines; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }
    }

    // ------------------------------------------------------------------
    //  Extend selection across tab lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(DownData))]
    public void DownExtend_AnchorFixed(int lines, int len, bool wrap, int pos, string desc) {
        _ = desc;
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, true);
        Relayout(editor);
        Assert.Equal(before, Anchor(editor));
        Assert.True(Caret(editor) > before);
    }

    // ------------------------------------------------------------------
    //  Right/Left through tab character
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(FullData))]
    public void RightThroughTab_Advances(int lines, int len, bool wrap, int pos, string desc) {
        _ = desc;
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        for (var i = 0; i < 8; i++) {
            editor.MoveCaretHorizontalForTest(+1, false, false);
        }
        Relayout(editor);
        Assert.Equal(before + 8, Caret(editor));
    }

    // ------------------------------------------------------------------
    //  Word movement through tab lines
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [MemberData(nameof(FullData))]
    public void WordRight_TabDoc_Advances(int lines, int len, bool wrap, int pos, string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = SetupAtLine(doc, wrap, pos);
        var before = Caret(editor);
        editor.MoveCaretHorizontalForTest(+1, true, false);
        Relayout(editor);
        Assert.True(Caret(editor) > before + 1,
            $"{desc}: word-right should skip past tab");
    }

    // ------------------------------------------------------------------
    //  Column-wrap (WrapLinesAt) — different code path than window-wrap
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 80, 60, "tab100-col60")]
    [InlineData(100, 80, 40, "tab100-col40")]
    [InlineData(200, 80, 80, "tab200-col80")]
    public void ColumnWrap_Down_CaretOnScreen(int lines, int len, int wrapCol,
            string desc) {
        var doc = MakeTabDoc(lines, len);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = true,
            WrapLinesAt = wrapCol,
            UseWrapColumn = true,
        };
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));

        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(lines / 2));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        for (var step = 0; step < 20; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(100, 80, 60, "noTab100-col60")]
    [InlineData(100, 80, 40, "noTab100-col40")]
    public void ColumnWrap_NoTab_Down_CaretOnScreen(int lines, int len, int wrapCol,
            string desc) {
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++) {
            sb.Append($"L{i:D4} " + new string('a', Math.Max(0, len - 6)) + '\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = true,
            WrapLinesAt = wrapCol,
            UseWrapColumn = true,
        };
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));

        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(lines / 2));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        for (var step = 0; step < 20; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }
    }
}
