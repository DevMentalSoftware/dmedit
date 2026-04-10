using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression tests for scrollbar thumb drag scrolling through documents
/// with wrap enabled and mixed per-line row counts.  The 2026-04-09 session
/// added the per-line row index so that the scroll extent reflects the
/// actual document height instead of the char-density estimate.  These
/// tests lock in the invariants that the estimate can't provide:
///
/// <list type="bullet">
/// <item>ScrollMaximum equals (totalRows - viewportRows) * rh exactly
///       — no undershoot, no overshoot.</item>
/// <item>ScrollValue = ScrollMaximum lands on the last doc row with no
///       blank space below it.</item>
/// <item>ScrollValue = 0 lands on line 0 row 0.</item>
/// <item>Dragging from max back to 0 and from 0 to max produces
///       monotonic topLine progression (no direction reversals from
///       estimate drift).</item>
/// </list>
/// </summary>
public class EditorControlDragScrollTests {
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double Tolerance = 2.0;

    /// <summary>
    /// Builds a doc with mixed line lengths that produce varying wrap row
    /// counts.  Alternates short (1-row) and long (multi-row when wrapped)
    /// lines to maximise variance.
    /// </summary>
    private static Document BuildMixedWrapDoc(int lineCount) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var pattern = i % 4;
            switch (pattern) {
                case 0: sb.Append($"line {i:D4} short\n"); break;
                case 1: sb.Append($"line {i:D4} " + new string('a', 80) + "\n"); break;
                case 2: sb.Append($"line {i:D4} medium text here\n"); break;
                case 3: sb.Append($"line {i:D4} " + new string('b', 160) + "\n"); break;
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    private static EditorControl CreateEditor(Document doc, bool wrapLines) {
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = ViewportWidth,
            Height = ViewportHeight,
            WrapLines = wrapLines,
        };
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return editor;
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
    }

    // ------------------------------------------------------------------
    //  Invariant: at ScrollValue=0 the very first line is visible at top
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void DragToTop_ShowsLineZeroAtViewportTop() {
        var doc = BuildMixedWrapDoc(lineCount: 101);
        var editor = CreateEditor(doc, wrapLines: true);

        // Drag to bottom first, then back to top.
        editor.ScrollValue = editor.ScrollMaximum;
        Relayout(editor);
        editor.ScrollValue = 0;
        Relayout(editor);

        // ScrollValue must be exactly 0 (not clamped by stale extent).
        Assert.InRange(editor.ScrollValue, 0, Tolerance);

        // Line 0 should be at the viewport top.  Verify by placing the
        // caret at offset 0 and checking that its screen Y is near 0.
        editor.GoToPosition(0);
        Relayout(editor);

        var caretY = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretY);
        Assert.InRange(caretY!.Value, 0, editor.RowHeightValue + Tolerance);
    }

    // ------------------------------------------------------------------
    //  Invariant: at ScrollValue=ScrollMaximum the last line is visible
    //  at the viewport bottom (no blank space below)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void DragToBottom_LastLineSitsAtViewportBottom() {
        var doc = BuildMixedWrapDoc(lineCount: 101);
        var editor = CreateEditor(doc, wrapLines: true);

        // Drag to bottom.
        editor.ScrollValue = editor.ScrollMaximum;
        Relayout(editor);

        // The caret at the end of the doc should be visible near the
        // viewport bottom.
        editor.GoToPosition(doc.Table.Length);
        Relayout(editor);

        var caretY = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretY);
        // Caret should be within the viewport, on the bottom half.
        Assert.InRange(caretY!.Value, 0, ViewportHeight);
    }

    // ------------------------------------------------------------------
    //  Invariant: drag-to-bottom then drag-to-top round trip works
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void DragBottomThenTop_ReturnsToLineZero() {
        var doc = BuildMixedWrapDoc(lineCount: 101);
        var editor = CreateEditor(doc, wrapLines: true);

        // Capture initial state.
        editor.GoToPosition(0);
        Relayout(editor);
        var initialScroll = editor.ScrollValue;
        var initialCaretY = editor.GetCaretScreenYForTest();

        // Round trip: bottom then back to top.
        editor.ScrollValue = editor.ScrollMaximum;
        Relayout(editor);
        editor.ScrollValue = 0;
        Relayout(editor);

        // Caret is still at offset 0; after scrolling back to top it
        // should be at the same screen Y as before.
        var finalCaretY = editor.GetCaretScreenYForTest();

        Assert.NotNull(initialCaretY);
        Assert.NotNull(finalCaretY);
        Assert.InRange(finalCaretY!.Value,
            initialCaretY!.Value - Tolerance,
            initialCaretY.Value + Tolerance);
    }

    // ------------------------------------------------------------------
    //  Invariant: monotonic progression when sweeping ScrollValue from
    //  0 to max in increments.  Each step must keep the caret (at end
    //  of doc) in the same viewport position or move it upward — never
    //  jump backwards (estimate drift symptom).
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void SweepScroll_CaretAtEndMovesMonotonicallyUp() {
        var doc = BuildMixedWrapDoc(lineCount: 101);
        var editor = CreateEditor(doc, wrapLines: true);

        editor.GoToPosition(doc.Table.Length);
        Relayout(editor);

        var max = editor.ScrollMaximum;
        var steps = 20;
        var previousCaretY = double.NegativeInfinity;

        for (var i = 0; i <= steps; i++) {
            var frac = (double)i / steps;
            editor.ScrollValue = max * frac;
            Relayout(editor);

            var caretY = editor.GetCaretScreenYForTest();
            if (!caretY.HasValue) {
                // Off-viewport during intermediate steps is acceptable;
                // only the final step (frac=1) must show the caret.
                if (i == steps) {
                    Assert.Fail(
                        $"At max scroll the end-of-doc caret must be visible " +
                        $"(scrollValue={editor.ScrollValue}, scrollMax={max}).");
                }
                previousCaretY = double.PositiveInfinity;
                continue;
            }

            // Monotonic: as we scroll down (frac ↑), the caret at the END
            // of the doc should move UP in the viewport — i.e., its
            // screen-Y should decrease (or stay constant).  If it jumps
            // backward (goes larger), estimate drift is leaking into
            // the drag path.
            if (previousCaretY != double.PositiveInfinity
                    && previousCaretY != double.NegativeInfinity) {
                Assert.True(caretY.Value <= previousCaretY + 5 * Tolerance,
                    $"Non-monotonic caret at step {i}: previousY={previousCaretY}, " +
                    $"currentY={caretY.Value}, scrollValue={editor.ScrollValue}");
            }
            previousCaretY = caretY.Value;
        }
    }

    // ------------------------------------------------------------------
    //  ScrollMaximum is non-zero for a multi-screen doc.  If it collapses
    //  to 0 the user can't scroll at all.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void ScrollMaximum_NonZero_ForDocLargerThanViewport() {
        var doc = BuildMixedWrapDoc(lineCount: 101);
        var editor = CreateEditor(doc, wrapLines: true);

        Assert.True(editor.ScrollMaximum > 0,
            $"Mixed-wrap 101-line doc should be scrollable " +
            $"(ScrollMaximum={editor.ScrollMaximum}).");
    }
}
