using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using static DMEdit.App.Controls.EditorControl;

namespace DMEdit.App.Tests;

/// <summary>
/// Verifies every arc in <see cref="EditorControl.BuildSelectionGroupGeometry"/>
/// has the correct <see cref="SweepDirection"/>.  Uses the <c>arcLog</c>
/// parameter to inspect the recorded arcs rather than relying on
/// <c>FillContains</c> (which Avalonia headless doesn't support for arcs).
///
/// The CW contour rule (screen coords, Y-down):
///   CW rotation order: RIGHT → DOWN → LEFT → UP.
///   Convex (outward) corners are CW turns, concave (inward) corners are CCW.
///
/// The test uses three rects arranged as wide/narrow/wide to exercise all
/// four internal step types:
/// <code>
///   ╭───────────────────────╮   Rect 0  (0,0)–(200,40)       wide
///   │       ╭─────────╮     │
///   ╰───────│         │─────╯   Y=40: right inward, left outward-up
///           │         │         Rect 1  (40,40)–(160,80)     narrow
///   ╭───────│         │─────╮   Y=80: right outward, left inward-up
///   │       ╰─────────╯     │
///   ╰───────────────────────╯   Rect 2  (0,80)–(200,120)     wide
/// </code>
///
/// Expected arc sequence (12 arcs):
///   outer-TR        CW      top-right of first rect
///   R-in[0]-convex  CW      right edge step inward at Y=40, DOWN→LEFT
///   R-in[0]-concave CCW     right edge step inward at Y=40, LEFT→DOWN
///   R-out[1]-concave CCW    right edge step outward at Y=80, DOWN→RIGHT
///   R-out[1]-convex CW      right edge step outward at Y=80, RIGHT→DOWN
///   outer-BR        CW      bottom-right of last rect
///   outer-BL        CW      bottom-left of last rect
///   L-in[2]-convex  CW      left edge inward-up at Y=80, UP→RIGHT
///   L-in[2]-concave CCW     left edge inward-up at Y=80, RIGHT→UP
///   L-out[1]-concave CCW    left edge outward-up at Y=40, UP→LEFT
///   L-out[1]-convex CW      left edge outward-up at Y=40, LEFT→UP
///   outer-TL        CW      top-left of first rect (closes contour)
/// </summary>
public class SelectionPathCornerTests {
    private static readonly List<Rect> Rects = [
        new Rect(0, 0, 200, 40),
        new Rect(40, 40, 120, 40),
        new Rect(0, 80, 200, 40),
    ];
    private const double R = 10;

    private static List<ArcRecord> BuildLog() {
        var log = new List<ArcRecord>();
        BuildSelectionGroupGeometry(Rects, 0, 3, R, log);
        return log;
    }

    // ── Arc count ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ArcCount_Is12() {
        var log = BuildLog();
        Assert.Equal(12, log.Count);
    }

    // ── Outer corners (all CW / convex) ─────────────────────────────────

    [AvaloniaFact]
    public void Outer_TopRight_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "outer-TR");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void Outer_BottomRight_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "outer-BR");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void Outer_BottomLeft_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "outer-BL");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void Outer_TopLeft_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "outer-TL");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    // ── Right inward step at Y=40 ──────────────────────────────────────
    // DOWN→LEFT = CW (convex), LEFT→DOWN = CCW (concave)

    [AvaloniaFact]
    public void RightInward_Convex_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "R-in[0]-convex");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void RightInward_Concave_CCW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "R-in[0]-concave");
        Assert.Equal(SweepDirection.CounterClockwise, arc.Sweep);
    }

    // ── Right outward step at Y=80 ─────────────────────────────────────
    // DOWN→RIGHT = CCW (concave), RIGHT→DOWN = CW (convex)

    [AvaloniaFact]
    public void RightOutward_Concave_CCW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "R-out[1]-concave");
        Assert.Equal(SweepDirection.CounterClockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void RightOutward_Convex_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "R-out[1]-convex");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    // ── Left inward-up step at Y=80 ────────────────────────────────────
    // UP→RIGHT = CW (convex), RIGHT→UP = CCW (concave)

    [AvaloniaFact]
    public void LeftInwardUp_Convex_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "L-in[2]-convex");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void LeftInwardUp_Concave_CCW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "L-in[2]-concave");
        Assert.Equal(SweepDirection.CounterClockwise, arc.Sweep);
    }

    // ── Left outward-up step at Y=40 ───────────────────────────────────
    // UP→LEFT = CCW (concave), LEFT→UP = CW (convex)

    [AvaloniaFact]
    public void LeftOutwardUp_Concave_CCW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "L-out[1]-concave");
        Assert.Equal(SweepDirection.CounterClockwise, arc.Sweep);
    }

    [AvaloniaFact]
    public void LeftOutwardUp_Convex_CW() {
        var log = BuildLog();
        var arc = log.First(a => a.Label == "L-out[1]-convex");
        Assert.Equal(SweepDirection.Clockwise, arc.Sweep);
    }

    // ── Cross-check: every convex arc is CW, every concave arc is CCW ──

    [AvaloniaFact]
    public void AllConvexArcs_AreCW() {
        var log = BuildLog();
        foreach (var arc in log.Where(a => a.Label.Contains("convex") || a.Label.StartsWith("outer"))) {
            Assert.True(arc.Sweep == SweepDirection.Clockwise,
                $"Arc '{arc.Label}' should be CW (convex) but was CCW");
        }
    }

    [AvaloniaFact]
    public void AllConcaveArcs_AreCCW() {
        var log = BuildLog();
        foreach (var arc in log.Where(a => a.Label.Contains("concave"))) {
            Assert.True(arc.Sweep == SweepDirection.CounterClockwise,
                $"Arc '{arc.Label}' should be CCW (concave) but was CW");
        }
    }

    // ── Group splitting ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void GroupSplit_NonOverlappingRects_ProduceSeparateGroups() {
        // Rect A far right, rect B far left — no horizontal overlap.
        var rects = new List<Rect> {
            new Rect(150, 0, 50, 20),
            new Rect(0, 20, 50, 20),
        };
        // Each should be buildable as its own 1-rect group without error.
        var logA = new List<ArcRecord>();
        BuildSelectionGroupGeometry(rects, 0, 1, 3, logA);
        Assert.Equal(4, logA.Count); // 4 outer corners

        var logB = new List<ArcRecord>();
        BuildSelectionGroupGeometry(rects, 1, 1, 3, logB);
        Assert.Equal(4, logB.Count);
    }
}
