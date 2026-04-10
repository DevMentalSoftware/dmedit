using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression tests for arrow-key navigation scroll behaviour.  The audit of
/// <see cref="EditorControl.ScrollCaretIntoView"/> (2026-04-09 session)
/// established the invariant that vertical caret movement must only scroll
/// when the destination row would otherwise be off-screen, and when it does
/// scroll, it must move by exactly one row — not snap the caret's logical
/// line to the top.
/// </summary>
public class EditorControlNavigationTests {
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double Tolerance = 2.0;

    private static Document MakeLongDoc(int lineCount = 80) {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            sb.Append($"line {i:D3}\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    private static EditorControl CreateEditor(Document doc) {
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

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
    }

    // ------------------------------------------------------------------
    //  Step 2: arrow-down from top-visible row must NOT scroll
    //
    //  This is the bug the user reported: click the topmost visible row,
    //  press Down, and the viewport jerks because the redundant
    //  ScrollCaretIntoView at the end of MoveCaretVertical was writing a
    //  fresh scroll value based on line-start Y.  After the fix, arrow-
    //  down inside the viewport is pure caret motion with no scroll side-
    //  effect.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void ArrowDown_FromTopVisibleRow_DoesNotScroll() {
        var doc = MakeLongDoc();
        var editor = CreateEditor(doc);

        // Scroll to the middle of the document so "top of viewport" is a
        // meaningful position (not line 0, which is already clamped at the top).
        var rh = editor.RowHeightValue;
        editor.ScrollValue = 20 * rh;
        Relayout(editor);

        // Place the caret on the topmost visible row — the line whose
        // top edge coincides with ScrollValue.
        var topRowLine = 20;
        editor.GoToPosition(doc.Table.LineStartOfs(topRowLine));
        Relayout(editor);

        // Note: GoToPosition may itself adjust the scroll to bring the
        // caret into view if it wasn't already. Capture the ACTUAL post-
        // navigation scroll and caret position as the baseline for the
        // arrow-down check — the test is "did arrow-down cause scroll
        // movement from this stable baseline", not "was the baseline
        // exactly where we expected".
        var scrollBefore = editor.ScrollValue;
        var caretYBefore = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretYBefore);
        Assert.InRange(caretYBefore!.Value, 0, ViewportHeight);

        // Press Down.  The caret should advance one row.  Since the row
        // below was already visible, ScrollValue must be unchanged.
        editor.MoveCaretVerticalForTest(lineDelta: +1, extend: false);
        Relayout(editor);

        Assert.InRange(editor.ScrollValue,
            scrollBefore - Tolerance, scrollBefore + Tolerance);

        var caretYAfter = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretYAfter);
        // Caret should have moved down by exactly one row height.
        Assert.InRange(caretYAfter!.Value,
            caretYBefore.Value + rh - Tolerance,
            caretYBefore.Value + rh + Tolerance);
    }

    // ------------------------------------------------------------------
    //  Step 3: GoToPosition centers the target
    //
    //  Previously GoToPosition used default ScrollCaretIntoView (Minimal),
    //  which placed a far-away target flush to the nearest viewport edge.
    //  After the audit, GoToPosition passes ScrollPolicy.Center so the
    //  target lands with surrounding context visible above and below.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void GoToPosition_FarTarget_CentersTargetRow() {
        var doc = MakeLongDoc(lineCount: 200);
        var editor = CreateEditor(doc);

        // Start at the top of the document.  Target line 100 — well past
        // the initial viewport and not close to either boundary, so the
        // Center policy's natural target (halfway down the viewport) is
        // unclipped by ScrollValue's [0, ScrollMaximum] clamp.
        var targetLine = 100;
        editor.GoToPosition(doc.Table.LineStartOfs(targetLine));
        Relayout(editor);

        var caretY = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretY);

        // The target row should land near the vertical center of the
        // viewport.  "Near" accounts for row-height quantization and
        // the fact that the caret is a single-row height inside a
        // single-row viewport slot.  We allow two rows of slack.
        var rh = editor.RowHeightValue;
        var expectedCenter = ViewportHeight / 2 - rh / 2;
        Assert.InRange(caretY!.Value,
            expectedCenter - 2 * rh,
            expectedCenter + 2 * rh);

        // And crucially, NOT flush to the top — the Minimal policy would
        // have put the caret at row 0, which is what we're fixing.
        Assert.True(caretY.Value > 2 * rh,
            $"GoToPosition should center, not place target at viewport top " +
            $"(caret Y = {caretY.Value}, row height = {rh}).");
    }

    // ------------------------------------------------------------------
    //  Step 3 (revised): Find scrolls match into view by direction
    //
    //  The user-reported scenario: "search for 'banana' while near the
    //  top, first match is 10 pages later".  FindNext is a "scroll down"
    //  operation, so the match should land at the viewport BOTTOM (the
    //  last row the user encounters while reading forward).  The old
    //  default policy would have done this by coincidence (the match is
    //  below, Minimal pulls it up to the bottom edge) — but for the
    //  reverse case, FindPrevious, Minimal would land the match at
    //  whatever edge it happened to be closest to, not at the top where
    //  a scroll-up operation should put it.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindNext_MatchFarBelowCaret_LandsAtViewportBottom() {
        // Build a document with a clearly-identifiable match far below the
        // initial viewport.  Use a 200-line doc with a unique word on line 150.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) {
            sb.Append(i == 150 ? $"line {i:D3} banana filler\n" : $"line {i:D3} filler\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

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

        editor.LastSearchTerm = "banana";
        var found = editor.FindNext();
        Assert.True(found);

        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        // FindNext is a "scroll down" operation — the match on line 150
        // should land at the viewport BOTTOM.  Target scroll is therefore
        // (line150Y + rh) - viewportH — row 150's bottom edge aligned
        // with the viewport's bottom edge.
        var rh = editor.RowHeightValue;
        var expectedScrollY = (150 + 1) * rh - ViewportHeight;
        Assert.InRange(editor.ScrollValue,
            expectedScrollY - 2 * rh, expectedScrollY + 2 * rh);

        // The caret (at the end of the match) should be near the BOTTOM
        // of the viewport — confirming the direction rule applied.
        var caretY = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretY);
        Assert.True(caretY!.Value > ViewportHeight / 2,
            $"FindNext should land the match near the bottom of the viewport, " +
            $"not the top half (caret Y = {caretY.Value}, viewport = {ViewportHeight}).");
    }

    [AvaloniaFact]
    public void FindNext_MatchAlreadyVisible_DoesNotScroll() {
        // Regression for "Find next was scrolling rows into view when the
        // next occurrence was already visible" (2026-04-09 manual test).
        // Build a document with two "banana" matches a few lines apart —
        // both inside the same viewport.  Search forward.  First call
        // lands on the first match (may or may not move the viewport
        // depending on where it starts).  Second call should find the
        // second match; since it's already visible, ScrollValue must not
        // change.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) {
            if (i == 5 || i == 10) {
                sb.Append($"line {i:D3} banana filler\n");
            } else {
                sb.Append($"line {i:D3} filler\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

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

        editor.LastSearchTerm = "banana";

        // First FindNext: lands on line 5 (the first match).  Lines 5
        // and 10 are both well inside the initial viewport (33 rows at
        // 12px = 400px viewport), so this shouldn't need to scroll but
        // we don't care about that here — we care about the SECOND call.
        editor.FindNext();
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var scrollBeforeSecond = editor.ScrollValue;

        // Second FindNext: lands on line 10.  Line 10 is on screen (we're
        // scrolled to 0 and line 10 is well within the first viewport),
        // so ScrollValue must not change.
        editor.FindNext();
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        Assert.InRange(editor.ScrollValue,
            scrollBeforeSecond - Tolerance,
            scrollBeforeSecond + Tolerance);
    }

    [Fact]
    public void SearchChunkedBackward_NoMatchInSmallRange_TerminatesQuickly() {
        // Regression for the 2026-04-09 hang: FindPrevious on a doc with a
        // single match (after F3 selected it) would call
        // SearchChunkedBackward(table, opts, 0, matchOffset, maxOverlap).
        // The first iteration covered the entire [0, matchOffset) range in
        // one chunk; finding nothing, it then reset chunkEnd to
        // chunkStart + overlap = 0 + needleLen > 0, which left chunkEnd
        // above start forever.  The exit check was
        //   if (chunkEnd <= start) break;
        // which failed because overlap > 0.  The fix: break when
        // chunkStart == start (we've already covered everything down to
        // the range bottom, so retreating further is pointless).
        //
        // We drive the test via a background thread with a 5-second
        // deadline: if the fix regresses the worker hangs, the event
        // never signals, and the test fails cleanly instead of hanging
        // the test process.  PieceTable has no thread affinity so it's
        // safe to touch off the UI thread — unlike EditorControl, which
        // forced the earlier attempt to drive this via the public
        // FindPrevious entry point and hit Avalonia's thread affinity.
        var table = new PieceTable();
        table.Insert(0L, "one two three apple four five six seven eight");
        // "apple" is at offset 14.  SearchChunkedBackward(table, _, 0, 14, _)
        // should search [0, 14) and return -1 (no match in the prefix).
        // Before the fix this call looped forever.

        var done = new System.Threading.ManualResetEventSlim(false);
        long result = -99;
        System.Exception? bgError = null;
        var worker = new System.Threading.Thread(() => {
            try {
                result = EditorControl.SearchChunkedBackwardForTest(
                    table, "apple", start: 0, end: 14);
            } catch (System.Exception ex) {
                bgError = ex;
            } finally {
                done.Set();
            }
        }) { IsBackground = true };
        worker.Start();

        var signalled = done.Wait(TimeSpan.FromSeconds(5));
        Assert.True(signalled,
            "SearchChunkedBackward did not return within 5s — the " +
            "infinite-loop regression is back.");
        Assert.Null(bgError);
        Assert.Equal(-1, result);
    }

    [AvaloniaFact]
    public void FindPrevious_MatchFarAboveCaret_LandsAtViewportTop() {
        // Symmetric case: start near the end of the document, search
        // backward for a match near the beginning.  FindPrevious is a
        // "scroll up" operation, so the match should land at the
        // viewport TOP.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) {
            sb.Append(i == 20 ? $"line {i:D3} apricot filler\n" : $"line {i:D3} filler\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());

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

        // Place caret near the end of the document, then search backward.
        editor.GoToPosition(doc.Table.LineStartOfs(180));
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        editor.LastSearchTerm = "apricot";
        var found = editor.FindPrevious();
        Assert.True(found);

        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        // FindPrevious is a "scroll up" operation — the match on line 20
        // should land at the viewport TOP.  Target scroll is line20 * rh.
        var rh = editor.RowHeightValue;
        var expectedScrollY = 20 * rh;
        Assert.InRange(editor.ScrollValue,
            expectedScrollY - 2 * rh, expectedScrollY + 2 * rh);

        // Caret should be near the TOP of the viewport.
        var caretY = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretY);
        Assert.True(caretY!.Value < ViewportHeight / 2,
            $"FindPrevious should land the match near the top of the viewport, " +
            $"not the bottom half (caret Y = {caretY.Value}, viewport = {ViewportHeight}).");
    }

    [AvaloniaFact]
    public void ArrowUp_FromBottomVisibleRow_DoesNotScroll() {
        // Symmetric: caret on the bottom-visible row, arrow up should
        // move the caret up one row without shifting the viewport.
        var doc = MakeLongDoc();
        var editor = CreateEditor(doc);

        var rh = editor.RowHeightValue;
        editor.ScrollValue = 20 * rh;
        Relayout(editor);

        // Figure out how many rows fit in the viewport so we can park the
        // caret on the bottom one.  Viewport ÷ row height, minus one for
        // the row that would partially overflow.
        var visibleRows = (int)(ViewportHeight / rh);
        var bottomLine = 20 + visibleRows - 2; // last fully-visible row
        editor.GoToPosition(doc.Table.LineStartOfs(bottomLine));
        Relayout(editor);

        var scrollBefore = editor.ScrollValue;
        var caretYBefore = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretYBefore);
        Assert.InRange(caretYBefore!.Value, 0, ViewportHeight);

        editor.MoveCaretVerticalForTest(lineDelta: -1, extend: false);
        Relayout(editor);

        Assert.InRange(editor.ScrollValue,
            scrollBefore - Tolerance, scrollBefore + Tolerance);

        var caretYAfter = editor.GetCaretScreenYForTest();
        Assert.NotNull(caretYAfter);
        Assert.InRange(caretYAfter!.Value,
            caretYBefore.Value - rh - Tolerance,
            caretYBefore.Value - rh + Tolerance);
    }
}
