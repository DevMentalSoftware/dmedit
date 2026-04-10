using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.Core.Documents;
using DMEdit.Rendering.Layout;

namespace DMEdit.Rendering.Tests;

/// <summary>
/// Tests for <see cref="MonoLineLayout.GetCaretBounds"/> with the
/// <c>isAtEnd</c> parameter.  Constructs a wrapped line via
/// <see cref="MonoLineLayout.TryBuild"/> and verifies that the caret
/// renders on the correct visual row for every boundary/non-boundary
/// combination of (charOffset, affinity).
///
/// Uses a line of 'a' characters (no spaces, forcing hard breaks at
/// exactly <c>charsPerRow</c>) to make row boundaries deterministic
/// regardless of font metrics.  Hard breaks split at exact multiples:
/// row 0 = [0, cpr), row 1 = [cpr, 2*cpr), etc.
/// </summary>
public class MonoLineLayoutAffinityTests {
    private static MonoLineLayout BuildWrappedLine(int totalChars, int charsPerRow,
            out double charWidth, out double rowHeight) {
        var typeface = new Typeface(new FontFamily("Courier New"));
        var gtf = typeface.GlyphTypeface;
        if (gtf == null || !MonoLayoutContext.IsMonospace(gtf))
            throw new Xunit.Sdk.XunitException(
                "Courier New did not resolve to a monospace glyph typeface in headless mode.");

        var ctx = new MonoLayoutContext(gtf, 14.0, 16.0, hangingIndentChars: 0,
            Brushes.Black);
        charWidth = ctx.CharWidth;
        rowHeight = ctx.RowHeight;

        // Build a line of 'a' chars — no spaces, so NextRow hard-breaks
        // at exactly charsPerRow.
        var text = new string('a', totalChars);
        var layout = MonoLineLayout.TryBuild(ctx, text, charsPerRow);
        Assert.NotNull(layout);
        return layout!;
    }

    // -- Verify setup: row boundaries are where we expect --

    [AvaloniaFact]
    public void Setup_ThreeRows_CorrectSpans() {
        using var ml = BuildWrappedLine(30, 10, out _, out _);
        Assert.Equal(3, ml.RowCount);
        Assert.Equal(0, ml.Rows[0].CharStart);
        Assert.Equal(10, ml.Rows[0].CharLen);
        Assert.Equal(10, ml.Rows[1].CharStart);
        Assert.Equal(10, ml.Rows[1].CharLen);
        Assert.Equal(20, ml.Rows[2].CharStart);
        Assert.Equal(10, ml.Rows[2].CharLen);
    }

    // ----------------------------------------------------------------
    //  Mid-row positions: affinity is irrelevant
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void MidRow_RightAffinity_RendersOnSameRow() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(5, isAtEnd: false);
        Assert.Equal(5 * cw, rect.X, 0.1);
        Assert.Equal(0, rect.Y, 0.1); // row 0
    }

    [AvaloniaFact]
    public void MidRow_LeftAffinity_RendersOnSameRow() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(5, isAtEnd: true);
        Assert.Equal(5 * cw, rect.X, 0.1);
        Assert.Equal(0, rect.Y, 0.1); // row 0 — affinity doesn't affect mid-row
    }

    [AvaloniaFact]
    public void MidRow1_RightAffinity_RendersOnRow1() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(15, isAtEnd: false);
        Assert.Equal(5 * cw, rect.X, 0.1);
        Assert.Equal(rh, rect.Y, 0.1); // row 1
    }

    // ----------------------------------------------------------------
    //  Row boundary: position = row start (e.g., char 10, 20)
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void RowBoundary_RightAffinity_RendersOnNextRow() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        // Char 10 is the first char of row 1.
        var rect = ml.GetCaretBounds(10, isAtEnd: false);
        Assert.Equal(0.0, rect.X, 0.1);
        Assert.Equal(rh, rect.Y, 0.1); // row 1
    }

    [AvaloniaFact]
    public void RowBoundary_LeftAffinity_RendersOnPreviousRow() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        // Char 10 with left affinity → end of row 0.
        var rect = ml.GetCaretBounds(10, isAtEnd: true);
        Assert.Equal(10 * cw, rect.X, 0.1);
        Assert.Equal(0, rect.Y, 0.1); // row 0
    }

    [AvaloniaFact]
    public void SecondRowBoundary_RightAffinity_RendersOnRow2() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(20, isAtEnd: false);
        Assert.Equal(0.0, rect.X, 0.1);
        Assert.Equal(2 * rh, rect.Y, 0.1); // row 2
    }

    [AvaloniaFact]
    public void SecondRowBoundary_LeftAffinity_RendersOnRow1() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(20, isAtEnd: true);
        Assert.Equal(10 * cw, rect.X, 0.1);
        Assert.Equal(rh, rect.Y, 0.1); // row 1
    }

    // ----------------------------------------------------------------
    //  Row 0 start: left affinity has no previous row → stays at row 0
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void Row0Start_LeftAffinity_StaysOnRow0() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(0, isAtEnd: true);
        Assert.Equal(0.0, rect.X, 0.1);
        Assert.Equal(0.0, rect.Y, 0.1); // row 0, no previous row
    }

    // ----------------------------------------------------------------
    //  End of content: not a soft break, affinity irrelevant
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void EndOfContent_RightAffinity_RendersAtEnd() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rect = ml.GetCaretBounds(30, isAtEnd: false);
        Assert.Equal(10 * cw, rect.X, 0.1);
        Assert.Equal(2 * rh, rect.Y, 0.1); // end of row 2
    }

    [AvaloniaFact]
    public void EndOfContent_LeftAffinity_SameAsRight() {
        using var ml = BuildWrappedLine(30, 10, out var cw, out var rh);
        var rectR = ml.GetCaretBounds(30, isAtEnd: false);
        var rectL = ml.GetCaretBounds(30, isAtEnd: true);
        // Not at a soft break → same position regardless of affinity.
        Assert.Equal(rectR.X, rectL.X, 0.1);
        Assert.Equal(rectR.Y, rectL.Y, 0.1);
    }

    // ----------------------------------------------------------------
    //  Single row (no wrapping): affinity is always irrelevant
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void SingleRow_LeftAffinity_NoEffect() {
        using var ml = BuildWrappedLine(5, 10, out var cw, out var rh);
        Assert.Equal(1, ml.RowCount);
        var rectR = ml.GetCaretBounds(3, isAtEnd: false);
        var rectL = ml.GetCaretBounds(3, isAtEnd: true);
        Assert.Equal(rectR.X, rectL.X, 0.1);
        Assert.Equal(rectR.Y, rectL.Y, 0.1);
    }

    // ----------------------------------------------------------------
    //  Word-wrap boundaries (space consumed) — test with spaces
    // ----------------------------------------------------------------

    [AvaloniaFact]
    public void WordWrap_SpaceIncludedInRow0() {
        // "aaaaa bbbbb ccccc" with cpr=6:
        // Row 0: CharStart=0, CharLen=6 ("aaaaa" + space)
        // Row 1: CharStart=6, CharLen=6 ("bbbbb" + space)
        // Row 2: CharStart=12, CharLen=5 ("ccccc")
        var typeface = new Typeface(new FontFamily("Courier New"));
        var gtf = typeface.GlyphTypeface;
        if (gtf == null) throw new Xunit.Sdk.XunitException("No glyph typeface");
        var ctx = new MonoLayoutContext(gtf, 14.0, 16.0, 0, Brushes.Black);
        var cw = ctx.CharWidth;
        var rh = ctx.RowHeight;

        using var ml = MonoLineLayout.TryBuild(ctx, "aaaaa bbbbb ccccc", 6);
        Assert.NotNull(ml);

        // Verify row structure — space is part of row 0's CharLen.
        Assert.Equal(3, ml!.RowCount);
        Assert.Equal(0, ml.Rows[0].CharStart);
        Assert.Equal(6, ml.Rows[0].CharLen);    // "aaaaa" + space
        Assert.Equal(6, ml.Rows[1].CharStart);  // no gap
        Assert.Equal(6, ml.Rows[1].CharLen);    // "bbbbb" + space
        Assert.Equal(12, ml.Rows[2].CharStart);
        Assert.Equal(5, ml.Rows[2].CharLen);    // "ccccc"

        // Char 5 (the space) is on row 0 — it's a normal character.
        var rectSpace = ml.GetCaretBounds(5, isAtEnd: false);
        Assert.Equal(5 * cw, rectSpace.X, 0.1);
        Assert.Equal(0, rectSpace.Y, 0.1); // row 0

        // Char 6 (first char of row 1) with isAtEnd=true → end of row 0.
        var rect = ml.GetCaretBounds(6, isAtEnd: true);
        Assert.Equal(6 * cw, rect.X, 0.1);
        Assert.Equal(0, rect.Y, 0.1); // row 0

        // Char 6 with isAtEnd=false → start of row 1.
        var rectR = ml.GetCaretBounds(6, isAtEnd: false);
        Assert.Equal(0, rectR.X, 0.1);
        Assert.Equal(rh, rectR.Y, 0.1); // row 1
    }
}
