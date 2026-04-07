using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DMEdit.App.Controls;

/// <summary>
/// A tiny child control that paints a single caret rectangle.  Lives inside
/// <see cref="EditorControl"/> and is positioned via
/// <c>EditorControl.ArrangeOverride</c>.
/// </summary>
/// <remarks>
/// The whole point of this control is that calling
/// <see cref="Visual.InvalidateVisual"/> on it (when the caret blinks) only
/// re-paints its own ~20 pixels — it does not trigger
/// <c>EditorControl.Render</c>, so the editor's per-row scene-graph rebuild
/// (which dominates per-blink allocation cost) is skipped entirely while the
/// caret is just blinking on idle.  Editor frames are now driven only by
/// real changes: edits, scroll, focus, layout invalidation.
/// </remarks>
public sealed class CaretLayer : Control {

    private bool _visible = true;
    private IBrush _brush = Brushes.Black;
    private double _caretWidth = 1.0;
    private bool _overwriteMode;

    public CaretLayer() {
        // Layout / hit-test must not see this control as interactive.
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>
    /// Whether the caret is currently shown.  Toggling this is the only
    /// per-blink state change.
    /// </summary>
    public bool CaretVisible {
        get => _visible;
        set {
            if (_visible == value) return;
            _visible = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Pen brush for the caret rectangle.  In overwrite mode the brush is
    /// applied with reduced alpha for the block-style cursor.
    /// </summary>
    public IBrush Brush {
        get => _brush;
        set {
            if (ReferenceEquals(_brush, value)) return;
            _brush = value;
            InvalidateVisual();
        }
    }

    /// <summary>Width of the caret rectangle in pixels (insertion mode).</summary>
    public double CaretWidth {
        get => _caretWidth;
        set {
            if (_caretWidth == value) return;
            _caretWidth = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// True for the block-style overwrite caret (paints a translucent
    /// rectangle the full width of the layer's bounds), false for the
    /// thin insertion-point caret.
    /// </summary>
    public bool OverwriteMode {
        get => _overwriteMode;
        set {
            if (_overwriteMode == value) return;
            _overwriteMode = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context) {
        if (!_visible) return;
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (_overwriteMode) {
            // Block caret: full layer width, translucent fill so the glyph
            // beneath remains readable.
            var color = _brush is ISolidColorBrush scb
                ? Color.FromArgb(100, scb.Color.R, scb.Color.G, scb.Color.B)
                : Color.FromArgb(100, 0, 0, 0);
            context.FillRectangle(new SolidColorBrush(color),
                new Rect(0, 0, bounds.Width, bounds.Height));
        } else {
            // Insertion caret: thin vertical bar at the layer's left edge.
            context.FillRectangle(_brush, new Rect(0, 0, _caretWidth, bounds.Height));
        }
    }
}
