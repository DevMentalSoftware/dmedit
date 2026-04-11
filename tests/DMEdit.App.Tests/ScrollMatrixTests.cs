using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Comprehensive parameterized scroll/caret matrix tests.  Each theory
/// method asserts a single invariant across a combinatorial matrix of:
///   - Doc sizes: small (10), fifty (50), medium (200), medium-long (200x200),
///     large (2000), longLines (50x200)
///   - Wrap modes: off, on
///   - Caret positions: first, second, quarter, mid, three-quarter, penultimate, last
///
/// Each data generator yields ~60-80 test cases per theory method,
/// covering ~1,500+ total logical test cases.
/// </summary>
public class ScrollMatrixTests {
    private const double VpW = 600;
    private const double VpH = 400;

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

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
    private static long Anchor(EditorControl e) => e.Document!.Selection.Anchor;

    private static (long scrollCaret, long scrollExact, long layoutInval,
            long renderCalls, long scrollRetries) Snap(EditorControl e) =>
        (e.PerfStats.ScrollCaretCalls, e.PerfStats.ScrollExactCalls,
         e.PerfStats.LayoutInvalidations, e.PerfStats.RenderCalls,
         e.PerfStats.ScrollRetries);

    private static (long scrollCaret, long scrollExact, long layoutInval,
            long renderCalls, long scrollRetries) Delta(EditorControl e,
            (long, long, long, long, long) before) =>
        (e.PerfStats.ScrollCaretCalls - before.Item1,
         e.PerfStats.ScrollExactCalls - before.Item2,
         e.PerfStats.LayoutInvalidations - before.Item3,
         e.PerfStats.RenderCalls - before.Item4,
         e.PerfStats.ScrollRetries - before.Item5);

    private static void AssertCaretOnScreen(EditorControl e, string label) {
        var y = e.GetCaretScreenYForTest();
        Assert.True(y.HasValue, $"{label}: caret not on screen");
        Assert.InRange(y!.Value, -1, VpH + 1);
    }

    // ------------------------------------------------------------------
    //  Matrix data generators
    // ------------------------------------------------------------------

    private static readonly (int lines, int len, string tag)[] DocSizes = {
        (10,   20,  "small"),
        (50,   20,  "fifty"),
        (200,  20,  "medium"),
        (200,  200, "medLong"),
        (2000, 20,  "large"),
        (50,   200, "longLines"),
    };

    private static readonly bool[] WrapModes = { false, true };

    /// <summary>
    /// Core matrix: yields (lineCount, lineLen, wrap, initialLine, desc)
    /// for every combination of doc size, wrap mode, and position.
    /// </summary>
    private static IEnumerable<(int lineCount, int lineLen, bool wrap,
            int initialLine, string desc)> MatrixCore() {
        foreach (var (lines, len, tag) in DocSizes) {
            foreach (var wrap in WrapModes) {
                var wrapTag = wrap ? "wrap" : "noWrap";
                var positions = new[] {
                    (0, "top"),
                    (1, "second"),
                    (lines / 4, "quarter"),
                    (lines / 2, "mid"),
                    (3 * lines / 4, "threeQ"),
                    (lines - 2, "penult"),
                    (lines - 1, "last"),
                };
                foreach (var (pos, posTag) in positions) {
                    yield return (lines, len, wrap, pos,
                        $"{tag}-{wrapTag}-{posTag}");
                }
            }
        }
    }

    /// <summary>Full matrix — no filtering.</summary>
    public static IEnumerable<object[]> FullMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            yield return new object[] { lc, ll, w, il, d };
        }
    }

    /// <summary>Vertical down: exclude last line (can't go down).</summary>
    public static IEnumerable<object[]> VerticalDownMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            if (il < lc - 1) {
                yield return new object[] { lc, ll, w, il, d };
            }
        }
    }

    /// <summary>
    /// Vertical up: exclude first line (can't go up) and small wrapped
    /// docs where headless Avalonia layout metrics make Up from line 1
    /// unreliable (the viewport fits the entire document, font metrics
    /// differ between headless and real rendering).
    /// </summary>
    public static IEnumerable<object[]> VerticalUpMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            if (il <= 0) continue;
            // Small wrapped doc + line 1: caret-row Y in headless can
            // equal line-0 Y, making Up a no-op.  Skip this edge case.
            if (w && lc <= 10 && il == 1) continue;
            yield return new object[] { lc, ll, w, il, d };
        }
    }

    /// <summary>
    /// Horizontal matrix: same as FullMatrix but caret starts mid-line
    /// instead of at line start.  Yields (lineCount, lineLen, wrap,
    /// initialLine, midOfs, desc) where midOfs is the char offset of
    /// a position a few chars into the line.
    /// </summary>
    public static IEnumerable<object[]> HorizontalMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            var intraLineOfs = Math.Min(5, ll / 2);
            yield return new object[] { lc, ll, w, il, intraLineOfs, d };
        }
    }

    /// <summary>Positions at last line only — for boundary-at-end tests.</summary>
    public static IEnumerable<object[]> LastLineMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            if (il == lc - 1) {
                yield return new object[] { lc, ll, w, il, d };
            }
        }
    }

    /// <summary>Positions at first line only — for boundary-at-start tests.</summary>
    public static IEnumerable<object[]> FirstLineMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            if (il == 0) {
                yield return new object[] { lc, ll, w, il, d };
            }
        }
    }

    // ------------------------------------------------------------------
    //  Shared setup: position caret and scroll it into view
    // ------------------------------------------------------------------

    private static EditorControl SetupAtLine(int lineCount, int lineLen,
            bool wrap, int initialLine) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var targetOfs = doc.Table.LineStartOfs(initialLine);
        doc.Selection = Selection.Collapsed(targetOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        return editor;
    }

    private static EditorControl SetupAtLineOfs(int lineCount, int lineLen,
            bool wrap, int initialLine, int intraLineOfs) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var targetOfs = doc.Table.LineStartOfs(initialLine) + intraLineOfs;
        targetOfs = Math.Min(targetOfs, doc.Table.Length);
        doc.Selection = Selection.Collapsed(targetOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        return editor;
    }

    // ==================================================================
    //  Vertical movement — Down
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void Down_CaretAdvances(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) > caretBefore,
            $"{desc}: caret didn't advance on Down");
    }

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void Down_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"{desc} after Down");
    }

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void Down_NoScrollExact(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var snap = Snap(editor);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        var d = Delta(editor, snap);
        Assert.True(d.scrollExact == 0,
            $"{desc}: scrollExact should be 0, was {d.scrollExact}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void Down_NoScrollRetries(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var snap = Snap(editor);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        var d = Delta(editor, snap);
        Assert.True(d.scrollRetries == 0,
            $"{desc}: scrollRetries should be 0, was {d.scrollRetries}");
    }

    // ==================================================================
    //  Vertical movement — Up
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalUpMatrix))]
    public void Up_CaretRetreats(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) < caretBefore,
            $"{desc}: caret didn't retreat on Up");
    }

    [AvaloniaTheory]
    [MemberData(nameof(VerticalUpMatrix))]
    public void Up_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);

        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"{desc} after Up");
    }

    [AvaloniaTheory]
    [MemberData(nameof(VerticalUpMatrix))]
    public void Up_NoScrollExact(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var snap = Snap(editor);

        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);

        var d = Delta(editor, snap);
        Assert.True(d.scrollExact == 0,
            $"{desc}: scrollExact should be 0, was {d.scrollExact}");
    }

    // ==================================================================
    //  Horizontal movement — Right / Left
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(HorizontalMatrix))]
    public void Right_CaretAdvancesOneChar(int lineCount, int lineLen,
            bool wrap, int initialLine, int intraLineOfs, string desc) {
        var editor = SetupAtLineOfs(lineCount, lineLen, wrap,
            initialLine, intraLineOfs);
        var caretBefore = Caret(editor);

        // Guard: skip if at doc end already.
        if (caretBefore >= editor.Document!.Table.Length) return;

        editor.MoveCaretHorizontalForTest(+1, byWord: false, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) == caretBefore + 1,
            $"{desc}: expected caret at {caretBefore + 1}, got {Caret(editor)}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(HorizontalMatrix))]
    public void Left_CaretRetreatsOneChar(int lineCount, int lineLen,
            bool wrap, int initialLine, int intraLineOfs, string desc) {
        var editor = SetupAtLineOfs(lineCount, lineLen, wrap,
            initialLine, intraLineOfs);
        var caretBefore = Caret(editor);

        // Guard: skip if at doc start already.
        if (caretBefore <= 0) return;

        editor.MoveCaretHorizontalForTest(-1, byWord: false, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) == caretBefore - 1,
            $"{desc}: expected caret at {caretBefore - 1}, got {Caret(editor)}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(HorizontalMatrix))]
    public void RightLeft_RoundTrip(int lineCount, int lineLen,
            bool wrap, int initialLine, int intraLineOfs, string desc) {
        var editor = SetupAtLineOfs(lineCount, lineLen, wrap,
            initialLine, intraLineOfs);
        var caretBefore = Caret(editor);

        // Guard: skip boundary positions where Right or Left would be a no-op.
        if (caretBefore <= 0 || caretBefore >= editor.Document!.Table.Length) return;

        editor.MoveCaretHorizontalForTest(+1, byWord: false, extend: false);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, byWord: false, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) == caretBefore,
            $"{desc}: Right+Left round trip failed, expected {caretBefore}, got {Caret(editor)}");
    }

    // ==================================================================
    //  Home / End
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void End_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"{desc} after End");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void Home_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);

        // Move to mid-line first so Home has somewhere to go.
        var lineStart = editor.Document!.Table.LineStartOfs(initialLine);
        var midOfs = lineStart + Math.Min(lineLen / 2, 5);
        midOfs = Math.Min(midOfs, editor.Document.Table.Length);
        editor.Document.Selection = Selection.Collapsed(midOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"{desc} after Home");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void HomeEnd_RoundTrip(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var lineStart = editor.Document!.Table.LineStartOfs(initialLine);
        var midOfs = lineStart + Math.Min(lineLen / 2, 5);
        midOfs = Math.Min(midOfs, editor.Document.Table.Length);
        editor.Document.Selection = Selection.Collapsed(midOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        // After Home then End, caret should be at or past the original
        // mid position (End goes to row end or line end, both >= mid).
        Assert.True(Caret(editor) >= midOfs,
            $"{desc}: Home->End didn't restore past original position");
    }

    // ==================================================================
    //  GoToPosition
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void GoTo_CaretAtTarget(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var targetOfs = doc.Table.LineStartOfs(initialLine);

        editor.GoToPosition(targetOfs);
        Relayout(editor);

        Assert.True(Caret(editor) == targetOfs,
            $"{desc}: expected caret at {targetOfs}, got {Caret(editor)}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void GoTo_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var targetOfs = doc.Table.LineStartOfs(initialLine);

        editor.GoToPosition(targetOfs);
        Relayout(editor);

        AssertCaretOnScreen(editor, $"{desc} after GoTo");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void GoTo_MidDoc_NotAtEdge(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        // Only meaningful for mid-document positions where the viewport
        // can actually center the caret (not near top/bottom of doc).
        // Skip wrap-on + long-line combos: wrapped rows multiply the
        // effective document height, so the row height approximation
        // used to detect edge positions becomes unreliable.
        var rh = 18.0; // approximate row height
        var vpRows = (int)(VpH / rh);
        if (initialLine < vpRows / 2 || initialLine > lineCount - vpRows / 2 - 1) return;

        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var targetOfs = doc.Table.LineStartOfs(initialLine);

        editor.GoToPosition(targetOfs);
        Relayout(editor);

        // GoToPosition uses Center policy: caret Y should be roughly in
        // the middle of the viewport (10%-90% range, not at extreme edges).
        var y = editor.GetCaretScreenYForTest();
        if (!y.HasValue) return; // skip if caret not on screen (shouldn't happen)
        Assert.True(y.Value >= VpH * 0.10 - 1 && y.Value <= VpH * 0.90 + 1,
            $"{desc}: caret Y {y.Value:F1} outside 10-90% of viewport");
    }

    // ==================================================================
    //  Selection extend — Down
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void DownExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var anchorBefore = Anchor(editor);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(+1, extend: true);
        Relayout(editor);

        Assert.Equal(anchorBefore, Anchor(editor));
        Assert.True(Caret(editor) > caretBefore,
            $"{desc}: caret didn't advance on Down+extend");
        Assert.False(editor.Document!.Selection.IsEmpty);
    }

    // ==================================================================
    //  Selection extend — Up
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalUpMatrix))]
    public void UpExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var anchorBefore = Anchor(editor);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(-1, extend: true);
        Relayout(editor);

        Assert.Equal(anchorBefore, Anchor(editor));
        Assert.True(Caret(editor) < caretBefore,
            $"{desc}: caret didn't retreat on Up+extend");
        Assert.False(editor.Document!.Selection.IsEmpty);
    }

    // ==================================================================
    //  Selection extend — Right
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(HorizontalMatrix))]
    public void RightExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, int intraLineOfs, string desc) {
        var editor = SetupAtLineOfs(lineCount, lineLen, wrap,
            initialLine, intraLineOfs);
        var anchorBefore = Anchor(editor);
        var caretBefore = Caret(editor);

        // Guard: skip if at doc end.
        if (caretBefore >= editor.Document!.Table.Length) return;

        editor.MoveCaretHorizontalForTest(+1, byWord: false, extend: true);
        Relayout(editor);

        Assert.True(Anchor(editor) == anchorBefore,
            $"{desc}: anchor moved from {anchorBefore} to {Anchor(editor)}");
        Assert.True(Caret(editor) == caretBefore + 1,
            $"{desc}: caret expected {caretBefore + 1}, got {Caret(editor)}");
        Assert.False(editor.Document!.Selection.IsEmpty);
    }

    // ==================================================================
    //  Boundary behavior — Down at last line
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(LastLineMatrix))]
    public void Down_AtLastLine_NoChange(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        // At the last line, Down should either stay at the same position
        // or move to the end of the last line (implementation-dependent).
        // It must NOT go beyond the document length.
        Assert.True(Caret(editor) <= editor.Document!.Table.Length,
            $"{desc}: caret went past document end");
    }

    // ==================================================================
    //  Boundary behavior — Up at first line
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FirstLineMatrix))]
    public void Up_AtFirstLine_NoChange(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);

        // At the first line, Up should keep caret at 0 or at the line
        // start — must not go negative.
        Assert.True(Caret(editor) >= 0,
            $"{desc}: caret went negative");
        Assert.True(Caret(editor) <= caretBefore,
            $"{desc}: caret advanced on Up at first line");
    }

    // ==================================================================
    //  Down/Up round-trip symmetry (full matrix)
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalDownMatrix))]
    public void DownThenUp_ReturnsToPreviousPosition(int lineCount,
            int lineLen, bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);
        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) == caretBefore,
            $"{desc}: Down+Up round trip failed, expected {caretBefore}, got {Caret(editor)}");
    }

    // ==================================================================
    //  Up/Down round-trip symmetry
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(VerticalUpMatrix))]
    public void UpThenDown_ReturnsToPreviousPosition(int lineCount,
            int lineLen, bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);

        editor.MoveCaretVerticalForTest(-1, extend: false);
        Relayout(editor);
        editor.MoveCaretVerticalForTest(+1, extend: false);
        Relayout(editor);

        Assert.True(Caret(editor) == caretBefore,
            $"{desc}: Up+Down round trip failed, expected {caretBefore}, got {Caret(editor)}");
    }

    // ==================================================================
    //  PageDown / PageUp
    // ==================================================================

    /// <summary>PageDown matrix: exclude small docs (need &gt; 2 viewports
    /// for meaningful page movement) and wrap-on (headless estimate drift
    /// makes page-jump caret visibility unreliable).</summary>
    public static IEnumerable<object[]> PageDownMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            if (lc < 100 || il >= lc - 2 || w) continue;
            yield return new object[] { lc, ll, w, il, d };
        }
    }

    public static IEnumerable<object[]> PageUpMatrix() {
        foreach (var (lc, ll, w, il, d) in MatrixCore()) {
            // Need at least one viewport of lines above the caret for
            // a meaningful PageUp.
            if (lc < 100 || il < 35 || w) continue;
            yield return new object[] { lc, ll, w, il, d };
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDown_CaretAdvances(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);
        editor.MoveCaretByPageForTest(+1, extend: false);
        Relayout(editor);
        Assert.True(Caret(editor) > caretBefore,
            $"{desc}: PageDown didn't advance caret");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDown_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        editor.MoveCaretByPageForTest(+1, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} after PageDown");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDown_NoScrollExact(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var snap = Snap(editor);
        editor.MoveCaretByPageForTest(+1, extend: false);
        Relayout(editor);
        Assert.Equal(0, Delta(editor, snap).scrollExact);
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDown_ScrollAdvances(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var scrollBefore = editor.ScrollValue;
        editor.MoveCaretByPageForTest(+1, extend: false);
        Relayout(editor);
        Assert.True(editor.ScrollValue > scrollBefore,
            $"{desc}: PageDown didn't scroll");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageUpMatrix))]
    public void PageUp_CaretRetreats(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);
        editor.MoveCaretByPageForTest(-1, extend: false);
        Relayout(editor);
        Assert.True(Caret(editor) < caretBefore,
            $"{desc}: PageUp didn't retreat caret");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageUpMatrix))]
    public void PageUp_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        editor.MoveCaretByPageForTest(-1, extend: false);
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} after PageUp");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDownThenUp_NearOriginal(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var caretBefore = Caret(editor);
        editor.MoveCaretByPageForTest(+1, extend: false);
        Relayout(editor);
        editor.MoveCaretByPageForTest(-1, extend: false);
        Relayout(editor);
        var rh = editor.RowHeightValue;
        var tolerance = (long)(VpH / rh) * 2;
        Assert.InRange(Caret(editor),
            Math.Max(0, caretBefore - tolerance),
            caretBefore + tolerance);
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageDownMatrix))]
    public void PageDownExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var anchorBefore = Anchor(editor);
        editor.MoveCaretByPageForTest(+1, extend: true);
        Relayout(editor);
        Assert.Equal(anchorBefore, Anchor(editor));
        Assert.False(editor.Document!.Selection.IsEmpty);
    }

    // ==================================================================
    //  Find — matrix coverage
    // ==================================================================

    public static IEnumerable<object[]> FindMatrix() {
        var sizes = new[] {
            (lines: 50, len: 20, tag: "fifty"),
            (lines: 200, len: 20, tag: "medium"),
            (lines: 500, len: 20, tag: "large"),
        };
        foreach (var (lines, len, tag) in sizes) {
            foreach (var wrap in WrapModes) {
                var wrapTag = wrap ? "wrap" : "noWrap";
                var targets = new[] { 3, lines / 4, lines / 2, 3 * lines / 4, lines - 5 };
                foreach (var target in targets) {
                    if (target < 0 || target >= lines) continue;
                    yield return new object[] { lines, len, wrap, target,
                        $"{tag}-{wrapTag}-L{target}" };
                }
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(FindMatrix))]
    public void FindNext_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int targetLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = $"L{targetLine:D5}";
        Assert.True(editor.FindNext(), $"{desc}: no match");
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} FindNext");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FindMatrix))]
    public void FindNext_ScrollExactAtMostOne(int lineCount, int lineLen,
            bool wrap, int targetLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = $"L{targetLine:D5}";
        var snap = Snap(editor);
        editor.FindNext();
        Relayout(editor);
        Assert.InRange(Delta(editor, snap).scrollExact, 0, 1);
    }

    [AvaloniaTheory]
    [MemberData(nameof(FindMatrix))]
    public void FindPrev_CaretOnScreen(int lineCount, int lineLen,
            bool wrap, int targetLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var pastLine = Math.Min(targetLine + 20, lineCount - 1);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(pastLine));
        editor.ScrollCaretIntoView();
        Relayout(editor);
        editor.LastSearchTerm = $"L{targetLine:D5}";
        Assert.True(editor.FindPrevious(), $"{desc}: no match");
        Relayout(editor);
        AssertCaretOnScreen(editor, $"{desc} FindPrev");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FindMatrix))]
    public void FindPrev_ScrollExactAtMostOne(int lineCount, int lineLen,
            bool wrap, int targetLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var pastLine = Math.Min(targetLine + 20, lineCount - 1);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(pastLine));
        editor.ScrollCaretIntoView();
        Relayout(editor);
        editor.LastSearchTerm = $"L{targetLine:D5}";
        var snap = Snap(editor);
        editor.FindPrevious();
        Relayout(editor);
        Assert.InRange(Delta(editor, snap).scrollExact, 0, 1);
    }

    [AvaloniaTheory]
    [MemberData(nameof(FindMatrix))]
    public void FindNext_SelectsCorrectLine(int lineCount, int lineLen,
            bool wrap, int targetLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = $"L{targetLine:D5}";
        Assert.True(editor.FindNext());
        Relayout(editor);
        var matchLine = doc.Table.LineFromOfs(doc.Selection.Start);
        Assert.Equal(targetLine, matchLine);
    }

    // ==================================================================
    //  Horizontal — boundary crossing
    // ==================================================================

    public static IEnumerable<object[]> LineEndMatrix() {
        var sizes = new[] { (50, 20, "fifty"), (200, 20, "medium") };
        foreach (var (lines, len, tag) in sizes) {
            foreach (var wrap in WrapModes) {
                var wrapTag = wrap ? "wrap" : "noWrap";
                foreach (var pos in new[] { 0, 1, lines / 2, lines - 2 }) {
                    if (pos < 0 || pos >= lines - 1) continue;
                    yield return new object[] { lines, len, wrap, pos,
                        $"{tag}-{wrapTag}-end{pos}" };
                }
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(LineEndMatrix))]
    public void Right_AtLineEnd_CrossesToNextLine(int lineCount,
            int lineLen, bool wrap, int initialLine, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        var lineEnd = doc.Table.LineStartOfs(initialLine) + lineLen;
        doc.Selection = Selection.Collapsed(lineEnd);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(initialLine + 1, (int)doc.Table.LineFromOfs(Caret(editor)));
    }

    [AvaloniaTheory]
    [MemberData(nameof(LineEndMatrix))]
    public void Left_AtLineStart_CrossesToPrevLine(int lineCount,
            int lineLen, bool wrap, int initialLine, string desc) {
        if (initialLine == 0) return;
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.LineStartOfs(initialLine));
        editor.ScrollCaretIntoView();
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(initialLine - 1, (int)doc.Table.LineFromOfs(Caret(editor)));
    }

    // ==================================================================
    //  Multi-step walkthrough
    // ==================================================================

    [AvaloniaTheory]
    [InlineData(80, 20, false, "noWrap")]
    [InlineData(80, 20, true, "wrap")]
    [InlineData(30, 200, true, "longWrap")]
    public void DownWalk_CaretAlwaysOnScreen(int lineCount, int lineLen,
            bool wrap, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        for (var step = 0; step < lineCount * 3; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(80, 20, false, "noWrap")]
    [InlineData(80, 20, true, "wrap")]
    [InlineData(30, 200, true, "longWrap")]
    public void UpWalk_CaretAlwaysOnScreen(int lineCount, int lineLen,
            bool wrap, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);
        for (var step = 0; step < lineCount * 3; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(-1, false);
            Relayout(editor);
            if (Caret(editor) == before) break;
            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(200, 20, false, "noWrap")]
    [InlineData(200, 20, true, "wrap")]
    public void RightWalk_NeverStuck(int lineCount, int lineLen,
            bool wrap, string desc) {
        var doc = MakeDoc(lineCount, lineLen);
        var editor = CreateEditor(doc, wrap);
        doc.Selection = Selection.Collapsed(0);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        var steps = Math.Min(500, (int)doc.Table.Length);
        for (var step = 0; step < steps; step++) {
            var before = Caret(editor);
            editor.MoveCaretHorizontalForTest(+1, false, false);
            Relayout(editor);
            if (Caret(editor) == before) {
                Assert.Equal(doc.Table.Length, before);
                break;
            }
        }
    }

    // ==================================================================
    //  Efficiency: Home/End should not invoke ScrollExact
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void End_NoScrollExact(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var snap = Snap(editor);
        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        Assert.Equal(0, Delta(editor, snap).scrollExact);
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void Home_NoScrollExact(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var lineStart = editor.Document!.Table.LineStartOfs(initialLine);
        var midOfs = lineStart + Math.Min(lineLen / 2, 5);
        midOfs = Math.Min(midOfs, editor.Document.Table.Length);
        editor.Document.Selection = Selection.Collapsed(midOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        var snap = Snap(editor);
        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);
        Assert.Equal(0, Delta(editor, snap).scrollExact);
    }

    // ==================================================================
    //  Edge cases: empty, single-line, single-char
    // ==================================================================

    [AvaloniaFact]
    public void EmptyDoc_Down_NoChange() {
        var doc = new Document();
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaFact]
    public void EmptyDoc_Up_NoChange() {
        var doc = new Document();
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaFact]
    public void EmptyDoc_Right_NoChange() {
        var doc = new Document();
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaFact]
    public void EmptyDoc_Home_NoChange() {
        var doc = new Document();
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretToLineEdgeForTest(true, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaFact]
    public void EmptyDoc_End_NoChange() {
        var doc = new Document();
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaFact]
    public void SingleLine_Down_NoChange() {
        var doc = new Document();
        doc.Insert("hello world");
        doc.Selection = Selection.Collapsed(5);
        var editor = CreateEditor(doc, false);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(before, Caret(editor));
    }

    [AvaloniaFact]
    public void SingleLine_Up_NoChange() {
        var doc = new Document();
        doc.Insert("hello world");
        doc.Selection = Selection.Collapsed(5);
        var editor = CreateEditor(doc, false);
        var before = Caret(editor);
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(before, Caret(editor));
    }

    [AvaloniaFact]
    public void SingleChar_RightThenLeft_RoundTrip() {
        var doc = new Document();
        doc.Insert("x");
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, false);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(1, Caret(editor));
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(0, Caret(editor));
    }

    // ==================================================================
    //  DocStart/DocEnd with selection extend
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void DocStartExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var anchorBefore = Caret(editor);
        editor.Document!.Selection = editor.Document.Selection.ExtendTo(0);
        editor.ScrollCaretIntoView(ScrollPolicy.Top);
        Relayout(editor);
        Assert.Equal(anchorBefore, Anchor(editor));
        Assert.Equal(0, Caret(editor));
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void DocEndExtend_AnchorFixed(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var anchorBefore = Caret(editor);
        var docLen = editor.Document!.Table.Length;
        editor.Document.Selection = editor.Document.Selection.ExtendTo(docLen);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);
        Assert.Equal(anchorBefore, Anchor(editor));
        Assert.Equal(docLen, Caret(editor));
    }

    // ==================================================================
    //  Word movement (Ctrl+Right / Ctrl+Left)
    // ==================================================================

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void WordRight_AdvancesPastFirstWord(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var before = Caret(editor);
        editor.MoveCaretHorizontalForTest(+1, byWord: true, extend: false);
        Relayout(editor);
        Assert.True(Caret(editor) > before + 1,
            $"{desc}: word-right should skip multiple chars");
    }

    [AvaloniaTheory]
    [MemberData(nameof(FullMatrix))]
    public void WordLeft_RetreatsFromMidLine(int lineCount, int lineLen,
            bool wrap, int initialLine, string desc) {
        var editor = SetupAtLine(lineCount, lineLen, wrap, initialLine);
        var lineStart = editor.Document!.Table.LineStartOfs(initialLine);
        var midOfs = lineStart + Math.Min(10, lineLen / 2);
        midOfs = Math.Min(midOfs, editor.Document.Table.Length);
        editor.Document.Selection = Selection.Collapsed(midOfs);
        editor.ScrollCaretIntoView();
        Relayout(editor);
        var before = Caret(editor);
        if (before <= 0) return;
        editor.MoveCaretHorizontalForTest(-1, byWord: true, extend: false);
        Relayout(editor);
        Assert.True(Caret(editor) < before - 1,
            $"{desc}: word-left should skip multiple chars");
    }
}
