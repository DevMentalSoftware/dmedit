using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Stress tests for FindNext / FindPrevious on wrapped documents.
/// Targets the scenario that had known issues: ~100 lines wrapping
/// to ~250 visual rows.  Each test walks Find through every match
/// in the document, asserting caret visibility and state consistency
/// at each step, with a hard step limit to catch infinite loops.
///
/// Test doc structure: 100 lines, each ~150 chars (wraps to 2-3 rows
/// at 600px viewport width with Consolas 14pt ≈ 65 chars/row).
/// A "needle" word is planted on specific lines.
/// </summary>
public class FindStressTests {
    private const double VpW = 600;
    private const double VpH = 400;
    private const int MaxSteps = 500; // hard limit to catch infinite loops

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

    /// <summary>
    /// Creates a document with <paramref name="lineCount"/> lines, each
    /// <paramref name="lineLen"/> chars, with the string "NEEDLE" planted
    /// on every <paramref name="needleEvery"/>-th line at
    /// <paramref name="needleCol"/> chars into the line.
    /// </summary>
    private static (Document doc, int matchCount) MakeNeedleDoc(
            int lineCount, int lineLen, int needleEvery, int needleCol) {
        var sb = new StringBuilder();
        var matchCount = 0;
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4} ";
            var padLen = Math.Max(0, lineLen - prefix.Length);
            var line = prefix + new string('x', padLen);
            if (i % needleEvery == 0 && needleCol + 6 <= line.Length) {
                line = line.Substring(0, needleCol) + "NEEDLE"
                    + line.Substring(needleCol + 6);
                matchCount++;
            }
            sb.Append(line);
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return (doc, matchCount);
    }

    private static void AssertCaretOnScreen(EditorControl e, string label) {
        var y = e.GetCaretScreenYForTest();
        Assert.True(y.HasValue, $"{label}: caret not on screen");
        Assert.InRange(y!.Value, -1, VpH + 1);
    }

    private static void AssertScrollConsistent(EditorControl e, string label) {
        Assert.InRange(e.ScrollValue, 0, e.ScrollMaximum + 2);
        Assert.True(e.ScrollMaximum > 0,
            $"{label}: ScrollMaximum collapsed to 0 (extent bug)");
    }

    // ------------------------------------------------------------------
    //  Walk all matches forward (FindNext)
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, 5, 10, true, "100x150-every5-col10-wrap")]
    [InlineData(100, 150, 5, 10, false, "100x150-every5-col10-noWrap")]
    [InlineData(100, 150, 10, 80, true, "100x150-every10-col80-wrap")]
    [InlineData(100, 150, 3, 5, true, "100x150-every3-col5-wrap")]
    [InlineData(50, 300, 5, 150, true, "50x300-every5-col150-wrap")]
    [InlineData(200, 80, 7, 20, true, "200x80-every7-col20-wrap")]
    public void FindNextWalk_AllMatchesVisible(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, expectedMatches) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);

        editor.LastSearchTerm = "NEEDLE";
        var matchesFound = 0;
        var firstMatchStart = -1L;

        for (var step = 0; step < MaxSteps; step++) {
            var found = editor.FindNext();
            Relayout(editor);

            if (!found) {
                Assert.Fail($"{desc}: FindNext returned false at step {step}, " +
                    $"expected {expectedMatches} matches");
            }

            var matchStart = doc.Selection.Start;

            if (step == 0) {
                firstMatchStart = matchStart;
            } else if (matchStart == firstMatchStart) {
                // Wrapped around to the first match — we've visited all.
                break;
            }

            matchesFound++;

            AssertCaretOnScreen(editor, $"{desc} step {step} (match at {matchStart})");
            AssertScrollConsistent(editor, $"{desc} step {step}");

            // Selection should cover exactly "NEEDLE" (6 chars).
            Assert.Equal(6, doc.Selection.Len);
        }

        Assert.Equal(expectedMatches, matchesFound);
    }

    // ------------------------------------------------------------------
    //  Walk all matches backward (FindPrevious)
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, 5, 10, true, "100x150-every5-col10-wrap")]
    [InlineData(100, 150, 5, 10, false, "100x150-every5-col10-noWrap")]
    [InlineData(100, 150, 10, 80, true, "100x150-every10-col80-wrap")]
    [InlineData(100, 150, 3, 5, true, "100x150-every3-col5-wrap")]
    [InlineData(50, 300, 5, 150, true, "50x300-every5-col150-wrap")]
    [InlineData(200, 80, 7, 20, true, "200x80-every7-col20-wrap")]
    public void FindPrevWalk_AllMatchesVisible(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, expectedMatches) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);

        // Start from the end so FindPrevious walks backward through all.
        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);

        editor.LastSearchTerm = "NEEDLE";
        var matchesFound = 0;
        var firstMatchStart = -1L;

        for (var step = 0; step < MaxSteps; step++) {
            var found = editor.FindPrevious();
            Relayout(editor);

            if (!found) {
                Assert.Fail($"{desc}: FindPrevious returned false at step {step}");
            }

            var matchStart = doc.Selection.Start;

            if (step == 0) {
                firstMatchStart = matchStart;
            } else if (matchStart == firstMatchStart) {
                break; // wrapped around
            }

            matchesFound++;

            AssertCaretOnScreen(editor, $"{desc} step {step} (match at {matchStart})");
            AssertScrollConsistent(editor, $"{desc} step {step}");
            Assert.Equal(6, doc.Selection.Len);
        }

        Assert.Equal(expectedMatches, matchesFound);
    }

    // ------------------------------------------------------------------
    //  Alternating: FindNext then FindPrev — state stays consistent
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, 5, 10, true, "100x150-wrap")]
    [InlineData(100, 150, 5, 10, false, "100x150-noWrap")]
    [InlineData(50, 300, 5, 150, true, "50x300-wrap")]
    public void FindAlternating_StateConsistent(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, _) = MakeNeedleDoc(lineCount, lineLen, needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        // Walk forward 5 matches.
        for (var i = 0; i < 5; i++) {
            Assert.True(editor.FindNext(), $"{desc}: FindNext {i} failed");
            Relayout(editor);
            AssertCaretOnScreen(editor, $"{desc} fwd {i}");
            AssertScrollConsistent(editor, $"{desc} fwd {i}");
        }

        var midMatchStart = doc.Selection.Start;

        // Walk backward 3 matches.
        for (var i = 0; i < 3; i++) {
            Assert.True(editor.FindPrevious(), $"{desc}: FindPrev {i} failed");
            Relayout(editor);
            AssertCaretOnScreen(editor, $"{desc} bwd {i}");
            AssertScrollConsistent(editor, $"{desc} bwd {i}");
        }

        // Walk forward again — should pass through the mid match.
        for (var i = 0; i < 5; i++) {
            Assert.True(editor.FindNext(), $"{desc}: FindNext2 {i} failed");
            Relayout(editor);
            AssertCaretOnScreen(editor, $"{desc} fwd2 {i}");
            AssertScrollConsistent(editor, $"{desc} fwd2 {i}");
        }
    }

    // ------------------------------------------------------------------
    //  Wrap-around: caret near end, FindNext wraps to start
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, 10, 10, true, "100x150-wrap")]
    [InlineData(100, 150, 10, 10, false, "100x150-noWrap")]
    public void FindNext_WrapAround_MatchVisible(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, matchCount) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);

        // Position past the last match.
        var lastMatchLine = ((lineCount - 1) / needleEvery) * needleEvery;
        doc.Selection = Selection.Collapsed(
            doc.Table.LineStartOfs(Math.Min(lastMatchLine + 1, lineCount - 1)));
        editor.ScrollCaretIntoView();
        Relayout(editor);

        editor.LastSearchTerm = "NEEDLE";

        // FindNext should wrap to the first match (line 0).
        Assert.True(editor.FindNext());
        Relayout(editor);

        var matchLine = doc.Table.LineFromOfs(doc.Selection.Start);
        Assert.Equal(0, matchLine);
        AssertCaretOnScreen(editor, $"{desc} wrap-around");
        AssertScrollConsistent(editor, $"{desc} wrap-around");
    }

    [AvaloniaTheory]
    [InlineData(100, 150, 10, 10, true, "100x150-wrap")]
    [InlineData(100, 150, 10, 10, false, "100x150-noWrap")]
    public void FindPrev_WrapAround_MatchVisible(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, _) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);

        // Position before the first match.
        editor.LastSearchTerm = "NEEDLE";
        Assert.True(editor.FindNext()); // lands on line 0 match
        Relayout(editor);

        // FindPrevious should wrap to the last match.
        Assert.True(editor.FindPrevious());
        Relayout(editor);

        var matchLine = doc.Table.LineFromOfs(doc.Selection.Start);
        var lastMatchLine = ((lineCount - 1) / needleEvery) * needleEvery;
        Assert.Equal(lastMatchLine, matchLine);
        AssertCaretOnScreen(editor, $"{desc} wrap-back");
        AssertScrollConsistent(editor, $"{desc} wrap-back");
    }

    // ------------------------------------------------------------------
    //  Match on a wrapped continuation row (not row 0 of its line)
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindNext_MatchOnContinuationRow_Visible(bool wrap) {
        // 50 lines × 200 chars.  Needle at column 130 — this is past
        // the first row's char count (~65 chars), so the match is on a
        // continuation row (row 1 or 2 of the wrapped line).
        var (doc, matchCount) = MakeNeedleDoc(50, 200, 5, 130);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        for (var step = 0; step < matchCount + 2; step++) {
            var before = doc.Selection.Start;
            if (!editor.FindNext()) break;
            Relayout(editor);
            if (doc.Selection.Start == before && step > 0) break;

            AssertCaretOnScreen(editor,
                $"wrap={wrap} step {step} match at {doc.Selection.Start}");
            AssertScrollConsistent(editor,
                $"wrap={wrap} step {step}");
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindPrev_MatchOnContinuationRow_Visible(bool wrap) {
        var (doc, matchCount) = MakeNeedleDoc(50, 200, 5, 130);
        var editor = CreateEditor(doc, wrap);

        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);
        editor.LastSearchTerm = "NEEDLE";

        for (var step = 0; step < matchCount + 2; step++) {
            var before = doc.Selection.Start;
            if (!editor.FindPrevious()) break;
            Relayout(editor);
            if (doc.Selection.Start == before && step > 0) break;

            AssertCaretOnScreen(editor,
                $"wrap={wrap} step {step} match at {doc.Selection.Start}");
            AssertScrollConsistent(editor,
                $"wrap={wrap} step {step}");
        }
    }

    // ------------------------------------------------------------------
    //  Single match — FindNext then FindPrev should not infinite-loop
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SingleMatch_FindNextThenPrev_NoLoop(bool wrap) {
        // Only one match in the whole document.
        var (doc, _) = MakeNeedleDoc(100, 150, 999, 10);
        // Plant exactly one needle manually.
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            var prefix = $"L{i:D4} ";
            var pad = new string('x', 150 - prefix.Length);
            if (i == 50) {
                pad = pad.Substring(0, 10) + "NEEDLE" + pad.Substring(16);
            }
            sb.Append(prefix + pad + '\n');
        }
        doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        // FindNext should find the single match.
        Assert.True(editor.FindNext());
        Relayout(editor);
        var matchStart = doc.Selection.Start;
        AssertCaretOnScreen(editor, "single-FindNext");

        // FindNext again should wrap and find the same match.
        Assert.True(editor.FindNext());
        Relayout(editor);
        Assert.Equal(matchStart, doc.Selection.Start);
        AssertCaretOnScreen(editor, "single-FindNext-wrap");

        // FindPrevious should also find the same match.
        Assert.True(editor.FindPrevious());
        Relayout(editor);
        Assert.Equal(matchStart, doc.Selection.Start);
        AssertCaretOnScreen(editor, "single-FindPrev");
    }

    // ------------------------------------------------------------------
    //  Scroll state integrity after many Find operations
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, 5, 10, true, "100x150-wrap")]
    [InlineData(100, 150, 5, 10, false, "100x150-noWrap")]
    public void ManyFinds_ScrollMaximumStable(int lineCount, int lineLen,
            int needleEvery, int needleCol, bool wrap, string desc) {
        var (doc, matchCount) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery, needleCol);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        // Capture initial ScrollMaximum (after first layout).
        Relayout(editor);
        var initialMax = editor.ScrollMaximum;

        // Walk through all matches twice.
        for (var step = 0; step < matchCount * 2 + 5; step++) {
            if (!editor.FindNext()) break;
            Relayout(editor);
        }

        // ScrollMaximum must not drift.  The 2026-04-09 bug #2 caused
        // collapse to 0; the 2026-04-11 user report showed inflation to
        // 2-3× the real value (exact-pin extent used estimate-based
        // scrollY + vpH instead of totalVisualRows * rh).  Allow 10%
        // tolerance for estimate-based row count rounding.
        Assert.InRange(editor.ScrollMaximum,
            initialMax * 0.9, initialMax * 1.1);
    }

    // ------------------------------------------------------------------
    //  Dense matches: every line has a match
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(100, 150, true, "100x150-wrap")]
    [InlineData(100, 150, false, "100x150-noWrap")]
    [InlineData(50, 300, true, "50x300-wrap")]
    public void DenseMatches_FindNextWalk(int lineCount, int lineLen,
            bool wrap, string desc) {
        var (doc, matchCount) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery: 1, needleCol: 5);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        var firstMatch = -1L;
        var step = 0;
        for (; step < MaxSteps; step++) {
            if (!editor.FindNext()) {
                Assert.Fail($"{desc}: FindNext returned false at step {step}");
            }
            Relayout(editor);

            if (step == 0) {
                firstMatch = doc.Selection.Start;
            } else if (doc.Selection.Start == firstMatch) {
                break; // wrapped
            }

            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }

        Assert.Equal(matchCount, step);
    }

    [AvaloniaTheory]
    [InlineData(100, 150, true, "100x150-wrap")]
    [InlineData(100, 150, false, "100x150-noWrap")]
    public void DenseMatches_FindPrevWalk(int lineCount, int lineLen,
            bool wrap, string desc) {
        var (doc, matchCount) = MakeNeedleDoc(lineCount, lineLen,
            needleEvery: 1, needleCol: 5);
        var editor = CreateEditor(doc, wrap);

        doc.Selection = Selection.Collapsed(doc.Table.Length);
        editor.ScrollCaretIntoView(ScrollPolicy.Bottom);
        Relayout(editor);
        editor.LastSearchTerm = "NEEDLE";

        var firstMatch = -1L;
        var step = 0;
        for (; step < MaxSteps; step++) {
            if (!editor.FindPrevious()) {
                Assert.Fail($"{desc}: FindPrev returned false at step {step}");
            }
            Relayout(editor);

            if (step == 0) {
                firstMatch = doc.Selection.Start;
            } else if (doc.Selection.Start == firstMatch) {
                break;
            }

            AssertCaretOnScreen(editor, $"{desc} step {step}");
        }

        Assert.Equal(matchCount, step);
    }

    // ------------------------------------------------------------------
    //  Two matches, far apart — exercises the long-scroll path
    // ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoMatchesFarApart_FindNextBounces(bool wrap) {
        // 200 lines, matches only on lines 5 and 195.
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            var prefix = $"L{i:D4} ";
            var pad = new string('x', 150 - prefix.Length);
            if (i == 5 || i == 195) {
                pad = pad.Substring(0, 10) + "NEEDLE" + pad.Substring(16);
            }
            sb.Append(prefix + pad + '\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc, wrap);
        editor.LastSearchTerm = "NEEDLE";

        // FindNext → line 5.
        Assert.True(editor.FindNext());
        Relayout(editor);
        Assert.Equal(5, (int)doc.Table.LineFromOfs(doc.Selection.Start));
        AssertCaretOnScreen(editor, "first match");
        AssertScrollConsistent(editor, "first match");

        // FindNext → line 195 (long scroll forward).
        Assert.True(editor.FindNext());
        Relayout(editor);
        Assert.Equal(195, (int)doc.Table.LineFromOfs(doc.Selection.Start));
        AssertCaretOnScreen(editor, "second match");
        AssertScrollConsistent(editor, "second match");

        // FindNext → wraps to line 5 (long scroll backward).
        Assert.True(editor.FindNext());
        Relayout(editor);
        Assert.Equal(5, (int)doc.Table.LineFromOfs(doc.Selection.Start));
        AssertCaretOnScreen(editor, "wrap to first");
        AssertScrollConsistent(editor, "wrap to first");

        // FindPrevious → line 195 (long scroll forward from near-start).
        Assert.True(editor.FindPrevious());
        Relayout(editor);
        Assert.Equal(195, (int)doc.Table.LineFromOfs(doc.Selection.Start));
        AssertCaretOnScreen(editor, "prev to second");
        AssertScrollConsistent(editor, "prev to second");
    }
}
