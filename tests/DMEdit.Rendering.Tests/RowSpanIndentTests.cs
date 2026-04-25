using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.Rendering.Layout;

namespace DMEdit.Rendering.Tests;

/// <summary>
/// Tests for <see cref="RowSpan.IndentCols"/> and the relationship
/// between continuation-row indent in characters vs pixels.  Special
/// attention to ODD <c>HangingIndentChars</c> values (e.g. indent=3
/// gives hanging=1) — int division must stay consistent across every
/// site that derives an indent from the editor's indent setting.
/// </summary>
public class RowSpanIndentTests {
    private static MonoLayoutContext BuildContext(int hangingIndentChars, int tabWidth = 4) {
        var typeface = new Typeface(new FontFamily("Courier New"));
        var gtf = typeface.GlyphTypeface;
        if (gtf == null || !MonoLayoutContext.IsMonospace(gtf)) {
            throw new Xunit.Sdk.XunitException(
                "Courier New did not resolve to a monospace glyph typeface in headless mode.");
        }
        return new MonoLayoutContext(
            gtf, 14.0, 16.0, hangingIndentChars, Brushes.Black, tabWidth);
    }

    // -- IndentCols matches HangingIndentChars on continuation rows --

    [AvaloniaFact]
    public void IndentCols_FirstRow_IsZero() {
        var ctx = BuildContext(hangingIndentChars: 2);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 30), 10)!;
        Assert.True(ml.RowCount > 1);
        Assert.Equal(0, ml.Rows[0].IndentCols);
    }

    [AvaloniaFact]
    public void IndentCols_ContinuationRow_EqualsHangingIndentChars_NoLeadingWhitespace() {
        var ctx = BuildContext(hangingIndentChars: 2);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 30), 10)!;
        Assert.Equal(2, ml.Rows[1].IndentCols);
    }

    // -- Odd indent (3) — hanging = 1 col by int division --

    [AvaloniaFact]
    public void IndentCols_OddIndent3_ContinuationRow_IsOne() {
        // Indent setting of 3 → HangingIndentChars = 3/2 = 1 (int div).
        var ctx = BuildContext(hangingIndentChars: 1);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 30), 10)!;
        Assert.True(ml.RowCount > 1);
        Assert.Equal(0, ml.Rows[0].IndentCols);
        Assert.Equal(1, ml.Rows[1].IndentCols);
    }

    [AvaloniaFact]
    public void IndentCols_OddIndent5_ContinuationRow_IsTwo() {
        // Indent setting of 5 → HangingIndentChars = 5/2 = 2.
        var ctx = BuildContext(hangingIndentChars: 2);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 30), 10)!;
        Assert.Equal(2, ml.Rows[1].IndentCols);
    }

    // -- Indent disabled — every row's IndentCols is 0 --

    [AvaloniaFact]
    public void IndentCols_HangingIndentZero_AllRowsZero() {
        var ctx = BuildContext(hangingIndentChars: 0);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 30), 10)!;
        Assert.True(ml.RowCount > 1);
        for (var r = 0; r < ml.RowCount; r++) {
            Assert.Equal(0, ml.Rows[r].IndentCols);
        }
    }

    // -- Per-line leading whitespace adds to IndentCols --

    [AvaloniaFact]
    public void IndentCols_LeadingWhitespace_AddsToHangingIndent() {
        // Line starts with 4 spaces; hanging indent = 1 (odd setting).
        // Continuation rows should have IndentCols = 4 + 1 = 5.
        var ctx = BuildContext(hangingIndentChars: 1);
        var text = "    " + new string('a', 30);  // 4 leading spaces
        using var ml = MonoLineLayout.TryBuild(ctx, text, 10)!;
        Assert.True(ml.RowCount > 1);
        Assert.Equal(0, ml.Rows[0].IndentCols);
        Assert.Equal(5, ml.Rows[1].IndentCols);
    }

    // -- IndentCols is consistent across all continuation rows --

    [AvaloniaFact]
    public void IndentCols_AllContinuationRows_HaveSameIndent() {
        var ctx = BuildContext(hangingIndentChars: 1);
        using var ml = MonoLineLayout.TryBuild(ctx, new string('a', 60), 10)!;
        Assert.True(ml.RowCount >= 3);
        for (var r = 1; r < ml.RowCount; r++) {
            Assert.Equal(ml.Rows[1].IndentCols, ml.Rows[r].IndentCols);
        }
    }
}
