using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.Core.Documents;
using DMEdit.Rendering.Layout;

namespace DMEdit.Rendering.Tests;

/// <summary>
/// Tests for the <see cref="MonoLineLayout.OffsetToPos"/> /
/// <see cref="MonoLineLayout.PosToOffset"/> converters that translate
/// between character offsets and the unambiguous (row, col) form used by
/// <see cref="TextPosition"/>.
///
/// <para>Key invariants:</para>
/// <list type="bullet">
///   <item><c>OffsetToPos</c> prefers the upstream position at wrap
///     boundaries (end of previous row).</item>
///   <item><c>PosToOffset(OffsetToPos(x)) == x</c> for every offset
///     <c>x</c> that is not at a wrap boundary.</item>
///   <item>At a wrap boundary, <c>OffsetToPos(x)</c> returns the end of
///     the previous row; <c>PosToOffset</c> of the downstream position
///     (start of next row, col=0) returns the same offset <c>x</c>.</item>
/// </list>
/// </summary>
public class MonoLineLayoutPositionTests {
    private static MonoLineLayout BuildLine(string text, int charsPerRow) {
        var typeface = new Typeface(new FontFamily("Courier New"));
        var gtf = typeface.GlyphTypeface;
        if (gtf == null || !MonoLayoutContext.IsMonospace(gtf)) {
            throw new Xunit.Sdk.XunitException(
                "Courier New did not resolve to a monospace glyph typeface in headless mode.");
        }
        var ctx = new MonoLayoutContext(gtf, 14.0, 16.0, hangingIndentChars: 0,
            Brushes.Black);
        var layout = MonoLineLayout.TryBuild(ctx, text, charsPerRow);
        Assert.NotNull(layout);
        return layout!;
    }

    // -- Hard-break (no spaces) --

    [AvaloniaFact]
    public void OffsetToPos_MidRow_ReturnsThatRow() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal((0, 5), ml.OffsetToPos(5));
        Assert.Equal((1, 3), ml.OffsetToPos(13));
        Assert.Equal((2, 7), ml.OffsetToPos(27));
    }

    [AvaloniaFact]
    public void OffsetToPos_BoundaryOffset_ReturnsEndOfPreviousRow() {
        using var ml = BuildLine(new string('a', 30), 10);
        // Offset 10 is shared between "end of row 0" and "start of row 1".
        // Upstream default: end of row 0.
        Assert.Equal((0, 10), ml.OffsetToPos(10));
        Assert.Equal((1, 10), ml.OffsetToPos(20));
    }

    [AvaloniaFact]
    public void OffsetToPos_FirstRowStart_NoUpstreamRuleApplies() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal((0, 0), ml.OffsetToPos(0));
    }

    [AvaloniaFact]
    public void OffsetToPos_EndOfLine_ReturnsEndOfLastRow() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal((2, 10), ml.OffsetToPos(30));
    }

    // -- Space-break (wrap after a space) --

    [AvaloniaFact]
    public void OffsetToPos_SpaceBreak_SpaceIsWalkableOnPreviousRow() {
        // "hello world" at charsPerRow=6:
        //   row 0: CharStart=0, CharLen=6 → "hello " (space drawn on row 0)
        //   row 1: CharStart=6, CharLen=5 → "world"
        using var ml = BuildLine("hello world", 6);
        Assert.Equal(2, ml.RowCount);
        Assert.Equal(0, ml.Rows[0].CharStart);
        Assert.Equal(6, ml.Rows[0].CharLen);
        Assert.Equal(6, ml.Rows[1].CharStart);
        Assert.Equal(5, ml.Rows[1].CharLen);

        // Offset 5 is BEFORE the space (mid row 0).
        Assert.Equal((0, 5), ml.OffsetToPos(5));
        // Offset 6 is the boundary: end-of-row-0 (past the space) wins.
        Assert.Equal((0, 6), ml.OffsetToPos(6));
        // Offset 7 is mid row 1 (before 'o' of "world").
        Assert.Equal((1, 1), ml.OffsetToPos(7));
    }

    // -- Clamping / out-of-range --

    [AvaloniaFact]
    public void OffsetToPos_NegativeOffset_ClampsToStart() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal((0, 0), ml.OffsetToPos(-5));
    }

    [AvaloniaFact]
    public void OffsetToPos_OffsetPastEnd_ClampsToEnd() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal((2, 10), ml.OffsetToPos(99));
    }

    [AvaloniaFact]
    public void OffsetToPos_EmptyLine_ReturnsZeroZero() {
        using var ml = BuildLine("", 10);
        Assert.Equal((0, 0), ml.OffsetToPos(0));
        Assert.Equal((0, 0), ml.OffsetToPos(5));
    }

    [AvaloniaFact]
    public void OffsetToPos_ShortSingleRowLine_MidAndEnd() {
        using var ml = BuildLine("abc", 10);
        Assert.Equal(1, ml.RowCount);
        Assert.Equal((0, 0), ml.OffsetToPos(0));
        Assert.Equal((0, 2), ml.OffsetToPos(2));
        Assert.Equal((0, 3), ml.OffsetToPos(3));
    }

    // -- PosToOffset --

    [AvaloniaFact]
    public void PosToOffset_MidRow_ReturnsCharStartPlusCol() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal(5, ml.PosToOffset(0, 5));
        Assert.Equal(13, ml.PosToOffset(1, 3));
        Assert.Equal(27, ml.PosToOffset(2, 7));
    }

    [AvaloniaFact]
    public void PosToOffset_EndOfRow_ReturnsBoundaryOffset() {
        using var ml = BuildLine(new string('a', 30), 10);
        // End of row 0 == start of row 1 in offset space.
        Assert.Equal(10, ml.PosToOffset(0, 10));
        Assert.Equal(10, ml.PosToOffset(1, 0));
    }

    [AvaloniaFact]
    public void PosToOffset_StartOfRow1_SameOffsetAsEndOfRow0() {
        // Confirms that the two distinct TextPositions at a boundary share
        // one CharOffset — that's the core of the new model.
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal(ml.PosToOffset(0, 10), ml.PosToOffset(1, 0));
        Assert.Equal(ml.PosToOffset(1, 10), ml.PosToOffset(2, 0));
    }

    [AvaloniaFact]
    public void PosToOffset_ClampsNegativeCol() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal(10, ml.PosToOffset(1, -5));
    }

    [AvaloniaFact]
    public void PosToOffset_ClampsColPastRowEnd() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal(20, ml.PosToOffset(1, 99));
    }

    [AvaloniaFact]
    public void PosToOffset_ClampsRowInLine() {
        using var ml = BuildLine(new string('a', 30), 10);
        Assert.Equal(0, ml.PosToOffset(-1, 0));
        Assert.Equal(20, ml.PosToOffset(99, 0));
    }

    // -- Round-trip invariants --

    [AvaloniaFact]
    public void RoundTrip_NonBoundaryOffset_Preserved() {
        using var ml = BuildLine(new string('a', 30), 10);
        for (var ofs = 0; ofs <= 30; ofs++) {
            // At boundary offsets (10, 20) we land on end-of-previous-row,
            // which still shares the same offset — so round-trip is still
            // preserved by construction (rows are contiguous).
            var (row, col) = ml.OffsetToPos(ofs);
            Assert.Equal(ofs, ml.PosToOffset(row, col));
        }
    }

    [AvaloniaFact]
    public void RoundTrip_SpaceBreakBoundary_Preserved() {
        using var ml = BuildLine("hello world", 6);
        for (var ofs = 0; ofs <= 11; ofs++) {
            var (row, col) = ml.OffsetToPos(ofs);
            Assert.Equal(ofs, ml.PosToOffset(row, col));
        }
    }
}
