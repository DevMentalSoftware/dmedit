using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DMEdit.Rendering.Layout;

namespace DMEdit.Rendering.Tests;

/// <summary>
/// The monospace fast path must put the baseline where TextLayout does —
/// the row height comes from TextLayout, so any disagreement draws text
/// off-centre in its row relative to the caret (GH #19: Consolas, whose
/// non-zero line gap was left out of the fast-path baseline).
/// </summary>
public class MonoBaselineTests {
    [AvaloniaTheory]
    [InlineData("Consolas")]
    [InlineData("Courier New")]
    [InlineData("Cascadia Code")]
    public void MonoBaseline_MatchesTextLayoutBaseline(string family) {
        const double fontSize = 14.0;
        var typeface = new Typeface(new FontFamily(family));
        var gtf = typeface.GlyphTypeface;
        if (gtf is null || !MonoLayoutContext.IsMonospace(gtf)) {
            return; // font not available on this machine
        }

        using var tl = new TextLayout(" ", typeface, fontSize, Brushes.Black);
        var expected = tl.TextLines[0].Baseline;

        var ctx = new MonoLayoutContext(gtf, fontSize, tl.Height,
            hangingIndentChars: 0, Brushes.Black);

        Assert.Equal(expected, ctx.Baseline, precision: 3);
    }
}
