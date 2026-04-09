using Avalonia;
using Avalonia.Headless.XUnit;
using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression tests for <see cref="DualZoneScrollBar"/>'s drag state machine.
/// Focuses on the inner-thumb drag branch, which had a regression where the
/// drag anchor wasn't reset after the scroll value hit a boundary — causing
/// the thumb to ignore mouse movement until the cursor returned to the exact
/// point where it had originally overshot.  This test suite didn't exist
/// when that bug was first fixed, so it regressed; adding it now locks the
/// fix in.
/// </summary>
public class DualZoneScrollBarTests {
    /// <summary>
    /// Minimal test double for <see cref="IScrollSource"/>.  ScrollValue is
    /// mutable so the scrollbar's ScrollRequested event can "apply" updates
    /// back into the source, mirroring how <c>EditorControl.ScrollValue</c>
    /// does it in production.
    /// </summary>
    private sealed class FakeScrollSource : IScrollSource {
        public double ScrollMaximum { get; init; }
        public double ScrollValue { get; set; }
        public double ScrollViewportHeight { get; init; }
        public double ScrollExtentHeight => ScrollViewportHeight + ScrollMaximum;
        public double RowHeightValue { get; init; } = 20;
    }

    private static (DualZoneScrollBar bar, FakeScrollSource src) CreateBar(
            double maximum = 1000, double viewport = 800, double rowHeight = 20) {
        var src = new FakeScrollSource {
            ScrollMaximum = maximum,
            ScrollValue = 0,
            ScrollViewportHeight = viewport,
            RowHeightValue = rowHeight,
        };
        var bar = new DualZoneScrollBar {
            // Give the bar a concrete size so ComputeThumbGeometry produces
            // a valid availableRange (track - thumb > 0).
            Width = 16,
            Height = 800,
        };
        bar.Measure(new Size(16, 800));
        bar.Arrange(new Rect(0, 0, 16, 800));
        bar.ScrollSource = src;
        bar.ScrollRequested += v => src.ScrollValue = v;
        return (bar, src);
    }

    [AvaloniaFact]
    public void InnerThumbDrag_SimpleDownwardDrag_ScrollsProportionally() {
        var (bar, src) = CreateBar(maximum: 1000);

        // Start drag at mouse Y = 100 (inner-thumb anchor).
        bar.StartInnerDrag(100);
        // Drag down by 100 px.
        bar.HandleDragMove(200);

        // scrollPerPixel = 1000 / (800 - thumbHeight), but whatever it is,
        // 100 px of mouse movement should produce positive, bounded scroll.
        Assert.True(src.ScrollValue > 0);
        Assert.True(src.ScrollValue <= 1000);
    }

    [AvaloniaFact]
    public void InnerThumbDrag_OvershootTop_ThenReverseDown_ThumbRespondsImmediately() {
        // This is the Bug 2 regression.  Start the thumb in the middle, drag
        // it up past the top extent (scroll clamps to 0), then drag back
        // down.  Before the fix, the thumb wouldn't move until the cursor
        // returned to the exact position where it had originally hit the
        // top — "dead travel" equal to the overshoot distance.
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        // Start inner-thumb drag.  Anchor at mouse Y = 400 (middle-ish).
        bar.StartInnerDrag(400);

        // Drag up well past the top: mouse Y = 0, delta = −400 px.
        // ScrollValue clamps to 0.
        bar.HandleDragMove(0);
        Assert.Equal(0, src.ScrollValue);

        // Now REVERSE — drag down by even one pixel.  With the fix, the
        // anchor has been reset to (0, 0), so a downward move immediately
        // moves the scroll value off zero.  Without the fix, the anchor
        // is still at (400, 500), and dragging back to mouse Y = 1 would
        // compute newValue = 500 + (1 − 400) * scrollPerPixel ≈ 500 −
        // (big negative), still clamped at 0.
        bar.HandleDragMove(1);
        Assert.True(src.ScrollValue > 0,
            $"After overshoot and reversal, scroll should respond immediately " +
            $"to the downward move.  ScrollValue = {src.ScrollValue}.");
    }

    [AvaloniaFact]
    public void InnerThumbDrag_OvershootBottom_ThenReverseUp_ThumbRespondsImmediately() {
        // Mirror of the previous test — overshoot the BOTTOM extent and
        // verify reversal responds immediately.
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        bar.StartInnerDrag(400);

        // Drag down well past the bottom.
        bar.HandleDragMove(800);
        Assert.Equal(1000, src.ScrollValue);

        // Reverse — drag up by one pixel.
        bar.HandleDragMove(799);
        Assert.True(src.ScrollValue < 1000,
            $"After overshoot and reversal, scroll should respond immediately. " +
            $"ScrollValue = {src.ScrollValue}.");
    }

    [AvaloniaFact]
    public void InnerThumbDrag_RepeatedOvershootAndReverse_NeverGetsStuck() {
        // Stress version of the regression test — ping-pong the thumb past
        // both extents repeatedly and verify the scroll value tracks the
        // direction changes every time.
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;
        bar.StartInnerDrag(400);

        // Overshoot top.
        bar.HandleDragMove(-200);
        Assert.Equal(0, src.ScrollValue);

        // Reverse to bottom (past bottom).
        bar.HandleDragMove(2000);
        Assert.Equal(1000, src.ScrollValue);

        // Reverse to top (past top).
        bar.HandleDragMove(-500);
        Assert.Equal(0, src.ScrollValue);

        // One more reversal — must still respond.
        bar.HandleDragMove(-499);
        Assert.True(src.ScrollValue > 0,
            $"Expected scroll to start moving downward immediately after " +
            $"reversing direction.  ScrollValue = {src.ScrollValue}.");
    }

    // ---------------------------------------------------------------
    // Middle-drag — initiated from EditorControl via middle mouse
    // button.  Passes cumulative delta from press through
    // UpdateExternalMiddleDrag → HandleDragMove's outer-zone branch.
    //
    // These tests lock in the fix for a subtle bug: the external API
    // previously added _dragStartMouseY to the cumulative delta, which
    // contaminated the external coordinate system with the internal
    // anchor-reset shifts that fire when scroll clamps at an extent.
    // Result: middle-drag had dead-travel on overshoot reversal even
    // though the outer-zone branch had the reset logic.
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void MiddleDrag_SimpleDownwardDrag_ScrollsProportionally() {
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        bar.BeginExternalMiddleDrag();
        // Cumulative delta of +50 (mouse 50 px below press).
        bar.UpdateExternalMiddleDrag(50);
        Assert.True(src.ScrollValue > 500,
            $"Expected middle-drag down to increase scroll value. " +
            $"ScrollValue = {src.ScrollValue}.");
        bar.EndExternalMiddleDrag();
    }

    [AvaloniaFact]
    public void MiddleDrag_OvershootTop_ThenReverseDown_RespondsImmediately() {
        // Same shape as InnerThumbDrag_OvershootTop_* but for the middle
        // drag path.  The bug was that after an overshoot + clamp, the
        // anchor reset inside HandleDragMove would shift _dragStartMouseY,
        // and the next UpdateExternalMiddleDrag(cum) call would add the
        // shifted anchor to the caller's cumulative delta — producing a
        // virtual mouseY that didn't correspond to the actual mouse
        // position.  After the fix, UpdateExternalMiddleDrag passes the
        // cumulative delta straight through to HandleDragMove.
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        bar.BeginExternalMiddleDrag();

        // Drag up well past the top.  Cumulative deltas are from the
        // press point, and they go more negative as the user drags up.
        bar.UpdateExternalMiddleDrag(-100);
        bar.UpdateExternalMiddleDrag(-200);
        bar.UpdateExternalMiddleDrag(-500);
        Assert.Equal(0, src.ScrollValue);

        // Reverse: mouse moves back down by even a few pixels.  The
        // cumulative delta goes from -500 to -495 (still negative
        // relative to press, but moving toward zero).  The scroll must
        // start moving DOWNWARD immediately.
        bar.UpdateExternalMiddleDrag(-495);
        Assert.True(src.ScrollValue > 0,
            $"After middle-drag overshoot and reversal, scroll should " +
            $"respond immediately.  ScrollValue = {src.ScrollValue}.");

        bar.EndExternalMiddleDrag();
    }

    [AvaloniaFact]
    public void MiddleDrag_OvershootBottom_ThenReverseUp_RespondsImmediately() {
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        bar.BeginExternalMiddleDrag();

        bar.UpdateExternalMiddleDrag(100);
        bar.UpdateExternalMiddleDrag(300);
        bar.UpdateExternalMiddleDrag(800);
        Assert.Equal(1000, src.ScrollValue);

        // Reverse (drag up a little).
        bar.UpdateExternalMiddleDrag(795);
        Assert.True(src.ScrollValue < 1000,
            $"After middle-drag bottom overshoot and reversal, scroll " +
            $"should respond immediately.  ScrollValue = {src.ScrollValue}.");

        bar.EndExternalMiddleDrag();
    }

    [AvaloniaFact]
    public void MiddleDrag_RepeatedPingPong_TrackReversals() {
        var (bar, src) = CreateBar(maximum: 1000);
        src.ScrollValue = 500;

        bar.BeginExternalMiddleDrag();

        bar.UpdateExternalMiddleDrag(-1000);
        Assert.Equal(0, src.ScrollValue);

        bar.UpdateExternalMiddleDrag(1000);
        Assert.Equal(1000, src.ScrollValue);

        bar.UpdateExternalMiddleDrag(-1000);
        Assert.Equal(0, src.ScrollValue);

        // One tiny reversal — must respond.
        bar.UpdateExternalMiddleDrag(-995);
        Assert.True(src.ScrollValue > 0,
            $"Middle-drag ping-pong: tiny reversal after overshoot must " +
            $"respond immediately.  ScrollValue = {src.ScrollValue}.");

        bar.EndExternalMiddleDrag();
    }

    // ---------------------------------------------------------------
    // Outer-zone drag — initiated by clicking on an outer zone of a
    // dual-zone scrollbar.  Dual-zone mode kicks in when the
    // proportional thumb would shrink below MinInnerThumbHeight, which
    // requires an extent-to-viewport ratio of ~45:1 or higher.
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void OuterZoneDrag_DualZoneMode_OvershootTop_ThenReverse_RespondsImmediately() {
        // Huge maximum → dual-zone mode.  Outer-zone click + drag goes
        // through the same OuterTop/OuterBottom branch as middle-drag,
        // but the caller passes absolute mouseY (not cumulative delta).
        var (bar, src) = CreateBar(maximum: 100_000);
        src.ScrollValue = 50_000;
        // Crank the multiplier so a test-scale drag actually reaches the
        // extent.  Default multiplier is 2.0, giving rate ≈ 5 px-scroll
        // per px-mouse, which would need ~10000 pixels of drag to cross
        // a 100 000 Maximum.  Use 100 so 50 px of drag scrolls ~13k.
        bar.OuterScrollRateMultiplier = 100;

        // Click on outer-top at mouseY = 100.  Anchor stored at 100.
        bar.StartOuterDrag(100, DualZoneScrollBar.HitZone.OuterTop);

        // Drag up well past the top.  HandleDragMove gets absolute mouseY.
        bar.HandleDragMove(50);
        bar.HandleDragMove(-500);
        Assert.Equal(0, src.ScrollValue);

        // Reverse by one pixel.  With the anchor reset working, scroll
        // should come off the top immediately.
        bar.HandleDragMove(-499);
        Assert.True(src.ScrollValue > 0,
            $"Outer-zone drag overshoot reversal should respond " +
            $"immediately.  ScrollValue = {src.ScrollValue}.");
    }

    [AvaloniaFact]
    public void OuterZoneDrag_DualZoneMode_OvershootBottom_ThenReverse_RespondsImmediately() {
        var (bar, src) = CreateBar(maximum: 100_000);
        src.ScrollValue = 50_000;
        bar.OuterScrollRateMultiplier = 100;

        // Click on outer-bottom at mouseY = 700.
        bar.StartOuterDrag(700, DualZoneScrollBar.HitZone.OuterBottom);

        bar.HandleDragMove(800);
        bar.HandleDragMove(1500);
        Assert.Equal(100_000, src.ScrollValue);

        bar.HandleDragMove(1499);
        Assert.True(src.ScrollValue < 100_000,
            $"Outer-zone bottom overshoot reversal should respond " +
            $"immediately.  ScrollValue = {src.ScrollValue}.");
    }

    // ---------------------------------------------------------------
    // Arrow-button scroll (ScrollByRows).  Regression tests for a
    // floating-point drift bug where clicking the scrollbar up/down
    // arrow would get stuck at specific scroll positions whenever the
    // row height was not exactly representable in binary (e.g. 17.6 at
    // 125% DPI).  For certain integer row counts k, (k*RH)/RH evaluates
    // to k − ulp in double precision, so Math.Floor returns k − 1
    // instead of k — and the resulting newValue ((k−1+1)*RH = k*RH)
    // matches the current Value within a few ulps, causing
    // RequestScroll's "meaningful change" guard to silently drop the
    // event.  The scrollbar arrow would just stop responding at that
    // exact position until the user nudged the view with some other
    // mechanism.
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void ArrowDown_AtFractionalRowBoundary_AlwaysAdvances() {
        // Value = 528, RH = 17.6 — the exact case reported by the user.
        // In double precision, 528 / 17.6 ≈ 29.999999999999996, so the
        // old code computed floor(...) + 1 = 30, * 17.6 ≈ 528, matching
        // Value and silently dropping the click.
        var (bar, src) = CreateBar(maximum: 10_000, rowHeight: 17.6);
        src.ScrollValue = 528;

        bar.ScrollByRows(+1);

        Assert.True(src.ScrollValue > 528,
            $"Arrow-down at row boundary with fractional RH must advance. " +
            $"ScrollValue = {src.ScrollValue}.");
    }

    [AvaloniaFact]
    public void ArrowUp_AtFractionalRowBoundary_AlwaysRetreats() {
        // Mirror of the down case.  With the old ceil-based math,
        // ceil(29.999999999999996) = 30, then +(-1) = 29, * 17.6 ≈ 510.4 —
        // actually this direction may not trip because ceil rounds UP.
        // But a Value slightly above a row boundary would trip the
        // equivalent issue on the ceil path: ceil(k + ulp) = k + 1,
        // then (k + 1 − 1)*RH = k*RH which may equal Value within a few
        // ulps.  We use Value = 528 here as a round number; if the fp
        // math doesn't happen to trip this exact case, the sweep test
        // below is the real safety net.
        var (bar, src) = CreateBar(maximum: 10_000, rowHeight: 17.6);
        src.ScrollValue = 528;

        bar.ScrollByRows(-1);

        Assert.True(src.ScrollValue < 528,
            $"Arrow-up at row boundary with fractional RH must retreat. " +
            $"ScrollValue = {src.ScrollValue}.");
    }

    [AvaloniaFact]
    public void ArrowDown_SweepAcrossManyRows_NeverJams_FractionalRowHeight() {
        // Walk the arrow-down button all the way from 0 to near the
        // scroll maximum.  Every click must strictly advance the scroll
        // value.  This would have caught the original bug at row 30
        // (528 px) and any other fp-drift positions — and also catches
        // future regressions at whatever row counts happen to land on
        // the wrong side of an ulp boundary for this particular RH.
        var (bar, src) = CreateBar(maximum: 100_000, rowHeight: 17.6);

        double prev = src.ScrollValue;
        for (int i = 0; i < 500; i++) {
            bar.ScrollByRows(+1);
            Assert.True(src.ScrollValue > prev,
                $"Arrow-down click {i} did not advance.  " +
                $"prev = {prev}, ScrollValue = {src.ScrollValue}, RH = 17.6.");
            prev = src.ScrollValue;
        }
    }

    [AvaloniaFact]
    public void ArrowUp_SweepAcrossManyRows_NeverJams_FractionalRowHeight() {
        var (bar, src) = CreateBar(maximum: 100_000, rowHeight: 17.6);
        src.ScrollValue = 500 * 17.6; // start near row 500

        double prev = src.ScrollValue;
        for (int i = 0; i < 499; i++) {
            bar.ScrollByRows(-1);
            Assert.True(src.ScrollValue < prev,
                $"Arrow-up click {i} did not retreat.  " +
                $"prev = {prev}, ScrollValue = {src.ScrollValue}, RH = 17.6.");
            prev = src.ScrollValue;
        }
    }

    [AvaloniaFact]
    public void ArrowDown_FromMidRowPosition_SnapsAndAdvances() {
        // Preserves the original UX intent: arrow-down from a mid-row
        // position (e.g. after a drag) should snap to the next row
        // boundary.  17.6 * 30 = 528, so starting at 520 (mid-row 29)
        // the next down click should land at 528 (row 30) — the first
        // click partially completes the row rather than jumping past.
        var (bar, src) = CreateBar(maximum: 10_000, rowHeight: 17.6);
        src.ScrollValue = 520;

        bar.ScrollByRows(+1);

        // Expect ≈ 528 (row 30), within a few ulps.
        Assert.InRange(src.ScrollValue, 527.9, 528.1);
    }

    [AvaloniaFact]
    public void ArrowUp_FromMidRowPosition_SnapsAndRetreats() {
        // Mirror of the down case.  Starting at 530 (just past row 30),
        // arrow-up should snap down to row 30 (≈ 528), not all the way
        // to row 29 (≈ 510.4).
        var (bar, src) = CreateBar(maximum: 10_000, rowHeight: 17.6);
        src.ScrollValue = 530;

        bar.ScrollByRows(-1);

        Assert.InRange(src.ScrollValue, 527.9, 528.1);
    }

    [AvaloniaFact]
    public void ArrowDown_AtScrollMaximum_DoesNothing() {
        // Sanity check: at the bottom, the arrow should stay at the
        // maximum (not wrap, not fire a ghost event).  The "no
        // meaningful change" guard in RequestScroll is doing its job
        // in this case, and the fix must not break it.
        var (bar, src) = CreateBar(maximum: 1000, rowHeight: 17.6);
        src.ScrollValue = 1000;

        bar.ScrollByRows(+1);

        Assert.Equal(1000, src.ScrollValue);
    }

    [AvaloniaFact]
    public void ArrowUp_AtScrollZero_DoesNothing() {
        var (bar, src) = CreateBar(maximum: 1000, rowHeight: 17.6);
        src.ScrollValue = 0;

        bar.ScrollByRows(-1);

        Assert.Equal(0, src.ScrollValue);
    }
}
