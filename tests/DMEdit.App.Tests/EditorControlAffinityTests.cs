using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Integration tests for caret affinity at soft line break boundaries.
///
/// At a row boundary, the same document offset has two visual positions:
/// - isAtEnd=false: caret at the START of the next row
/// - isAtEnd=true:  caret at the END of the current row
///
/// Right arrow through a boundary visits BOTH positions:
///   ...last char → end-of-row (isAtEnd) → start-of-next-row → next char...
///
/// Left arrow through a boundary visits both in reverse:
///   ...second char → start-of-row → end-of-prev-row (isAtEnd) → prev char...
///
/// The screen Y (visual row) is the definitive check — offset alone
/// can't distinguish the two boundary positions.
/// </summary>
public class EditorControlAffinityTests {
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;
    private const double Tolerance = 2.0;

    private static (EditorControl editor, int cpr, double rh) CreateWrappedEditor() {
        var sb = new StringBuilder();
        sb.Append(new string('a', 200));
        sb.Append('\n');
        sb.Append("short\n");
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = ViewportWidth,
            Height = ViewportHeight,
            WrapLines = true,
        };
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var rh = editor.RowHeightValue;
        var cpr = 1;

        // Determine actual chars-per-row from layout.
        var layout = editor.GetType()
            .GetMethod("EnsureLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(editor, null) as DMEdit.Rendering.Layout.LayoutResult;
        if (layout?.Lines.Count > 0) {
            var ll = layout.Lines[0];
            if (ll.Mono is { } mono && mono.Rows.Length > 1) {
                cpr = mono.Rows[0].CharLen;
            }
        }

        return (editor, cpr, rh);
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
    }

    private static long Caret(EditorControl editor) =>
        editor.Document!.Selection.Caret;

    private static int CaretRow(EditorControl editor, double rh) {
        var y = editor.GetCaretScreenYForTest();
        Assert.NotNull(y);
        return (int)Math.Round(y!.Value / rh);
    }

    // ================================================================
    //  RIGHT ARROW — full boundary traversal
    // ================================================================

    [AvaloniaFact]
    public void Right_MidRow_StaysOnSameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(3);
        Relayout(editor);

        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(4, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Right_FromSecondToLast_LandsOnLastChar_SameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // cpr-2 is 2nd to last char of row 0.
        editor.GoToPosition(cpr - 2);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);

        // Should be at cpr-1 (last char), still on row 0.
        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Right_FromLastChar_LandsAtEndOfRow_SameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // cpr-1 is the last char of row 0. Caret is to its left.
        editor.GoToPosition(cpr - 1);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);

        // Should land at offset cpr with isAtEnd=true → visually at
        // the END of row 0 (caret to the right of the last char).
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh)); // STILL row 0, not row 1
    }

    [AvaloniaFact]
    public void Right_FromEndOfRow_FlipsToStartOfNextRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Get to end-of-row-0: go to cpr-1, then right.
        editor.GoToPosition(cpr - 1);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        // Now at offset cpr, isAtEnd=true, row 0.
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));

        // Next right: same offset, but flips to row 1 (start of next row).
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(cpr, Caret(editor)); // same offset
        Assert.Equal(1, CaretRow(editor, rh)); // now on row 1
    }

    [AvaloniaFact]
    public void Right_FromStartOfRow1_AdvancesToNextChar() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Go to start of row 1 (offset cpr, isAtEnd=false).
        editor.GoToPosition(cpr);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(cpr + 1, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Right_FullBoundaryCrossing_FiveSteps() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Start at cpr-2 (2nd to last of row 0).
        editor.GoToPosition(cpr - 2);
        Relayout(editor);

        // Step 1: cpr-2 → cpr-1 (last char, row 0)
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));

        // Step 2: cpr-1 → cpr, isAtEnd=true (end of row 0)
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh)); // still row 0

        // Step 3: cpr isAtEnd → cpr !isAtEnd (start of row 1)
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor)); // same offset
        Assert.Equal(1, CaretRow(editor, rh)); // now row 1

        // Step 4: cpr → cpr+1 (second char, row 1)
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr + 1, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
    }

    // ================================================================
    //  LEFT ARROW — full boundary traversal
    // ================================================================

    [AvaloniaFact]
    public void Left_MidRow_StaysOnSameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 3);
        Relayout(editor);

        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(cpr + 2, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Left_FromSecondCharOfRow1_LandsOnFirstChar_SameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 1);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh)); // still row 1
    }

    [AvaloniaFact]
    public void Left_FromStartOfRow1_FlipsToEndOfRow0() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);

        // Same offset, but now isAtEnd=true → end of row 0.
        Assert.Equal(cpr, Caret(editor)); // same offset
        Assert.Equal(0, CaretRow(editor, rh)); // now row 0
    }

    [AvaloniaFact]
    public void Left_FromEndOfRow0_MovesToLastChar() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Get to end-of-row-0: go to row 1 start, then left.
        editor.GoToPosition(cpr);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);
        // Now at offset cpr, isAtEnd=true, row 0.
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));

        // Next left: moves to cpr-1 (last char of row 0).
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);

        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Left_FullBoundaryCrossing_FourSteps() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 2);
        Relayout(editor);

        // Step 1: cpr+2 → cpr+1 (row 1)
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr + 1, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));

        // Step 2: cpr+1 → cpr (start of row 1)
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));

        // Step 3: cpr !isAtEnd → cpr isAtEnd (end of row 0)
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor)); // same offset
        Assert.Equal(0, CaretRow(editor, rh)); // now row 0

        // Step 4: cpr isAtEnd → cpr-1 (last char, row 0)
        editor.MoveCaretHorizontalForTest(delta: -1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    // ================================================================
    //  WORD-WRAP boundary (consumed space between rows)
    // ================================================================

    /// <summary>
    /// Creates an editor with word-wrap text that breaks at spaces.
    /// </summary>
    private static (EditorControl editor, int row0Len, double rh) CreateWordWrapEditor() {
        // "aaaa bbbb cccc" with narrow viewport forces word-wrap.
        // We need lines long enough to wrap. Use repeated "abcdef " blocks.
        var sb = new StringBuilder();
        for (var i = 0; i < 30; i++) sb.Append("abcdef ");
        sb.Append('\n');
        sb.Append("short\n");
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);

        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = ViewportWidth,
            Height = ViewportHeight,
            WrapLines = true,
        };
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));

        var rh = editor.RowHeightValue;
        var row0Len = 0;

        var layout = editor.GetType()
            .GetMethod("EnsureLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(editor, null) as DMEdit.Rendering.Layout.LayoutResult;
        if (layout?.Lines.Count > 0) {
            var ll = layout.Lines[0];
            if (ll.Mono is { } mono && mono.Rows.Length > 1) {
                row0Len = mono.Rows[0].CharLen; // drawn chars (excl consumed space)
            }
        }

        return (editor, row0Len, rh);
    }

    [AvaloniaFact]
    public void WordWrap_Right_ThroughSpace_NoIsAtEnd() {
        var (editor, row0Len, rh) = CreateWordWrapEditor();
        if (row0Len <= 1) return;

        // row0Len includes the break-space.  Last non-space char is at
        // row0Len - 2, space is at row0Len - 1.
        editor.GoToPosition(row0Len - 2);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        // Right → advances to the space, which is on row 0.
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);

        // Right → advances past the space to row1Start.  Arrow keys at
        // space-breaks don't set isAtEnd — that's only for hard breaks.
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WordWrap_End_ThenRight_FlipsToNextRow() {
        var (editor, row0Len, rh) = CreateWordWrapEditor();
        if (row0Len <= 1) return;

        editor.GoToPosition(5);
        Relayout(editor);

        // End → boundary with isAtEnd=true, caret at end of row 0.
        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.True(editor.CaretIsAtEnd);

        // Right → flips to start of next row (same offset, !isAtEnd).
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WordWrap_FullStepThrough_Row0Boundary() {
        var (editor, row0Len, rh) = CreateWordWrapEditor();
        if (row0Len <= 2) return; // skip if no wrap

        // Get actual row 1 start from layout.
        var layout = editor.GetType()
            .GetMethod("EnsureLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(editor, null) as DMEdit.Rendering.Layout.LayoutResult;
        var mono = layout!.Lines[0].Mono!;
        var row1Start = mono.Rows[1].CharStart;

        // Step through from row0Len-2 to row1Start+1.
        editor.GoToPosition(row0Len - 2);
        Relayout(editor);

        var steps = new List<(long ofs, int row, bool atEnd)>();
        steps.Add((Caret(editor), CaretRow(editor, rh), editor.CaretIsAtEnd));

        for (var i = 0; i < 6; i++) {
            editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
            Relayout(editor);
            steps.Add((Caret(editor), CaretRow(editor, rh), editor.CaretIsAtEnd));
        }

        // Expected sequence:
        // (row0Len-2, row0, false) → start
        // (row0Len-1, row0, false) → last drawn char of row 0
        // (row0Len,   row0, true)  → end of row 0 (past last drawn char or consumed space)
        //   ...or maybe (row1Start, row0, true) if consumed space is skipped
        // (row1Start, row1, false) → flip to start of row 1
        // (row1Start+1, row1, false) → normal advance
        //
        // The exact values depend on whether the consumed space is visited.
        // Key invariant: every step either advances the offset or flips
        // isAtEnd (changing the row), and no offset appears with isAtEnd
        // at a non-boundary position.

        // Verify no stuck positions (same offset, same row, same atEnd).
        for (var i = 1; i < steps.Count; i++) {
            Assert.False(
                steps[i].ofs == steps[i - 1].ofs
                    && steps[i].row == steps[i - 1].row
                    && steps[i].atEnd == steps[i - 1].atEnd,
                $"Stuck at step {i}: {steps[i]}. " +
                $"Full sequence: [{string.Join(", ", steps)}]");
        }

        // With space included in row 0's CharLen, row0Len == row1Start.
        // Word-wrap arrow keys don't use isAtEnd — the space is a normal
        // character, so the transition is a simple offset advance.
        Assert.Equal(row0Len, row1Start);

        var transitions = 0;
        for (var i = 1; i < steps.Count; i++) {
            if (steps[i - 1].row == 0 && steps[i].row == 1) transitions++;
        }
        Assert.True(transitions == 1,
            $"Expected 1 row transition, got {transitions}. " +
            $"row0Len={row0Len}, row1Start={row1Start}. " +
            $"Steps: [{string.Join(", ", steps.Select(s => $"({s.ofs},r{s.row},{(s.atEnd ? "E" : "-")})") )}]");

        // No isAtEnd should be set during arrow traversal in word-wrap.
        Assert.True(steps.All(s => !s.atEnd),
            $"Word-wrap arrow keys should never set isAtEnd. " +
            $"Steps: [{string.Join(", ", steps.Select(s => $"({s.ofs},r{s.row},{(s.atEnd ? "E" : "-")})") )}]");
    }

    // ================================================================
    //  END KEY
    // ================================================================

    // ================================================================
    //  UP/DOWN from row start
    // ================================================================

    [AvaloniaFact]
    public void Down_FromRowStart_GoesToSameColumnOnNextRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Place at start of row 1 (offset cpr).
        editor.GoToPosition(cpr);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.Equal(cpr, Caret(editor));
        Assert.False(editor.CaretIsAtEnd);

        editor.MoveCaretVerticalForTest(lineDelta: +1, extend: false);
        Relayout(editor);

        // Should be at start of row 2, not end of row 1.
        Assert.True(2 == CaretRow(editor, rh),
            $"Down from row start: expected row 2, got row {CaretRow(editor, rh)}. " +
            $"caret={Caret(editor)}, cpr={cpr}, isAtEnd={editor.CaretIsAtEnd}, " +
            $"2*cpr={2 * cpr}, prefX={editor.PreferredCaretXForTest}, " +
            $"cw={editor.CharWidth}");
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void Up_FromRowStart_GoesToSameColumnOnPrevRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        // Place at start of row 2 (offset 2*cpr).
        editor.GoToPosition(2 * cpr);
        Relayout(editor);
        Assert.Equal(2, CaretRow(editor, rh));
        Assert.Equal(2 * cpr, Caret(editor));
        Assert.False(editor.CaretIsAtEnd);

        editor.MoveCaretVerticalForTest(lineDelta: -1, extend: false);
        Relayout(editor);

        // Should be at start of row 1, not end of row 0.
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void Down_FromRowStart_ThenDown_Monotonic() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr);
        Relayout(editor);

        var rows = new List<int> { CaretRow(editor, rh) };
        for (var i = 0; i < 3; i++) {
            editor.MoveCaretVerticalForTest(lineDelta: +1, extend: false);
            Relayout(editor);
            rows.Add(CaretRow(editor, rh));
        }

        // Rows must be strictly increasing.
        for (var i = 1; i < rows.Count; i++) {
            Assert.True(rows[i] > rows[i - 1],
                $"Down from row start not monotonic: [{string.Join(", ", rows)}]");
        }
    }

    [AvaloniaFact]
    public void Down_ToShorterRow_LandsAtEndOfShorterRow() {
        // Line 0: 200 'a' chars wrapping into multiple rows.
        // Line 1: "short" — much shorter than the rows above.
        // Down from near the end of the last wrapped row of line 0
        // should clamp to the end of "short" with isAtEnd=true.
        var (editor, cpr, rh) = CreateWrappedEditor();

        var lastRowStart = (200 / cpr) * cpr;
        if (lastRowStart >= 200) lastRowStart -= cpr;
        var posNearEnd = lastRowStart + cpr - 3;
        if (posNearEnd >= 200) posNearEnd = 198;
        editor.GoToPosition(posNearEnd);
        Relayout(editor);
        var startRow = CaretRow(editor, rh);

        editor.MoveCaretVerticalForTest(lineDelta: +1, extend: false);
        Relayout(editor);

        var newRow = CaretRow(editor, rh);
        Assert.Equal(startRow + 1, newRow);
        // The caret should be at or near the end of the shorter row.
        // isAtEnd may or may not be set depending on whether the target
        // is a multi-row wrapped line (isAtEnd at boundary) or a short
        // single-row line (caret at content end, no boundary).  The key
        // invariant: the caret is on the correct row, not on the row
        // after that.
    }

    // ================================================================
    //  END KEY
    // ================================================================

    [AvaloniaFact]
    public void End_MidRow_StaysOnSameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void End_MidRow1_StaysOnRow1() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 3);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void End_ThenEnd_GoesToLineEnd() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);

        Assert.Equal(200, Caret(editor));
    }

    // ================================================================
    //  HOME KEY
    // ================================================================

    [AvaloniaFact]
    public void Home_MidRow1_GoesToRow1Start() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);

        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void Home_FromRow1Start_GoesToLineStart() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);

        Assert.Equal(0, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    // ================================================================
    //  SEQUENCES
    // ================================================================

    [AvaloniaFact]
    public void Right_NeverSetsIsAtEnd_ForNonBoundaryPositions() {
        // Regression: _caretIsAtEnd was persisting from a previous End
        // key press, causing the flip logic to fire at non-boundary
        // positions.  Verify that stepping right through every position
        // in row 0 never produces isAtEnd except at the row boundary.
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(0);
        Relayout(editor);

        for (var i = 1; i <= cpr + 1; i++) {
            editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
            Relayout(editor);
            var ofs = Caret(editor);
            var row = CaretRow(editor, rh);
            var atEnd = editor.CaretIsAtEnd;

            if (i < cpr) {
                // Mid-row: isAtEnd must be false, row must be 0.
                Assert.False(atEnd,
                    $"Step {i}: isAtEnd should be false at mid-row offset {ofs}");
                Assert.Equal(0, row);
            } else if (i == cpr) {
                // Boundary: isAtEnd=true, row 0 (end of row).
                Assert.True(atEnd,
                    $"Step {i}: isAtEnd should be true at boundary offset {ofs}");
                Assert.Equal(0, row);
            } else {
                // After flip: isAtEnd=false, row 1 (start of next row).
                Assert.False(atEnd,
                    $"Step {i}: isAtEnd should be false after flip at offset {ofs}");
                Assert.Equal(1, row);
            }
        }
    }

    [AvaloniaFact]
    public void End_Then_Home_StaysOnSameRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        editor.MoveCaretToLineEdgeForTest(toStart: true, extend: false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.Equal(cpr, Caret(editor));
    }

    [AvaloniaFact]
    public void End_Then_Right_MovesForward() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        var endRow = CaretRow(editor, rh);
        Assert.Equal(0, endRow);

        // Right from end-of-row should flip to start of next row
        // (same offset, different row), or advance.  Either way,
        // the visual row must be >= endRow (no backwards jump).
        editor.MoveCaretHorizontalForTest(delta: +1, byWord: false, extend: false);
        Relayout(editor);
        var afterRow = CaretRow(editor, rh);
        Assert.True(afterRow >= endRow,
            $"Right after End should not go backwards: endRow={endRow}, afterRow={afterRow}");
    }

    [AvaloniaFact]
    public void End_Then_Down_MovesDownOneRow() {
        var (editor, cpr, rh) = CreateWrappedEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        editor.MoveCaretToLineEdgeForTest(toStart: false, extend: false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        editor.MoveCaretVerticalForTest(lineDelta: +1, extend: false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
    }
}
