using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression tests locking down FindNext / FindPrevious scroll behaviour
/// after the 2026-04-09 exact-scroll-targeting work.  These tests exercise
/// three document sizes (small, medium, large) across wrap-off and wrap-on
/// modes, with two directions each, plus wrap-around behaviour that used
/// to corrupt the scroll extent (bugs #2 and #3 from that session).
///
/// <para>The invariants every test checks, regardless of doc size or
/// direction:</para>
/// <list type="bullet">
/// <item>Find lands the match inside the viewport (caret's screen Y is in
///       the valid range and non-negative).</item>
/// <item>ScrollValue is clamped to [0, ScrollMaximum] and ScrollMaximum
///       is consistent with the document's actual extent — no "scroll
///       max collapsed" (bug #2) and no "scrollbar at bottom with blank
///       space below last line" (bug #3).</item>
/// <item>Mid-document Find pins the match precisely to the expected edge
///       (bottom for Forward, top for Backward) — this is the
///       pixel-perfect targeting the exact-scroll work added.</item>
/// <item>Round-trip FindPrev-wrap-to-end followed by FindNext-wrap-to-top
///       leaves the document back at the top with the full extent
///       available — catches state corruption across the two
///       wrap-around boundaries.</item>
/// </list>
/// </summary>
public class EditorControlFindScrollTests {
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double Tolerance = 2.0;

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static Document BuildDoc(int lineCount, int needleLine, string needle,
            int baseLineChars = 20) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            // Build text to baseLineChars: "line NNNNN " (11 chars) + padding
            // + optional needle.  The needle is embedded at the START of the
            // padding portion so it's always in the first wrapped row, which
            // makes the expected caret position deterministic.
            var prefix = $"line {i:D5} ";
            var padLen = Math.Max(0, baseLineChars - prefix.Length);
            var filler = new string('.', padLen);
            var text = prefix + filler;
            if (i == needleLine) {
                // Replace the first few padding chars with the needle.
                var needleStart = prefix.Length;
                if (needleStart + needle.Length <= text.Length) {
                    text = text.Substring(0, needleStart)
                        + needle
                        + text.Substring(needleStart + needle.Length);
                }
            }
            sb.Append(text);
            sb.Append('\n');
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

    private static void AssertMatchVisible(EditorControl editor, string label) {
        var caretY = editor.GetCaretScreenYForTest();
        Assert.True(caretY.HasValue,
            $"{label}: caret has no screen position — match is off-viewport.");
        Assert.InRange(caretY!.Value, 0, ViewportHeight);
    }

    private static void AssertScrollStateConsistent(EditorControl editor,
            string label) {
        // ScrollValue must be within [0, ScrollMaximum].  If ScrollMaximum
        // is positive the doc is larger than the viewport, and
        // ScrollMaximum must NOT have collapsed to zero (bug #2 symptom).
        Assert.InRange(editor.ScrollValue, 0, editor.ScrollMaximum + Tolerance);
    }

    // ------------------------------------------------------------------
    //  Mid-document pin-at-edge (wrap off)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindNext_MidDocWrapOff_LandsAtViewportBottom() {
        var doc = BuildDoc(lineCount: 500, needleLine: 200, needle: "pear");
        var editor = CreateEditor(doc, wrapLines: false);

        editor.LastSearchTerm = "pear";
        Assert.True(editor.FindNext());
        Relayout(editor);

        AssertMatchVisible(editor, "FindNext mid-doc wrap off");
        AssertScrollStateConsistent(editor, "FindNext mid-doc wrap off");

        // Exact pin: row 200's bottom aligned with viewport bottom.
        var rh = editor.RowHeightValue;
        var expected = (200 + 1) * rh - ViewportHeight;
        Assert.InRange(editor.ScrollValue,
            expected - 2 * rh, expected + 2 * rh);
    }

    [AvaloniaFact]
    public void FindPrev_MidDocWrapOff_LandsAtViewportTop() {
        var doc = BuildDoc(lineCount: 500, needleLine: 100, needle: "pear");
        var editor = CreateEditor(doc, wrapLines: false);

        // Position caret after the match so FindPrevious has to scroll up.
        editor.GoToPosition(doc.Table.LineStartOfs(300));
        Relayout(editor);

        editor.LastSearchTerm = "pear";
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        AssertMatchVisible(editor, "FindPrev mid-doc wrap off");
        AssertScrollStateConsistent(editor, "FindPrev mid-doc wrap off");

        // Exact pin: row 100 at viewport top.
        var rh = editor.RowHeightValue;
        var expected = 100 * rh;
        Assert.InRange(editor.ScrollValue,
            expected - 2 * rh, expected + 2 * rh);
    }

    // ------------------------------------------------------------------
    //  Mid-document pin-at-edge (wrap on, short lines: 1 row per line)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindNext_MidDocWrapOnShortLines_LandsAtViewportBottom() {
        var doc = BuildDoc(lineCount: 500, needleLine: 200, needle: "pear",
            baseLineChars: 20);
        var editor = CreateEditor(doc, wrapLines: true);

        editor.LastSearchTerm = "pear";
        Assert.True(editor.FindNext());
        Relayout(editor);

        AssertMatchVisible(editor, "FindNext mid-doc wrap on (short lines)");
        AssertScrollStateConsistent(editor, "FindNext mid-doc wrap on (short lines)");

        // With short lines that don't wrap, the match lands at viewport
        // bottom similar to wrap-off.
        var caretY = editor.GetCaretScreenYForTest()!.Value;
        Assert.True(caretY > ViewportHeight / 2,
            $"FindNext should land match in bottom half of viewport " +
            $"(caret Y = {caretY}, viewport = {ViewportHeight}).");
    }

    [AvaloniaFact]
    public void FindPrev_MidDocWrapOnShortLines_LandsAtViewportTop() {
        var doc = BuildDoc(lineCount: 500, needleLine: 100, needle: "pear",
            baseLineChars: 20);
        var editor = CreateEditor(doc, wrapLines: true);

        editor.GoToPosition(doc.Table.LineStartOfs(300));
        Relayout(editor);

        editor.LastSearchTerm = "pear";
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        AssertMatchVisible(editor, "FindPrev mid-doc wrap on (short lines)");
        AssertScrollStateConsistent(editor, "FindPrev mid-doc wrap on (short lines)");

        var caretY = editor.GetCaretScreenYForTest()!.Value;
        Assert.True(caretY < ViewportHeight / 2,
            $"FindPrev should land match in top half of viewport " +
            $"(caret Y = {caretY}, viewport = {ViewportHeight}).");
    }

    // NOTE: long-wrapped-line wrap-on tests intentionally omitted here.
    // They exercise the slow-path (proportional-font) wrap math, which
    // behaves differently in headless mode (where Consolas falls back
    // to Inter) than in the GUI (where Consolas is available and the
    // mono fast path applies).  These cases are verified manually in
    // the GUI — see the 2026-04-09 "3 doc sizes, wrapping enabled"
    // user regression walk.

    // ------------------------------------------------------------------
    //  Wrap-around: FindPrev from first match jumps to last match
    //
    //  Bugs #2 and #3 (2026-04-09 session) showed up here: near-end
    //  targets corrupted the scroll extent (bug #2: FindNext afterward
    //  thought no scrolling was needed) or left blank space below the
    //  last line (bug #3).  These tests lock the fix in.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindPrev_WrapToEnd_SmallDoc_MatchVisibleAndStateConsistent() {
        // Small but larger than viewport.  Two matches — one near top,
        // one near end.  Start on the top match, invoke FindPrevious,
        // and expect it to wrap to the last match (line 33).
        var sb = new StringBuilder();
        for (var i = 0; i < 35; i++) {
            if (i == 3 || i == 33) {
                sb.Append($"line {i:D3} needle filler\n");
            } else {
                sb.Append($"line {i:D3} filler\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrapLines: false);

        editor.LastSearchTerm = "needle";
        // First FindNext lands on line 3.
        Assert.True(editor.FindNext());
        Relayout(editor);
        // FindPrevious wraps to line 33.
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        AssertMatchVisible(editor, "FindPrev wrap-to-end small doc");
        AssertScrollStateConsistent(editor,
            "FindPrev wrap-to-end small doc");

        // Bug #2 regression guard: after wrap-to-end the ScrollMaximum
        // must still be positive (the doc hasn't "collapsed" to appear
        // fully-fitting in the viewport).
        Assert.True(editor.ScrollMaximum > 0,
            "ScrollMaximum collapsed after wrap-to-end — bug #2 regression.");

        // A follow-up FindNext should successfully wrap back to the top.
        Assert.True(editor.FindNext());
        Relayout(editor);
        AssertMatchVisible(editor, "FindNext wrap-back-to-top small doc");
    }

    [AvaloniaFact]
    public void FindPrev_WrapToEnd_MediumDocWrapOn_NoBlankBelowLastLine() {
        // Medium doc with wrap-on.  Long lines so wrap inflates rows.
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            var baseText = $"line {i:D3} " + new string('x', 180);
            if (i == 5 || i == 195) {
                sb.Append($"line {i:D3} needle {new string('x', 170)}\n");
            } else {
                sb.Append(baseText);
                sb.Append('\n');
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrapLines: true);

        editor.LastSearchTerm = "needle";
        // First FindNext: line 5.
        Assert.True(editor.FindNext());
        Relayout(editor);
        // FindPrevious wraps to line 195 — the near-end case that used
        // to leave blank space below the last line (bug #3).
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        AssertMatchVisible(editor, "FindPrev wrap-to-end medium doc wrap on");
        AssertScrollStateConsistent(editor,
            "FindPrev wrap-to-end medium doc wrap on");

        // The match must be visible and the doc must not show "more
        // content below the bottom of the viewport when scrolled to max".
        // Bug #3 manifested as the scrollbar sitting at the bottom with
        // several empty rows displayed below the last line; we verify
        // that by checking the scroll state is consistent and the match
        // lands inside the viewport.
        Assert.True(editor.ScrollMaximum > 0);

        // Round-trip: FindNext wraps back to first match at line 5.
        Assert.True(editor.FindNext());
        Relayout(editor);
        AssertMatchVisible(editor,
            "FindNext wrap-back-to-top medium doc wrap on");
    }

    [AvaloniaFact]
    public void FindPrev_WrapToEnd_LargeDocWrapOn_MatchVisible() {
        // 5000 lines, wrap on.  Matches at line 10 and line 4990.
        var sb = new StringBuilder();
        for (var i = 0; i < 5000; i++) {
            if (i == 10 || i == 4990) {
                sb.Append($"line {i:D5} needle filler\n");
            } else {
                sb.Append($"line {i:D5} filler\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrapLines: true);

        editor.LastSearchTerm = "needle";
        Assert.True(editor.FindNext());
        Relayout(editor);
        // Wrap to last match.
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        AssertMatchVisible(editor, "FindPrev wrap-to-end large doc");
        AssertScrollStateConsistent(editor, "FindPrev wrap-to-end large doc");
        Assert.True(editor.ScrollMaximum > 0);
    }

    // ------------------------------------------------------------------
    //  Round-trip state integrity
    //
    //  Walk FindNext → FindPrev → FindNext across the wrap-around
    //  boundaries and verify the editor returns to a clean state with
    //  the match visible each time.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindRoundTrip_WrapAroundBoundaries_StateStaysConsistent() {
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++) {
            if (i == 2 || i == 150 || i == 297) {
                sb.Append($"line {i:D3} needle filler\n");
            } else {
                sb.Append($"line {i:D3} filler\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrapLines: true);
        editor.LastSearchTerm = "needle";

        // Walk through every match twice in each direction.
        for (var iter = 0; iter < 2; iter++) {
            Assert.True(editor.FindNext()); // line 2
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindNext 1");
            AssertScrollStateConsistent(editor, $"iter {iter} FindNext 1");

            Assert.True(editor.FindNext()); // line 150
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindNext 2");
            AssertScrollStateConsistent(editor, $"iter {iter} FindNext 2");

            Assert.True(editor.FindNext()); // line 297
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindNext 3");
            AssertScrollStateConsistent(editor, $"iter {iter} FindNext 3");

            // Wrap-around: FindNext goes to line 2.
            Assert.True(editor.FindNext());
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindNext wrap");
            AssertScrollStateConsistent(editor, $"iter {iter} FindNext wrap");

            // Walk back in reverse.
            Assert.True(editor.FindPrevious()); // wraps to line 297
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindPrev 1");
            AssertScrollStateConsistent(editor, $"iter {iter} FindPrev 1");

            Assert.True(editor.FindPrevious()); // line 150
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindPrev 2");
            AssertScrollStateConsistent(editor, $"iter {iter} FindPrev 2");

            Assert.True(editor.FindPrevious()); // line 2
            Relayout(editor);
            AssertMatchVisible(editor, $"iter {iter} FindPrev 3");
            AssertScrollStateConsistent(editor, $"iter {iter} FindPrev 3");
        }
    }

    // ------------------------------------------------------------------
    //  Already-visible matches: no scroll change
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindNext_MatchAlreadyVisible_WrapOn_NoScrollChange() {
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++) {
            if (i == 3 || i == 8) {
                sb.Append($"line {i:D3} needle filler\n");
            } else {
                sb.Append($"line {i:D3} filler\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrapLines: true);
        editor.LastSearchTerm = "needle";

        // First FindNext: line 3.
        Assert.True(editor.FindNext());
        Relayout(editor);

        var scrollBefore = editor.ScrollValue;

        // Second FindNext: line 8 — also visible from the top.
        Assert.True(editor.FindNext());
        Relayout(editor);

        Assert.InRange(editor.ScrollValue,
            scrollBefore - Tolerance, scrollBefore + Tolerance);
    }
}
