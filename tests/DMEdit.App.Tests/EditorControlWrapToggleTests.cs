using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression tests for the "caret jumps to a different viewport row when
/// toggling word wrap" bug.  Background: <c>_scrollOffset.Y</c> was kept
/// verbatim across the toggle, but the old pixel offset mapped to a
/// completely different visual row in the new wrap-on-vs-wrap-off layout.
/// The fix is <c>EditorControl.PreserveCaretScreenYAcross</c>, which
/// captures the caret's screen-Y before the toggle and adjusts
/// <c>ScrollValue</c> after the rebuild so the caret lands back at the
/// same screen-Y.
/// </summary>
public class EditorControlWrapToggleTests {
    private const double ViewportWidth = 400;
    private const double ViewportHeight = 400;
    private const double Tolerance = 2.0; // 2-pixel slack for rounding / snapping

    /// <summary>
    /// Builds a <see cref="Document"/> whose lines are long enough to wrap
    /// in the test viewport.  The line count is tuned so the document is
    /// several viewports tall — we need enough content to scroll into and
    /// put the caret somewhere in the middle of the viewport, not at the
    /// top or bottom edge.
    /// </summary>
    private static Document MakeWrappingDoc(int lineCount = 120, int charsPerLine = 150) {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            // Alternate two long lines so each has plenty of spaces for the
            // word-break wrap path and distinct content for debugging.
            sb.Append($"line{i:D4} ");
            sb.Append('x', charsPerLine - 10);
            sb.Append('\n');
        }
        // Document(string) is internal to Core; use the public ctor + Insert.
        var doc = new Document();
        doc.Insert(sb.ToString());
        // Insert moves the caret to the end; reset it to 0 so test setup can
        // position the caret explicitly.
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    /// <summary>
    /// Creates an <see cref="EditorControl"/>, assigns the given document,
    /// sizes it to the fixed test viewport, runs Measure/Arrange so the
    /// internal layout state (<c>_viewport</c>, <c>_extent</c>) is valid,
    /// and leaves wrap toggles at their defaults (<c>WrapLines = false</c>).
    /// </summary>
    private static EditorControl CreateEditor(Document doc) {
        // A concrete monospace font keeps row height stable across environments.
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = ViewportWidth,
            Height = ViewportHeight,
            WrapLines = false,
        };
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return editor;
    }

    /// <summary>
    /// Returns the caret's current screen-Y (pixels from the top of the
    /// control) by re-running Measure/Arrange so the internal layout is
    /// up-to-date, then calling the internal
    /// <see cref="EditorControl.GetCaretScreenYForTest"/> helper.
    /// </summary>
    private static double? GetCaretScreenY(EditorControl editor) {
        if (editor.Document == null) return null;
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return editor.GetCaretScreenYForTest();
    }

    // ------------------------------------------------------------------
    //  Toggling WrapLines — the original bug
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void WrapLines_ToggleOn_CaretStaysAtSameScreenY() {
        var doc = MakeWrappingDoc();
        var editor = CreateEditor(doc);

        // Put the caret partway down the document.  GoToPosition handles
        // making it visible; we don't care exactly where in the viewport
        // it lands — the test only asserts the position is preserved
        // across the wrap toggle.
        var caretLine = 40;
        editor.GoToPosition(doc.Table.LineStartOfs(caretLine));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var before = GetCaretScreenY(editor);
        Assert.NotNull(before);
        Assert.InRange(before!.Value, 0, ViewportHeight);

        // Toggle wrap ON.
        editor.WrapLines = true;

        var after = GetCaretScreenY(editor);
        Assert.NotNull(after);
        Assert.InRange(after!.Value, before.Value - Tolerance, before.Value + Tolerance);
    }

    [AvaloniaFact]
    public void WrapLines_ToggleOff_CaretStaysAtSameScreenY() {
        // This is the specific failure from the 2026-04-09 report: wrap on,
        // caret on displayed row 44, toggle wrap off, caret snaps to row 0.
        // Before the fix, the new wrap-off layout kept the pre-toggle
        // scrollY, which mapped to a wrap-off topLine BELOW the caret's
        // logical line, so the caret fell out of the layout window and
        // the fallback ScrollCaretIntoView placed it at the top of the viewport.
        var doc = MakeWrappingDoc();
        var editor = CreateEditor(doc);
        editor.WrapLines = true;
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var caretLine = 40;
        editor.GoToPosition(doc.Table.LineStartOfs(caretLine));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var before = GetCaretScreenY(editor);
        Assert.NotNull(before);
        Assert.InRange(before!.Value, 0, ViewportHeight);

        // Toggle wrap OFF — the failing direction in the user's reproduction.
        editor.WrapLines = false;

        var after = GetCaretScreenY(editor);
        Assert.NotNull(after);
        Assert.InRange(after!.Value, before.Value - Tolerance, before.Value + Tolerance);
    }

    // ------------------------------------------------------------------
    //  Toggling WrapLinesAt / UseWrapColumn / HangingIndent
    //
    //  These setters also rebuild the layout and should preserve caret
    //  screen-Y.  They're easier to get right than WrapLines because
    //  both before and after are in wrap-on mode.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void WrapLinesAt_Change_CaretStaysAtSameScreenY() {
        var doc = MakeWrappingDoc();
        var editor = CreateEditor(doc);
        editor.WrapLines = true;
        editor.UseWrapColumn = true;
        editor.WrapLinesAt = 80;
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var caretLine = 30;
        editor.GoToPosition(doc.Table.LineStartOfs(caretLine));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var before = GetCaretScreenY(editor);
        Assert.NotNull(before);

        // Shrink the wrap column — every wrapped line now takes more rows.
        editor.WrapLinesAt = 40;

        var after = GetCaretScreenY(editor);
        Assert.NotNull(after);
        Assert.InRange(after!.Value, before!.Value - Tolerance, before.Value + Tolerance);
    }

    [AvaloniaFact]
    public void UseWrapColumn_Toggle_CaretStaysAtSameScreenY() {
        var doc = MakeWrappingDoc();
        var editor = CreateEditor(doc);
        editor.WrapLines = true;
        editor.UseWrapColumn = true;
        editor.WrapLinesAt = 40;
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var caretLine = 25;
        editor.GoToPosition(doc.Table.LineStartOfs(caretLine));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var before = GetCaretScreenY(editor);
        Assert.NotNull(before);

        // Toggle off the column limit — wrap now respects only viewport width.
        editor.UseWrapColumn = false;

        var after = GetCaretScreenY(editor);
        Assert.NotNull(after);
        Assert.InRange(after!.Value, before!.Value - Tolerance, before.Value + Tolerance);
    }

    // ------------------------------------------------------------------
    //  ScrollCaretIntoView wrap-on row vs line (step 1 of the audit)
    // ------------------------------------------------------------------

    /// <summary>
    /// The step-1 bug: when wrap is on and the caret sits on a non-first
    /// row of a long wrapped line, <c>ScrollCaretIntoView</c> used to
    /// compute <c>caretY = EstimateWrappedLineY(caretLine, ...)</c> — the Y
    /// of the logical line's *start* (row 0 of that paragraph).  For a caret
    /// on row 5 or 10 of a multi-row paragraph, that's several rows above
    /// the caret's real position, so the code would detect
    /// <c>caretY &lt; scrollOffset.Y</c> and write <c>ScrollValue = caretY</c>,
    /// visibly jerking the viewport up to the line start.  This happened
    /// on every keystroke/click in a wrapped paragraph.
    /// The fix: measure the caret against the current layout first and
    /// leave the scroll alone when the caret is already visible.
    /// </summary>
    [AvaloniaFact]
    public void ScrollCaretIntoView_WrapOn_CaretAlreadyVisible_NoScrollChange() {
        // Build a document with one very long line that will wrap into
        // many rows at the test viewport width + wrap column.
        var doc = new Document();
        // Short preamble so line 0 is short and lines 1+ are long.
        doc.Insert("preamble\n");
        // ~20 rows' worth of content at 80 chars/row.  Uses space-separated
        // word-break-friendly content so the mono row breaker splits it at
        // spaces like the editor would.
        var words = new System.Text.StringBuilder();
        for (var i = 0; i < 400; i++) {
            words.Append($"word{i:D3} ");
        }
        doc.Insert(words.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = ViewportWidth,
            Height = ViewportHeight,
            WrapLines = true,
            UseWrapColumn = true,
            WrapLinesAt = 80,
        };
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        // Place the caret deep inside the wrapped paragraph — well past
        // row 0 of the long line.
        var caretOfs = 9 + 80 * 10; // line 1 start + 10 rows of content
        editor.GoToPosition(caretOfs);
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        // Sanity check: GoToPosition should have placed the caret somewhere
        // on screen.
        var screenYBefore = editor.GetCaretScreenYForTest();
        Assert.NotNull(screenYBefore);
        Assert.InRange(screenYBefore!.Value, 0, ViewportHeight);

        var scrollBefore = editor.ScrollValue;

        // Call ScrollCaretIntoView.  Since the caret is already visible in
        // the current layout, this must be a pure no-op.  Before the fix,
        // the wrap-on branch computed the line-start Y (which was above
        // the viewport top because the caret was several rows into the
        // long line) and wrote it to ScrollValue, visibly jumping the view.
        editor.ScrollCaretIntoView();
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var scrollAfter = editor.ScrollValue;
        Assert.InRange(scrollAfter, scrollBefore - Tolerance, scrollBefore + Tolerance);

        var screenYAfter = editor.GetCaretScreenYForTest();
        Assert.NotNull(screenYAfter);
        Assert.InRange(screenYAfter!.Value,
            screenYBefore.Value - Tolerance, screenYBefore.Value + Tolerance);
    }

    [AvaloniaFact]
    public void HangingIndent_Toggle_CaretStaysAtSameScreenY() {
        var doc = MakeWrappingDoc();
        var editor = CreateEditor(doc);
        editor.WrapLines = true;
        editor.HangingIndent = false;
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var caretLine = 35;
        editor.GoToPosition(doc.Table.LineStartOfs(caretLine));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var before = GetCaretScreenY(editor);
        Assert.NotNull(before);

        editor.HangingIndent = true;

        var after = GetCaretScreenY(editor);
        Assert.NotNull(after);
        Assert.InRange(after!.Value, before!.Value - Tolerance, before.Value + Tolerance);
    }
}
