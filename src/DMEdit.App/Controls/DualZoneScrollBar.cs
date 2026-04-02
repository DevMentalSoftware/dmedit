using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DMEdit.App.Services;

namespace DMEdit.App.Controls;

/// <summary>
/// A custom vertical scrollbar with two-zone thumb support for large documents.
///
/// When the document is small enough that a proportional thumb stays above a
/// minimum size, the scrollbar behaves like a normal scrollbar. When the document
/// is large enough that the proportional thumb would shrink below the minimum,
/// the thumb switches to a fixed-height assembly with three zones:
///
///   outer-top   — fixed-rate drag (lines/pixel independent of doc size)
///   inner       — proportional drag (standard scrollbar behavior)
///   outer-bottom — fixed-rate drag
///
/// The outer zones only appear in dual-zone mode and their visual extent adjusts
/// at document extremes (no outer-top when at top, no outer-bottom when at bottom).
///
/// See docs/design-journal.md "Two-Zone Custom Scrollbar" for full spec.
/// </summary>
public sealed class DualZoneScrollBar : Control {
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const double BarWidth = 17;
    private const double ArrowHeight = 17;
    private const double MinInnerThumbHeight = 17;   // same as arrow button
    private const double MinOuterZoneHeight = 8.5;    // half of arrow button
    private const double ReferenceDocLines = 100;     // for fixed-rate calc

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private EditorTheme _theme = EditorTheme.Light;

    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // Scroll state — read from an IScrollSource (single source of truth)
    // -------------------------------------------------------------------------

    private IScrollSource? _scrollSource;

    /// <summary>
    /// The scroll source this scrollbar reads state from. All scroll
    /// values (maximum, value, viewport, extent, row height) are read
    /// directly — no duplicated state.
    /// </summary>
    public IScrollSource? ScrollSource {
        get => _scrollSource;
        set {
            if (_scrollSource == value) return;
            _scrollSource = value;
            InvalidateVisual();
        }
    }

    private double Maximum => _scrollSource?.ScrollMaximum ?? 0;
    private double Value => _scrollSource?.ScrollValue ?? 0;
    private double ViewportSize => _scrollSource?.ScrollViewportHeight ?? 0;
    private double ExtentSize => _scrollSource?.ScrollExtentHeight ?? 0;
    private double RowHeight => _scrollSource?.RowHeightValue ?? 20;

    /// <summary>
    /// Multiplier applied to the outer-thumb fixed scroll rate.
    /// 1.0 = baseline (~100-line-doc feel). Higher = faster scanning.
    /// </summary>
    public double OuterScrollRateMultiplier { get; set; } = 2.0;

    /// <summary>Fired when the user drags or clicks. Carries the requested new scroll value.</summary>
    public event Action<double>? ScrollRequested;

    /// <summary>True while any drag operation is in progress (inner, outer, or middle).</summary>
    public bool IsDragging => _isDragging;

    /// <summary>Fired when any pointer interaction ends (drag release, arrow-hold release, etc.).</summary>
    public event Action? InteractionEnded;

    /// <summary>
    /// Fired when the user clicks the track above or below the thumb.
    /// The parameter is the direction: −1 for page up, +1 for page down.
    /// The <see cref="EditorControl"/> handles the actual scrolling in
    /// line-space so that wrapping is handled correctly.
    /// </summary>
    public event Action<int>? PageRequested;

    // -------------------------------------------------------------------------
    // Interaction state
    // -------------------------------------------------------------------------

    private enum HitZone {
        None,
        ArrowUp,
        ArrowDown,
        TrackAbove,
        TrackBelow,
        InnerThumb,
        OuterTop,
        OuterBottom
    }

    private HitZone _hoverZone = HitZone.None;
    private HitZone _pressedZone = HitZone.None;
    private bool _isDragging;
    private bool _isMiddleDrag;
    private double _dragStartMouseY;
    private double _dragStartValue;
    private double _outerDragVisualOffset;
    private DispatcherTimer? _repeatTimer;

    // -------------------------------------------------------------------------
    // Thumb geometry
    // -------------------------------------------------------------------------

    private readonly struct ThumbGeo {
        public required bool IsDualZone { get; init; }
        public required double TrackTop { get; init; }
        public required double TrackHeight { get; init; }
        public required double ThumbTop { get; init; }
        public required double InnerTop { get; init; }
        public required double InnerBottom { get; init; }
        public required double ThumbBottom { get; init; }
        public required double InnerHeight { get; init; }
        public required double OuterTopHeight { get; init; }
        public required double OuterBottomHeight { get; init; }
        public required double TotalThumbHeight { get; init; }

        public double TrackBottom => TrackTop + TrackHeight;
    }

    private ThumbGeo ComputeThumbGeometry() {
        var trackTop = ArrowHeight;
        var trackHeight = Math.Max(0, Bounds.Height - 2 * ArrowHeight);
        if (trackHeight < 1 || Maximum < 1) {
            // No scrolling possible — degenerate case
            return new ThumbGeo {
                IsDualZone = false,
                TrackTop = trackTop,
                TrackHeight = trackHeight,
                ThumbTop = trackTop,
                InnerTop = trackTop,
                InnerBottom = trackTop + trackHeight,
                ThumbBottom = trackTop + trackHeight,
                InnerHeight = trackHeight,
                OuterTopHeight = 0,
                OuterBottomHeight = 0,
                TotalThumbHeight = trackHeight,
            };
        }

        var fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;

        // Proportional inner thumb height
        var proportionalHeight = ExtentSize > 0
            ? (ViewportSize / ExtentSize) * trackHeight
            : trackHeight;

        bool isDualZone = proportionalHeight < MinInnerThumbHeight;

        double totalThumbHeight;
        double innerHeight;
        double outerTopHeight;
        double outerBottomHeight;

        if (!isDualZone) {
            // Normal mode — just a proportional thumb, no outer zones
            totalThumbHeight = Math.Clamp(proportionalHeight, MinInnerThumbHeight, trackHeight);
            innerHeight = totalThumbHeight;
            outerTopHeight = 0;
            outerBottomHeight = 0;
        } else {
            // Dual-zone mode — fixed total thumb height
            totalThumbHeight = MinInnerThumbHeight + 2 * MinOuterZoneHeight;
            if (totalThumbHeight > trackHeight) {
                totalThumbHeight = trackHeight;
            }

            // At the very extremes, the outer zone pointing toward the boundary
            // is hidden (can't scroll further) and its space goes to inner.
            // Otherwise both zones stay at full size — no gradual fade that
            // creates tiny ungrabable zones.
            outerTopHeight = fraction > 0.005 ? MinOuterZoneHeight : 0;
            outerBottomHeight = fraction < 0.995 ? MinOuterZoneHeight : 0;
            innerHeight = totalThumbHeight - outerTopHeight - outerBottomHeight;
            innerHeight = Math.Max(innerHeight, MinInnerThumbHeight);
        }

        var availableRange = trackHeight - totalThumbHeight;
        var thumbTop = trackTop + fraction * availableRange;

        return new ThumbGeo {
            IsDualZone = isDualZone,
            TrackTop = trackTop,
            TrackHeight = trackHeight,
            ThumbTop = thumbTop,
            InnerTop = thumbTop + outerTopHeight,
            InnerBottom = thumbTop + outerTopHeight + innerHeight,
            ThumbBottom = thumbTop + totalThumbHeight,
            InnerHeight = innerHeight,
            OuterTopHeight = outerTopHeight,
            OuterBottomHeight = outerBottomHeight,
            TotalThumbHeight = totalThumbHeight,
        };
    }

    /// <summary>
    /// Computes the fixed scroll rate (pixels of scroll per pixel of mouse drag)
    /// that makes outer-zone dragging feel like a proportional thumb on a ~100-line doc.
    /// </summary>
    private double ComputeFixedScrollRate() {
        var trackHeight = Math.Max(1, Bounds.Height - 2 * ArrowHeight);
        var refExtent = ReferenceDocLines * RowHeight;
        var refViewport = ViewportSize;
        if (refExtent <= refViewport) {
            return 1.0; // trivial doc — fallback
        }
        var refThumbHeight = (refViewport / refExtent) * trackHeight;
        refThumbHeight = Math.Max(refThumbHeight, MinInnerThumbHeight);
        var refDragRange = trackHeight - refThumbHeight;
        if (refDragRange < 1) {
            return 1.0;
        }
        var refMaxScroll = refExtent - refViewport;
        return (refMaxScroll / refDragRange) * OuterScrollRateMultiplier;
    }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------

    private HitZone HitTestZone(Point pt) {
        var y = pt.Y;
        if (y < ArrowHeight) {
            return HitZone.ArrowUp;
        }
        if (y >= Bounds.Height - ArrowHeight) {
            return HitZone.ArrowDown;
        }

        var geo = ComputeThumbGeometry();
        if (Maximum < 1) {
            return HitZone.None;
        }

        if (geo.IsDualZone) {
            // Check outer-top (accounting for visual drag offset)
            var outerTopDisplayTop = geo.ThumbTop;
            var outerTopDisplayBottom = geo.InnerTop;
            if (_isDragging && _pressedZone == HitZone.OuterTop) {
                outerTopDisplayTop += _outerDragVisualOffset;
                outerTopDisplayBottom = geo.InnerTop; // inner stays put
            }
            if (y >= outerTopDisplayTop && y < outerTopDisplayBottom && geo.OuterTopHeight > 0.5) {
                return HitZone.OuterTop;
            }

            // Check outer-bottom (accounting for visual drag offset)
            var outerBotDisplayTop = geo.InnerBottom;
            var outerBotDisplayBottom = geo.ThumbBottom;
            if (_isDragging && _pressedZone == HitZone.OuterBottom) {
                outerBotDisplayTop = geo.InnerBottom;
                outerBotDisplayBottom += _outerDragVisualOffset;
            }
            if (y >= outerBotDisplayTop && y < outerBotDisplayBottom && geo.OuterBottomHeight > 0.5) {
                return HitZone.OuterBottom;
            }
        }

        // Inner thumb
        if (y >= geo.InnerTop && y < geo.InnerBottom) {
            return HitZone.InnerThumb;
        }

        // Track regions
        if (y < geo.ThumbTop) {
            return HitZone.TrackAbove;
        }
        if (y >= geo.ThumbBottom) {
            return HitZone.TrackBelow;
        }

        // If we're in the thumb assembly but didn't match a zone, treat as inner
        return HitZone.InnerThumb;
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public DualZoneScrollBar() {
        Width = BarWidth;
        ClipToBounds = true;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext ctx) {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 1 || h < 1) {
            return;
        }

        // Track background
        ctx.FillRectangle(_theme.ScrollTrack, new Rect(0, 0, w, h));

        // Arrow buttons
        DrawArrowButton(ctx, isUp: true, w);
        DrawArrowButton(ctx, isUp: false, w);

        if (Maximum < 1) {
            return; // nothing to scroll
        }

        var geo = ComputeThumbGeometry();

        // Draw outer-top zone
        if (geo.IsDualZone && geo.OuterTopHeight > 0.5) {
            var otTop = geo.ThumbTop;
            // If dragging outer-top, apply visual offset
            if (_isDragging && _pressedZone == HitZone.OuterTop) {
                otTop += _outerDragVisualOffset;
            }
            var brush = _pressedZone == HitZone.OuterTop ? _theme.ScrollOuterThumbPress
                : _hoverZone == HitZone.OuterTop ? _theme.ScrollOuterThumbHover
                : _theme.ScrollOuterThumbNormal;
            ctx.FillRectangle(brush, new Rect(1, otTop, w - 2, geo.OuterTopHeight));
        }

        // Draw inner thumb
        {
            var brush = _pressedZone == HitZone.InnerThumb ? _theme.ScrollInnerThumbPress
                : _hoverZone == HitZone.InnerThumb ? _theme.ScrollInnerThumbHover
                : _theme.ScrollInnerThumbNormal;
            ctx.FillRectangle(brush, new Rect(1, geo.InnerTop, w - 2, geo.InnerHeight));
        }

        // Draw outer-bottom zone
        if (geo.IsDualZone && geo.OuterBottomHeight > 0.5) {
            var obTop = geo.InnerBottom;
            var obHeight = geo.OuterBottomHeight;
            // If dragging outer-bottom, apply visual offset
            if (_isDragging && _pressedZone == HitZone.OuterBottom) {
                obTop += _outerDragVisualOffset;
            }
            var brush = _pressedZone == HitZone.OuterBottom ? _theme.ScrollOuterThumbPress
                : _hoverZone == HitZone.OuterBottom ? _theme.ScrollOuterThumbHover
                : _theme.ScrollOuterThumbNormal;
            ctx.FillRectangle(brush, new Rect(1, obTop, w - 2, obHeight));
        }
    }

    private void DrawArrowButton(DrawingContext ctx, bool isUp, double w) {
        var y = isUp ? 0 : Bounds.Height - ArrowHeight;
        var zone = isUp ? HitZone.ArrowUp : HitZone.ArrowDown;

        var bg = _pressedZone == zone ? _theme.ScrollArrowBgPress
            : _hoverZone == zone ? _theme.ScrollArrowBgHover
            : _theme.ScrollArrowBg;
        ctx.FillRectangle(bg, new Rect(0, y, w, ArrowHeight));

        // Draw triangle glyph
        var cx = w / 2;
        var cy = y + ArrowHeight / 2;
        var triangleSize = 4.5;
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) {
            if (isUp) {
                sgc.BeginFigure(new Point(cx, cy - triangleSize), true);
                sgc.LineTo(new Point(cx - triangleSize, cy + triangleSize));
                sgc.LineTo(new Point(cx + triangleSize, cy + triangleSize));
            } else {
                sgc.BeginFigure(new Point(cx, cy + triangleSize), true);
                sgc.LineTo(new Point(cx - triangleSize, cy - triangleSize));
                sgc.LineTo(new Point(cx + triangleSize, cy - triangleSize));
            }
            sgc.EndFigure(true);
        }
        ctx.DrawGeometry(_theme.ScrollArrowGlyph, null, geo);
    }

    // -------------------------------------------------------------------------
    // Mouse interaction
    // -------------------------------------------------------------------------

    protected override void OnPointerEntered(PointerEventArgs e) {
        base.OnPointerEntered(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (!_isDragging) {
            _hoverZone = HitZone.None;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (_isDragging) {
            HandleDragMove(e.GetPosition(this).Y);
        } else {
            UpdateHover(e.GetPosition(this));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var isLeft = props.IsLeftButtonPressed;
        var isMiddle = props.IsMiddleButtonPressed;
        if (!isLeft && !isMiddle) {
            return;
        }

        var pt = e.GetPosition(this);
        var zone = HitTestZone(pt);

        // Middle-click anywhere on the scrollbar → fixed-rate outer drag.
        // The visual zone flips dynamically in HandleDragMove based on
        // the drag direction.
        if (isMiddle) {
            _isMiddleDrag = true;
            _pressedZone = HitZone.OuterBottom; // initial; HandleDragMove will flip
            StartOuterDrag(pt.Y, HitZone.OuterBottom);
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        // Left-click: normal zone-specific behavior
        _pressedZone = zone;

        switch (zone) {
            case HitZone.InnerThumb:
                StartInnerDrag(pt.Y);
                break;

            case HitZone.OuterTop:
            case HitZone.OuterBottom:
                StartOuterDrag(pt.Y, zone);
                break;

            case HitZone.ArrowUp:
                ScrollByRows(-1);
                StartRepeat(() => ScrollByRows(-1));
                break;

            case HitZone.ArrowDown:
                ScrollByRows(+1);
                StartRepeat(() => ScrollByRows(+1));
                break;

            case HitZone.TrackAbove:
                PageRequested?.Invoke(-1);
                StartRepeat(() => PageRequested?.Invoke(-1));
                break;

            case HitZone.TrackBelow:
                PageRequested?.Invoke(+1);
                StartRepeat(() => PageRequested?.Invoke(+1));
                break;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        StopRepeat();

        if (_isDragging) {
            _isDragging = false;
            _isMiddleDrag = false;
            _outerDragVisualOffset = 0;
        }
        _pressedZone = HitZone.None;
        e.Pointer.Capture(null);
        UpdateHover(e.GetPosition(this));
        InvalidateVisual();
        InteractionEnded?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        if (_isDragging) {
            _isDragging = false;
            _isMiddleDrag = false;
            _outerDragVisualOffset = 0;
            StopRepeat();
            _pressedZone = HitZone.None;
            InvalidateVisual();
            InteractionEnded?.Invoke();
        }
    }

    private void UpdateHover(Point pt) {
        var zone = HitTestZone(pt);
        if (zone != _hoverZone) {
            _hoverZone = zone;
            InvalidateVisual();
        }
    }

    // -------------------------------------------------------------------------
    // Dragging
    // -------------------------------------------------------------------------

    private void StartInnerDrag(double mouseY) {
        _isDragging = true;
        _dragStartMouseY = mouseY;
        _dragStartValue = Value;
        _outerDragVisualOffset = 0;
    }

    private void StartOuterDrag(double mouseY, HitZone zone) {
        _isDragging = true;
        _pressedZone = zone;
        _dragStartMouseY = mouseY;
        _dragStartValue = Value;
        _outerDragVisualOffset = 0;
    }

    private void HandleDragMove(double mouseY) {
        var deltaPixels = mouseY - _dragStartMouseY;

        if (_pressedZone == HitZone.InnerThumb) {
            // Proportional drag — map pixel movement to scroll range
            var geo = ComputeThumbGeometry();
            var availableRange = geo.TrackHeight - geo.TotalThumbHeight;
            if (availableRange > 0) {
                var scrollPerPixel = Maximum / availableRange;
                var newValue = _dragStartValue + deltaPixels * scrollPerPixel;
                RequestScroll(newValue);
            }
        } else if (_pressedZone == HitZone.OuterTop || _pressedZone == HitZone.OuterBottom) {
            // Middle-drag: flip the visual zone based on drag direction so
            // dragging up shows the outer-top separating and dragging down
            // shows the outer-bottom separating.
            if (_isMiddleDrag) {
                _pressedZone = deltaPixels < 0 ? HitZone.OuterTop : HitZone.OuterBottom;
            }

            // Fixed-rate drag — direction comes from deltaPixels sign naturally.
            var rate = ComputeFixedScrollRate();
            var unclamped = _dragStartValue + deltaPixels * rate;
            RequestScroll(unclamped);

            // If scroll hit a boundary (top or bottom), reset the drag anchor
            // so that reversing direction immediately starts scrolling again
            // instead of having dead travel back to the original anchor point.
            if (Math.Abs(Value - unclamped) > 0.01) {
                _dragStartMouseY = mouseY;
                _dragStartValue = Value;
            }

            // Visual offset: the grabbed zone separates from the inner thumb
            _outerDragVisualOffset = deltaPixels;
            // Clamp visual offset to stay within track
            var geo = ComputeThumbGeometry();
            if (_pressedZone == HitZone.OuterTop) {
                _outerDragVisualOffset = Math.Min(0, _outerDragVisualOffset); // can only go up
                _outerDragVisualOffset = Math.Max(-(geo.ThumbTop - geo.TrackTop), _outerDragVisualOffset);
            } else {
                _outerDragVisualOffset = Math.Max(0, _outerDragVisualOffset); // can only go down
                _outerDragVisualOffset = Math.Min(geo.TrackBottom - geo.ThumbBottom, _outerDragVisualOffset);
            }
        }

        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // External middle-drag (initiated from EditorControl)
    // -------------------------------------------------------------------------

    /// <summary>Begin a middle-drag initiated from outside (e.g. EditorControl).</summary>
    public void BeginExternalMiddleDrag() {
        _isMiddleDrag = true;
        _isDragging = true;
        _pressedZone = HitZone.OuterBottom;
        _dragStartMouseY = 0;
        _dragStartValue = Value;
        _outerDragVisualOffset = 0;
        InvalidateVisual();
    }

    /// <summary>Update the external middle-drag with a pixel delta from the press point.</summary>
    public void UpdateExternalMiddleDrag(double deltaPixels) {
        if (!_isMiddleDrag) {
            return;
        }
        HandleDragMove(_dragStartMouseY + deltaPixels);
    }

    /// <summary>End the external middle-drag; snap visuals back.</summary>
    public void EndExternalMiddleDrag() {
        if (!_isMiddleDrag) {
            return;
        }
        _isDragging = false;
        _isMiddleDrag = false;
        _outerDragVisualOffset = 0;
        _pressedZone = HitZone.None;
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // Scroll helpers
    // -------------------------------------------------------------------------

    private void ScrollByRows(int rows) {
        // Snap to row boundaries so each arrow click aligns to a full row.
        // If already aligned, moves by exactly |rows| rows.
        // If mid-row (e.g. from a thumb drag), the first click finishes
        // the partial row, then subsequent clicks move full rows.
        double newValue;
        if (rows > 0) {
            newValue = (Math.Floor(Value / RowHeight) + rows) * RowHeight;
        } else {
            newValue = (Math.Ceiling(Value / RowHeight) + rows) * RowHeight;
        }
        RequestScroll(newValue);
    }

    private void RequestScroll(double newValue) {
        newValue = Math.Clamp(newValue, 0, Maximum);
        if (Math.Abs(newValue - Value) > 0.01) {
            ScrollRequested?.Invoke(newValue);
            InvalidateVisual();
        }
    }

    // -------------------------------------------------------------------------
    // Repeat timer (arrow/track hold)
    // -------------------------------------------------------------------------

    private void StartRepeat(Action action) {
        StopRepeat();
        _repeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _repeatTimer.Tick += (_, _) => {
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(50); // accelerate after first tick
            action();
        };
        _repeatTimer.Start();
    }

    private void StopRepeat() {
        _repeatTimer?.Stop();
        _repeatTimer = null;
    }

    // -------------------------------------------------------------------------
    // Mouse wheel (forward to parent if needed)
    // -------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        // Scroll 3 rows per wheel notch
        var delta = -e.Delta.Y * RowHeight * 3;
        RequestScroll(Value + delta);
        e.Handled = true;
    }
}
