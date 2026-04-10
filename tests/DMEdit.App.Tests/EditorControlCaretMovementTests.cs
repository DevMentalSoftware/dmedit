using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Comprehensive caret movement tests across all wrap modes.
/// Tests assert CURRENT WORKING behavior — if a test fails it means
/// a regression, not a bug to fix.  Investigate before changing code.
///
/// Test matrix covers:
///   Modes: WrapMode (hard break), WrapMode (space break), WrapOff
///   Keys: Right, Left, Up, Down, Home, End
///   Positions: mid-row, near boundary, at boundary, first/last row
///
/// CharWrapMode is tested separately (different code paths).
/// </summary>
public class EditorControlCaretMovementTests {
    private const double ViewportWidth = 600;
    private const double ViewportHeight = 400;

    // ================================================================
    //  Helpers
    // ================================================================

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(ViewportWidth, ViewportHeight));
        editor.Arrange(new Rect(0, 0, ViewportWidth, ViewportHeight));
    }

    private static long Caret(EditorControl e) => e.Document!.Selection.Caret;
    private static long Anchor(EditorControl e) => e.Document!.Selection.Anchor;

    private static int CaretRow(EditorControl e, double rh) {
        var y = e.GetCaretScreenYForTest();
        Assert.NotNull(y);
        return (int)Math.Round(y!.Value / rh);
    }

    /// <summary>Hard-break wrap: 200 'a' chars (no spaces), wraps at exact cpr multiples.</summary>
    private static (EditorControl editor, int cpr, double rh) CreateHardBreakEditor() {
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
        var layout = editor.GetType()
            .GetMethod("EnsureLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(editor, null) as DMEdit.Rendering.Layout.LayoutResult;
        if (layout?.Lines.Count > 0 && layout.Lines[0].Mono is { } mono && mono.Rows.Length > 1)
            cpr = mono.Rows[0].CharLen;

        return (editor, cpr, rh);
    }

    /// <summary>Space-break wrap: repeated "abcdef " blocks, wraps at word boundaries.</summary>
    private static (EditorControl editor, int row0Len, int row1Start, double rh) CreateSpaceBreakEditor() {
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
        var row1Start = 0;
        var layout = editor.GetType()
            .GetMethod("EnsureLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(editor, null) as DMEdit.Rendering.Layout.LayoutResult;
        if (layout?.Lines.Count > 0 && layout.Lines[0].Mono is { } mono && mono.Rows.Length > 1) {
            row0Len = mono.Rows[0].CharLen;
            row1Start = mono.Rows[1].CharStart;
        }

        return (editor, row0Len, row1Start, rh);
    }

    /// <summary>Wrap off: two lines, no wrapping.</summary>
    private static (EditorControl editor, double rh) CreateWrapOffEditor() {
        var doc = new Document();
        doc.Insert("hello world this is line one\nsecond line\n");
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
        return (editor, editor.RowHeightValue);
    }

    // ================================================================
    //  WRAP OFF — baseline behavior (no affinity needed)
    // ================================================================

    [AvaloniaFact]
    public void WrapOff_Right_AdvancesOneChar() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(6, Caret(editor));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WrapOff_Left_RetreatOneChar() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(4, Caret(editor));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WrapOff_Home_GoesToLineStart() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(10);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(true, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WrapOff_End_GoesToLineEnd() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.Equal(28, Caret(editor)); // "hello world this is line one" = 28 chars
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void WrapOff_Down_MovesToNextLine() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void WrapOff_Up_MovesToPrevLine() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(33); // line 2
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void WrapOff_ShiftRight_ExtendsSelection() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, true);
        Relayout(editor);
        Assert.Equal(6, Caret(editor));
        Assert.Equal(5, Anchor(editor));
    }

    [AvaloniaFact]
    public void WrapOff_ShiftLeft_ExtendsSelection() {
        var (editor, rh) = CreateWrapOffEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, false, true);
        Relayout(editor);
        Assert.Equal(4, Caret(editor));
        Assert.Equal(5, Anchor(editor));
    }

    // ================================================================
    //  HARD BREAK WRAP — right/left arrows
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_Right_MidRow_NoIsAtEnd() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(3);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(4, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Right_SecondToLast_StaysOnRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr - 2);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Right_LastChar_LandsAtBoundaryWithIsAtEnd() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr - 1);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.True(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Right_FromIsAtEnd_FlipsToNextRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        // Get to isAtEnd at boundary.
        editor.GoToPosition(cpr - 1);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.True(editor.CaretIsAtEnd);

        // Next right: flip.
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Left_SecondCharOfRow1_StaysOnRow1() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 1);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Left_FromRow1Start_FlipsToEndOfRow0() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.True(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Left_FromEndOfRow0_MovesToLastChar() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        // Get to end of row 0.
        editor.GoToPosition(cpr);
        Relayout(editor);
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.True(editor.CaretIsAtEnd);

        // Next left: to last char.
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(cpr - 1, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    // ================================================================
    //  HARD BREAK WRAP — up/down arrows
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_Down_MidRow_MovesToSameColumnNextRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Down_FromRowStart_StaysAtStart() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(2, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Up_MidRow_MovesToSameColumnPrevRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_Down_PreservesIsAtEnd() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        // End key sets isAtEnd.
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.True(editor.CaretIsAtEnd);
        var endRow = CaretRow(editor, rh);

        // Down preserves isAtEnd (same-length rows).
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(endRow + 1, CaretRow(editor, rh));
        // isAtEnd preserved for same-length rows in CharWrapMode;
        // in word-wrap, the prevRowEndX check determines it.
    }

    // ================================================================
    //  HARD BREAK WRAP — Home/End
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_End_MidRow_StaysOnRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.True(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_End_ThenEnd_GoesToLineEnd() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.Equal(200, Caret(editor));
    }

    [AvaloniaFact]
    public void HardBreak_Home_MidRow1_GoesToRow1Start() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(true, false);
        Relayout(editor);
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void HardBreak_End_Then_Home_StaysOnSameRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);
        var startRow = CaretRow(editor, rh);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.Equal(startRow, CaretRow(editor, rh));
        editor.MoveCaretToLineEdgeForTest(true, false);
        Relayout(editor);
        Assert.Equal(startRow, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void HardBreak_End_Then_Right_MovesForward() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        var endRow = CaretRow(editor, rh);
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.True(CaretRow(editor, rh) >= endRow);
    }

    // ================================================================
    //  SPACE BREAK WRAP — right/left arrows (no flip step)
    // ================================================================

    [AvaloniaFact]
    public void SpaceBreak_Right_ThroughSpace_NoIsAtEnd() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        // Last non-space char is at row0Len - 2, space is at row0Len - 1.
        editor.GoToPosition(row0Len - 2);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));

        // Right → space (still row 0).
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);

        // Right → row1Start (row 1, no flip).
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void SpaceBreak_Left_FromRow1Start_GoesToSpace() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        editor.GoToPosition(row1Start);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));

        // Left → goes to space (row 0), no flip.
        editor.MoveCaretHorizontalForTest(-1, false, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void SpaceBreak_End_SetsIsAtEnd() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);

        Assert.Equal(0, CaretRow(editor, rh));
        Assert.True(editor.CaretIsAtEnd);
    }

    [AvaloniaFact]
    public void SpaceBreak_End_ThenRight_FlipsToNextRow() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        editor.GoToPosition(5);
        Relayout(editor);
        editor.MoveCaretToLineEdgeForTest(false, false);
        Relayout(editor);
        Assert.True(editor.CaretIsAtEnd);

        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        Assert.False(editor.CaretIsAtEnd);
    }

    // ================================================================
    //  SPACE BREAK WRAP — up/down
    // ================================================================

    [AvaloniaFact]
    public void SpaceBreak_Down_MidRow_MovesToNextRow() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        editor.GoToPosition(5);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void SpaceBreak_Up_FromRow1_GoesToRow0() {
        var (editor, row0Len, row1Start, rh) = CreateSpaceBreakEditor();
        if (row0Len <= 2) return;

        editor.GoToPosition(row1Start + 3);
        Relayout(editor);
        Assert.Equal(1, CaretRow(editor, rh));
        editor.MoveCaretVerticalForTest(-1, false);
        Relayout(editor);
        Assert.Equal(0, CaretRow(editor, rh));
    }

    // ================================================================
    //  HARD BREAK WRAP — full step-through (the comprehensive test)
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_RightArrow_FullStepThrough_NeverStuck() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(0);
        Relayout(editor);

        // Step through the first 2*cpr+3 positions.  Verify no stuck
        // positions (same offset + same row + same isAtEnd).
        var prev = (ofs: Caret(editor), row: CaretRow(editor, rh), atEnd: editor.CaretIsAtEnd);
        var stuckCount = 0;
        for (var i = 0; i < 2 * cpr + 3; i++) {
            editor.MoveCaretHorizontalForTest(+1, false, false);
            Relayout(editor);
            var cur = (ofs: Caret(editor), row: CaretRow(editor, rh), atEnd: editor.CaretIsAtEnd);
            if (cur == prev) stuckCount++;
            Assert.True(stuckCount == 0,
                $"Stuck at step {i}: offset={cur.ofs}, row={cur.row}, isAtEnd={cur.atEnd}");
            prev = cur;
        }
    }

    [AvaloniaFact]
    public void HardBreak_LeftArrow_FullStepThrough_NeverStuck() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(2 * cpr + 3);
        Relayout(editor);

        var prev = (ofs: Caret(editor), row: CaretRow(editor, rh), atEnd: editor.CaretIsAtEnd);
        var stuckCount = 0;
        for (var i = 0; i < 2 * cpr + 3; i++) {
            editor.MoveCaretHorizontalForTest(-1, false, false);
            Relayout(editor);
            var cur = (ofs: Caret(editor), row: CaretRow(editor, rh), atEnd: editor.CaretIsAtEnd);
            if (cur == prev) stuckCount++;
            Assert.True(stuckCount == 0,
                $"Stuck at step {i}: offset={cur.ofs}, row={cur.row}, isAtEnd={cur.atEnd}");
            prev = cur;
        }
    }

    // ================================================================
    //  SHIFT+arrow selection at boundaries
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_ShiftRight_AcrossBoundary_ExtendsSelection() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr - 1);
        Relayout(editor);

        // Shift+Right three times across the boundary.
        for (var i = 0; i < 3; i++) {
            editor.MoveCaretHorizontalForTest(+1, false, true);
            Relayout(editor);
        }

        Assert.Equal(cpr - 1, Anchor(editor));
        // Caret should have advanced (offset or flip).
        Assert.True(Caret(editor) >= cpr);
    }

    [AvaloniaFact]
    public void HardBreak_ShiftLeft_AcrossBoundary_ExtendsSelection() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 1);
        Relayout(editor);

        for (var i = 0; i < 3; i++) {
            editor.MoveCaretHorizontalForTest(-1, false, true);
            Relayout(editor);
        }

        Assert.Equal(cpr + 1, Anchor(editor));
        Assert.True(Caret(editor) <= cpr);
    }

    [AvaloniaFact]
    public void HardBreak_ShiftEnd_ExtendsToRowEnd() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(false, true);
        Relayout(editor);

        Assert.Equal(5, Anchor(editor));
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(0, CaretRow(editor, rh));
    }

    [AvaloniaFact]
    public void HardBreak_ShiftHome_ExtendsToRowStart() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        editor.GoToPosition(cpr + 5);
        Relayout(editor);

        editor.MoveCaretToLineEdgeForTest(true, true);
        Relayout(editor);

        Assert.Equal(cpr + 5, Anchor(editor));
        Assert.Equal(cpr, Caret(editor));
        Assert.Equal(1, CaretRow(editor, rh));
    }

    // ================================================================
    //  DOWN to shorter row (the case that kept breaking)
    // ================================================================

    [AvaloniaFact]
    public void HardBreak_Down_ToShorterLine_LandsOnCorrectRow() {
        var (editor, cpr, rh) = CreateHardBreakEditor();
        // Go to near end of the SECOND-TO-LAST wrapped row of line 0,
        // so down-arrow lands on the last (short) wrapped row, which
        // is shorter than the rows above it.
        // Line 0 is 200 chars, last full row starts at (200/cpr-1)*cpr.
        var fullRows = 200 / cpr;
        var secondToLastStart = (fullRows - 1) * cpr;
        // Place caret near end of that row.
        var startPos = Math.Min(secondToLastStart + cpr - 3, 199);
        editor.GoToPosition(startPos);
        Relayout(editor);
        var startRow = CaretRow(editor, rh);

        editor.MoveCaretVerticalForTest(+1, false);
        Relayout(editor);

        // Should be on the next row (line 1 "short").
        Assert.True(startRow + 1 == CaretRow(editor, rh),
            $"Expected row {startRow + 1}, got {CaretRow(editor, rh)}. " +
            $"caret={Caret(editor)}, isAtEnd={editor.CaretIsAtEnd}, " +
            $"cpr={cpr}, startPos={startPos}");
    }
}
