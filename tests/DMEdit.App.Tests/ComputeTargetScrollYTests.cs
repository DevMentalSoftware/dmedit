using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

/// <summary>
/// Pure-function tests for <see cref="EditorControl.ComputeTargetScrollY"/>.
/// No editor, no Avalonia headless — just arithmetic.  Each policy is
/// tested against a small set of boundary and interior scenarios.
///
/// Proving this function correct means callers only need to prove they
/// feed the right inputs, not that the scroll math itself works.
/// </summary>
public class ComputeTargetScrollYTests {
    // Shared constants — keep the numbers simple for readability.
    private const double Rh = 20;       // row height
    private const double VpH = 200;     // viewport height (10 rows)
    private const double DocH = 2000;   // document height (100 rows)

    // ================================================================
    //  Top policy: always align caret's top edge with viewport top.
    // ================================================================

    [Fact]
    public void Top_CaretMidDoc_ReturnsCaretDocY() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY: 500, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(500, result);
    }

    [Fact]
    public void Top_CaretAtDocStart_ReturnsZero() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY: 0, caretH: Rh, VpH, currentScrollY: 300);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Top_CaretNearDocEnd_ReturnsCaretDocY() {
        // Caller is responsible for clamping; Top just returns caretDocY.
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY: DocH - Rh, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(DocH - Rh, result);
    }

    // ================================================================
    //  Bottom policy: align caret's bottom edge with viewport bottom.
    // ================================================================

    [Fact]
    public void Bottom_CaretMidDoc_AlignsCareBottomWithViewportBottom() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY: 500, caretH: Rh, VpH, currentScrollY: 0);
        // caret bottom at 520, viewport bottom at scroll + 200 → scroll = 320.
        Assert.Equal(500 + Rh - VpH, result);
    }

    [Fact]
    public void Bottom_CaretAtDocStart_ReturnsNegative() {
        // Caller clamps; Bottom formula can go negative.
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY: 0, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(Rh - VpH, result);
    }

    [Fact]
    public void Bottom_CaretAtDocEnd_AlignsProperly() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY: DocH - Rh, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(DocH - VpH, result);
    }

    // ================================================================
    //  Center policy: center caret vertically in viewport.
    // ================================================================

    [Fact]
    public void Center_CaretMidDoc_CentersCaretInViewport() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY: 500, caretH: Rh, VpH, currentScrollY: 0);
        // caretMidY = 510, scroll = 510 - 100 = 410.
        Assert.Equal(500 + Rh / 2 - VpH / 2, result);
    }

    [Fact]
    public void Center_CaretAtDocStart_ReturnsNegative() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY: 0, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(Rh / 2 - VpH / 2, result);
    }

    [Fact]
    public void Center_TallCaret_UsesCaretMidpoint() {
        // Multi-row selection: caret height = 60 (3 rows).
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY: 400, caretH: 60, VpH, currentScrollY: 0);
        Assert.Equal(400 + 30 - 100, result);
    }

    // ================================================================
    //  Minimal policy: no-op if visible, smallest scroll otherwise.
    // ================================================================

    [Fact]
    public void Minimal_CaretAlreadyVisible_NoChange() {
        // Caret at docY=100 (row 5), viewport showing rows 0-9 (scroll=0).
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 100, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(0, result); // no scroll change
    }

    [Fact]
    public void Minimal_CaretJustAboveViewport_ScrollsUpMinimally() {
        // Viewport at scroll=200 (rows 10-19), caret at docY=180 (row 9).
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 180, caretH: Rh, VpH, currentScrollY: 200);
        Assert.Equal(180, result); // scroll up to show caret at top
    }

    [Fact]
    public void Minimal_CaretJustBelowViewport_ScrollsDownMinimally() {
        // Viewport at scroll=0 (rows 0-9), caret at docY=210 (row 10.5).
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 210, caretH: Rh, VpH, currentScrollY: 0);
        // Need caret bottom (230) at viewport bottom → scroll = 30.
        Assert.Equal(210 + Rh - VpH, result);
    }

    [Fact]
    public void Minimal_CaretAtExactTopEdge_NoChange() {
        // Caret top == viewport top → visible, no scroll.
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 200, caretH: Rh, VpH, currentScrollY: 200);
        Assert.Equal(200, result);
    }

    [Fact]
    public void Minimal_CaretAtExactBottomEdge_NoChange() {
        // Caret bottom == viewport bottom → visible, no scroll.
        // caretDocY + caretH = currentScrollY + vpH → 180 + 20 = 0 + 200.
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 180, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Minimal_CaretFarAbove_ScrollsUpToCaretDocY() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 0, caretH: Rh, VpH, currentScrollY: 1800);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Minimal_CaretFarBelow_ScrollsDownMinimally() {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY: 1900, caretH: Rh, VpH, currentScrollY: 0);
        Assert.Equal(1900 + Rh - VpH, result);
    }

    // ================================================================
    //  Edge cases shared across policies.
    // ================================================================

    [Fact]
    public void ZeroViewportHeight_DoesNotDivideByZero() {
        // Edge case: viewport height = 0 (e.g. collapsed window).
        // Should not throw; result doesn't matter much.
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY: 100, caretH: Rh, viewportH: 0, currentScrollY: 0);
        Assert.True(double.IsFinite(result));
    }

    [Fact]
    public void ZeroCaretHeight_TreatedAsPoint() {
        // caretH = 0 — collapsed caret (shouldn't happen, but don't crash).
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY: 100, caretH: 0, VpH, currentScrollY: 0);
        Assert.Equal(100, result);
    }

    [Theory]
    [InlineData(ScrollPolicy.Top)]
    [InlineData(ScrollPolicy.Bottom)]
    [InlineData(ScrollPolicy.Center)]
    [InlineData(ScrollPolicy.Minimal)]
    public void AllPolicies_NegativeCaretDocY_DoesNotThrow(ScrollPolicy policy) {
        // Negative caretDocY shouldn't happen but must not crash.
        var result = EditorControl.ComputeTargetScrollY(
            policy, caretDocY: -100, caretH: Rh, VpH, currentScrollY: 0);
        Assert.True(double.IsFinite(result));
    }

    // ================================================================
    //  Symmetry: Top and Bottom are inverse at doc boundaries.
    // ================================================================

    [Fact]
    public void TopAndBottom_DocEndCaret_DifferByVpH() {
        var caretDocY = DocH - Rh;
        var top = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY, Rh, VpH, 0);
        var bottom = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY, Rh, VpH, 0);
        // Top = caretDocY, Bottom = caretDocY + Rh - VpH.
        // Difference = Rh - VpH (negative because viewport > 1 row).
        Assert.Equal(VpH - Rh, top - bottom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1980)]
    public void Center_AlwaysBetweenTopAndBottom(double caretDocY) {
        var top = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY, Rh, VpH, 0);
        var center = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY, Rh, VpH, 0);
        var bottom = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY, Rh, VpH, 0);
        Assert.InRange(center, bottom, top);
    }

    // ================================================================
    //  Parametric sweep: Minimal policy boundary precision
    //
    //  The Minimal policy has three branches: above viewport, below
    //  viewport, already visible.  This sweep tests the exact
    //  transition points between branches.
    // ================================================================

    public static IEnumerable<object[]> MinimalSweepData() {
        // Sweep caretDocY from 0 to 2*VpH in steps of Rh/2,
        // with currentScrollY at various positions.
        var scrollPositions = new double[] { 0, 50, 100, 300, 500, 900, 1200, 1800 };
        foreach (var scrollY in scrollPositions) {
            for (var caretDocY = 0.0; caretDocY <= 2 * VpH; caretDocY += Rh / 2) {
                yield return new object[] { caretDocY, scrollY };
            }
        }
    }

    [Theory]
    [MemberData(nameof(MinimalSweepData))]
    public void Minimal_Sweep_ResultIsConsistent(double caretDocY, double scrollY) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY, Rh, VpH, scrollY);

        // After applying the result, the caret should be visible:
        //   result <= caretDocY  (caret not above viewport)
        //   caretDocY + Rh <= result + VpH  (caret not below viewport)
        // OR the result equals scrollY (no scroll needed because already visible).
        if (Math.Abs(result - scrollY) < 0.01) {
            // No scroll — caret was already visible.
            Assert.True(caretDocY >= scrollY - 0.01,
                $"No-scroll but caret ({caretDocY}) above viewport ({scrollY})");
            Assert.True(caretDocY + Rh <= scrollY + VpH + 0.01,
                $"No-scroll but caret bottom ({caretDocY + Rh}) below viewport bottom ({scrollY + VpH})");
        } else {
            // Scrolled — caret should be inside the new viewport.
            Assert.True(caretDocY >= result - 0.01,
                $"Caret ({caretDocY}) above viewport after scroll ({result})");
            Assert.True(caretDocY + Rh <= result + VpH + 0.01,
                $"Caret bottom ({caretDocY + Rh}) below viewport bottom ({result + VpH})");
        }
    }

    [Theory]
    [MemberData(nameof(MinimalSweepData))]
    public void Minimal_Sweep_NeverScrollsMoreThanNeeded(double caretDocY,
            double scrollY) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY, Rh, VpH, scrollY);

        // The scroll delta should be the MINIMUM needed.
        // If caret was above: result = caretDocY (pull viewport up to caret).
        // If caret was below: result = caretDocY + Rh - VpH (pull viewport
        //   down so caret bottom aligns with viewport bottom).
        // If already visible: result = scrollY (no change).
        var delta = Math.Abs(result - scrollY);
        if (delta < 0.01) return; // no scroll

        if (caretDocY < scrollY) {
            // Caret was above — should scroll up to caret (no further).
            Assert.Equal(caretDocY, result, 1e-6);
        } else {
            // Caret was below — should scroll down minimally.
            Assert.Equal(caretDocY + Rh - VpH, result, 1e-6);
        }
    }

    // ================================================================
    //  Parametric sweep: all policies, varying viewport sizes
    // ================================================================

    public static IEnumerable<object[]> ViewportSizeData() {
        var viewports = new double[] { 12, 20, 60, 100, 200, 400, 800 };
        var positions = new double[] { 0, 10, 50, 100, 200, 500, 990 };
        foreach (var vpH in viewports) {
            foreach (var caretDocY in positions) {
                yield return new object[] { caretDocY, vpH };
            }
        }
    }

    // ================================================================
    //  Multi-row caret height sweep
    // ================================================================

    public static IEnumerable<object[]> CaretHeightData() {
        var caretHeights = new double[] { Rh, Rh * 2, Rh * 3, Rh * 5 };
        var positions = new double[] { 0, 100, 500, 1000, 1980 };
        foreach (var ch in caretHeights) {
            foreach (var pos in positions) {
                yield return new object[] { pos, ch };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CaretHeightData))]
    public void Minimal_TallCaret_ResultConsistent(double caretDocY, double caretH) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Minimal, caretDocY, caretH, VpH, 0);
        // After scroll, the caret should fit in the viewport.
        if (Math.Abs(result) < 0.01) {
            Assert.True(caretDocY >= -0.01);
            Assert.True(caretDocY + caretH <= VpH + 0.01);
        } else {
            Assert.True(caretDocY >= result - 0.01);
            Assert.True(caretDocY + caretH <= result + VpH + 0.01);
        }
    }

    [Theory]
    [MemberData(nameof(CaretHeightData))]
    public void Center_TallCaret_CentersOnMidpoint(double caretDocY, double caretH) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY, caretH, VpH, 0);
        var caretMid = caretDocY + caretH / 2;
        var vpMid = result + VpH / 2;
        Assert.Equal(caretMid, vpMid, 1e-6);
    }

    [Theory]
    [MemberData(nameof(CaretHeightData))]
    public void Top_TallCaret_TopAligned(double caretDocY, double caretH) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY, caretH, VpH, 0);
        Assert.Equal(caretDocY, result, 1e-6);
    }

    [Theory]
    [MemberData(nameof(CaretHeightData))]
    public void Bottom_TallCaret_BottomAligned(double caretDocY, double caretH) {
        var result = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY, caretH, VpH, 0);
        Assert.Equal(caretDocY + caretH - VpH, result, 1e-6);
    }

    [Theory]
    [MemberData(nameof(ViewportSizeData))]
    public void AllPolicies_VaryingViewport_TopMinusCenterEqualsHalfVp(
            double caretDocY, double vpH) {
        var top = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Top, caretDocY, Rh, vpH, 0);
        var center = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY, Rh, vpH, 0);
        // Top - Center = vpH/2 - Rh/2 (half viewport minus half caret).
        Assert.Equal(vpH / 2 - Rh / 2, top - center, 1e-6);
    }

    [Theory]
    [MemberData(nameof(ViewportSizeData))]
    public void AllPolicies_VaryingViewport_BottomMinusCenterEqualsNegHalfVp(
            double caretDocY, double vpH) {
        var bottom = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Bottom, caretDocY, Rh, vpH, 0);
        var center = EditorControl.ComputeTargetScrollY(
            ScrollPolicy.Center, caretDocY, Rh, vpH, 0);
        Assert.Equal(-(vpH / 2 - Rh / 2), bottom - center, 1e-6);
    }
}
