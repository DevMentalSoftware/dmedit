using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Controls;

/// <summary>
/// Custom-drawn tab bar that mimics Windows 11 Notepad style:
/// - Active tab has rounded convex top corners and concave bottom curves
///   that blend into the content area below (menu bar)
/// - Inactive tabs are borderless (text only) with hover highlight
/// - "+" button after the last tab
/// - App icon drawn at left margin
/// </summary>
public sealed class TabBarControl : Control {
    public TabBarControl() {
        // Recompute tab layout when the control is resized (e.g. first
        // layout pass after the window appears, or window resize).
        PropertyChanged += (_, e) => {
            if (e.Property == BoundsProperty && _tabs.Count > 0) {
                ComputeTabLayout();
                InvalidateVisual();
            }
        };
    }

    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const double TabHeight = 30;
    private const double TabTopMargin = 6;       // space above tab content
    private const double TabPaddingLeft = 10;    // left text padding
    private const double TabPaddingRight = 4;    // right padding after close btn
    private const double TabMaxWidth = 280;
    private const double CloseButtonSize = 24;   // hit area for close "×"
    private const double CornerRadius = 4;       // convex top corners
    private const double ConcaveRadius = 6;      // concave bottom curves
    private const double PlusButtonWidth = 28;   // "+" button area
    private const double PlusButtonHeight = 24;  // "+" hover bg height
    private const double IconSize = 24;
    private const double IconLeftMargin = 6;
    private const double TabGap = 2;             // gap between tabs
    private const double CaptionButtonsReserved = 140; // space for min/max/close

    private static readonly Typeface TabFont = new("Segoe UI, Inter, sans-serif");
    private static readonly Typeface TabFontBold = new("Segoe UI, Inter, sans-serif",
        FontStyle.Normal, FontWeight.SemiBold);
    private const double TabFontSize = 12;
    private const double CloseFontSize = 18;
    private const double PlusFontSize = 18;

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private EditorTheme _theme = EditorTheme.Light;

    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // App icon
    // -------------------------------------------------------------------------

    private Bitmap? _appIcon;

    private void EnsureIcon() {
        if (_appIcon != null) return;
        try {
            var uri = new Uri("avares://dmedit/app_icon.ico");
            _appIcon = new Bitmap(Avalonia.Platform.AssetLoader.Open(uri));
        } catch {
            // Icon not found — draw nothing
        }
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private IReadOnlyList<TabState> _tabs = Array.Empty<TabState>();
    private TabState? _activeTab;
    private double[] _tabWidths = Array.Empty<double>();
    private double[] _tabXPositions = Array.Empty<double>();
    private double _contentStartX; // x after icon

    private enum HitZone { None, Tab, CloseButton, PlusButton, OverflowButton, DragArea }
    private HitZone _hoverZone = HitZone.None;
    private int _hoverTabIndex = -1;

    // Overflow state — set by ComputeTabLayout
    private int _visibleTabCount;         // how many tabs fit in the bar
    private double _overflowButtonX;      // x position of the ▼ button
    private const double OverflowButtonWidth = 28;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    public event Action<int>? TabClicked;
    public event Action<int>? TabCloseClicked;
    public event Action? PlusClicked;
    public event Action? DragAreaPressed;
    public event Action? OverflowClicked;

    /// <summary>
    /// The last PointerPressedEventArgs, available for the DragAreaPressed
    /// handler to pass to <see cref="Window.BeginMoveDrag"/>.
    /// </summary>
    public PointerPressedEventArgs? LastPointerPressedArgs { get; private set; }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void Update(IReadOnlyList<TabState> tabs, TabState? active) {
        _tabs = tabs;
        _activeTab = active;
        ComputeTabLayout();
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // Layout computation
    // -------------------------------------------------------------------------

    private void ComputeTabLayout() {
        _contentStartX = IconLeftMargin + IconSize + 8;
        _tabWidths = new double[_tabs.Count];
        _tabXPositions = new double[_tabs.Count];

        if (_tabs.Count == 0) {
            _visibleTabCount = 0;
            return;
        }

        // 1. Measure natural width for each tab (text + close + padding)
        var naturalWidths = new double[_tabs.Count];
        for (var i = 0; i < _tabs.Count; i++) {
            var text = GetTabLabel(i);
            var isActive = _tabs[i] == _activeTab;
            var typeface = isActive ? TabFontBold : TabFont;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, TabFontSize, Brushes.Black);
            var w = ft.Width + TabPaddingLeft + CloseButtonSize + TabPaddingRight + 8;
            naturalWidths[i] = Math.Min(w, TabMaxWidth);
        }

        // 2. Available space = window width − icon area − plus button − caption buttons
        var availableWidth = Bounds.Width - _contentStartX - PlusButtonWidth - 16
                             - CaptionButtonsReserved;
        if (availableWidth < 1) availableWidth = 1;

        // 3. Try fitting all tabs at natural widths
        var totalNatural = 0.0;
        var totalGaps = (_tabs.Count - 1) * TabGap;
        for (var i = 0; i < _tabs.Count; i++) {
            totalNatural += naturalWidths[i];
        }

        if (totalNatural + totalGaps <= availableWidth) {
            // Everything fits at natural size — no shrinking needed
            _visibleTabCount = _tabs.Count;
            for (var i = 0; i < _tabs.Count; i++) {
                _tabWidths[i] = naturalWidths[i];
            }
        } else {
            // 4. Tabs don't fit — overflow rightmost tabs until the visible
            //    set fits at natural widths (no shrinking, no truncation).
            var visibleCount = _tabs.Count;
            while (visibleCount > 1) {
                visibleCount--;
                var gaps = (visibleCount - 1) * TabGap;
                var overflowReserve = OverflowButtonWidth + TabGap;
                var spaceForTabs = availableWidth - gaps - overflowReserve;

                var sumNatural = 0.0;
                for (var i = 0; i < visibleCount; i++) {
                    sumNatural += naturalWidths[i];
                }

                if (sumNatural <= spaceForTabs) {
                    break;
                }
            }

            _visibleTabCount = visibleCount;
            for (var i = 0; i < visibleCount; i++) {
                _tabWidths[i] = naturalWidths[i];
            }
        }

        // 5. Compute x positions for visible tabs
        var x = _contentStartX;
        for (var i = 0; i < _visibleTabCount; i++) {
            _tabXPositions[i] = x;
            x += _tabWidths[i] + TabGap;
        }

        // 6. Overflow button position (if needed)
        _overflowButtonX = x + 4;
    }

    private string GetTabLabel(int index) {
        var tab = _tabs[index];
        var dirty = tab.IsDirty ? "\u2022 " : "";
        return $"{dirty}{tab.DisplayName}";
    }

    // -------------------------------------------------------------------------
    // Hit testing
    // -------------------------------------------------------------------------

    private (HitZone zone, int tabIndex) HitTest(Point pt) {
        var x = pt.X;
        var y = pt.Y;

        // Check "+" button
        var plusX = GetPlusButtonX();
        if (x >= plusX && x < plusX + PlusButtonWidth && y >= TabTopMargin) {
            return (HitZone.PlusButton, -1);
        }

        // Check overflow button (if visible)
        if (HasOverflow && x >= _overflowButtonX
            && x < _overflowButtonX + OverflowButtonWidth && y >= TabTopMargin) {
            return (HitZone.OverflowButton, -1);
        }

        // Check visible tabs (reverse order so overlapping active tab wins)
        for (var i = _visibleTabCount - 1; i >= 0; i--) {
            var tabX = _tabXPositions[i];
            var tabW = _tabWidths[i];
            if (x >= tabX && x < tabX + tabW && y >= TabTopMargin) {
                // Check close button area (right side of tab)
                var closeX = tabX + tabW - CloseButtonSize - TabPaddingRight;
                var closeY = TabTopMargin + (TabHeight - CloseButtonSize) / 2;
                if (x >= closeX && x < closeX + CloseButtonSize
                    && y >= closeY && y < closeY + CloseButtonSize) {
                    // Show close on active tab or hovered tab
                    if (_tabs[i] == _activeTab || _hoverTabIndex == i) {
                        return (HitZone.CloseButton, i);
                    }
                }
                return (HitZone.Tab, i);
            }
        }

        // Everything else is drag area (for moving the window)
        return (HitZone.DragArea, -1);
    }

    /// <summary>Whether any tabs are overflowed into the dropdown.</summary>
    private bool HasOverflow => _visibleTabCount < _tabs.Count;

    /// <summary>Returns the list of tab indices that are in the overflow menu.</summary>
    public IReadOnlyList<(int index, string label)> GetOverflowTabs() {
        var result = new List<(int, string)>();
        for (var i = _visibleTabCount; i < _tabs.Count; i++) {
            result.Add((i, GetTabLabel(i)));
        }
        return result;
    }

    private double GetPlusButtonX() {
        if (_tabs.Count == 0) return _contentStartX;
        if (HasOverflow) {
            return _overflowButtonX + OverflowButtonWidth + TabGap + 4;
        }
        var last = _visibleTabCount - 1;
        return _tabXPositions[last] + _tabWidths[last] + TabGap + 4;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext ctx) {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 1 || h < 1) return;

        // Background
        ctx.FillRectangle(_theme.TabBarBackground, new Rect(0, 0, w, h));

        // Draw app icon
        EnsureIcon();
        if (_appIcon != null) {
            var iconY = (h - IconSize) / 2;
            ctx.DrawImage(_appIcon, new Rect(IconLeftMargin, iconY, IconSize, IconSize));
        }

        if (_tabs.Count == 0) {
            DrawPlusButton(ctx, _contentStartX);
            return;
        }

        // Draw visible inactive tabs first, then active tab on top
        var activeIndex = -1;
        for (var i = 0; i < _visibleTabCount; i++) {
            if (_tabs[i] == _activeTab) {
                activeIndex = i;
                continue;
            }
            DrawInactiveTab(ctx, i);
        }

        // Draw separators on right edge of visible inactive tabs
        for (var i = 0; i < _visibleTabCount; i++) {
            if (_tabs[i] == _activeTab) continue;
            if (_hoverTabIndex == i && _hoverZone is HitZone.Tab or HitZone.CloseButton) continue;
            if (i + 1 < _visibleTabCount && _tabs[i + 1] == _activeTab) continue;
            if (i + 1 < _visibleTabCount && _hoverTabIndex == i + 1
                && _hoverZone is HitZone.Tab or HitZone.CloseButton) continue;

            var sepX = _tabXPositions[i] + _tabWidths[i] + TabGap / 2;
            var sepPen = new Pen(_theme.TabBorder, 1);
            ctx.DrawLine(sepPen, new Point(sepX, TabTopMargin + 6),
                new Point(sepX, TabTopMargin + TabHeight - 6));
        }

        // Draw active tab on top
        if (activeIndex >= 0) {
            DrawActiveTab(ctx, activeIndex);
        }

        // Draw overflow button if needed
        if (HasOverflow) {
            DrawOverflowButton(ctx);
        }

        // Draw "+" button
        DrawPlusButton(ctx, GetPlusButtonX());
    }

    private void DrawInactiveTab(DrawingContext ctx, int index) {
        var x = _tabXPositions[index];
        var tabW = _tabWidths[index];
        var isHovered = _hoverTabIndex == index && _hoverZone is HitZone.Tab or HitZone.CloseButton;
        var bottom = TabTopMargin + TabHeight;

        if (isHovered) {
            // Hovered inactive: same background as active tab, but with a
            // bottom border line so it doesn't appear to flow into the panel
            // below. Square bottom corners.
            var hoverRect = new Rect(x, TabTopMargin, tabW, TabHeight);
            // Top has rounded corners, bottom is square
            var hoverGeo = CreateTopRoundedRect(hoverRect, CornerRadius);
            ctx.DrawGeometry(_theme.TabActiveBackground, null, hoverGeo);

            // Bottom border line to separate from content area
            var borderPen = new Pen(_theme.TabBorder, 1);
            ctx.DrawLine(borderPen, new Point(x, bottom), new Point(x + tabW, bottom));
        }

        // Label
        var label = GetTabLabel(index);
        var maxTextW = tabW - TabPaddingLeft - CloseButtonSize - TabPaddingRight - 4;
        var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFont, TabFontSize, _theme.TabForeground) {
            MaxTextWidth = Math.Max(1, maxTextW),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;
        ctx.DrawText(ft, new Point(x + TabPaddingLeft, textY));

        // Close button on hover
        if (isHovered) {
            DrawCloseButton(ctx, index);
        }
    }

    private void DrawActiveTab(DrawingContext ctx, int index) {
        var x = _tabXPositions[index];
        var tabW = _tabWidths[index];
        var bottom = TabTopMargin + TabHeight;

        // Draw the shaped tab path with concave bottom curves
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) {
            // Start at bottom-left, outside the tab (on the bar surface)
            sgc.BeginFigure(new Point(x - ConcaveRadius, bottom), true);

            // Concave curve bottom-left: arc from bar surface up to tab bottom-left
            sgc.ArcTo(
                new Point(x, bottom - ConcaveRadius),
                new Size(ConcaveRadius, ConcaveRadius),
                0, false, SweepDirection.CounterClockwise);

            // Line up to top-left corner area
            sgc.LineTo(new Point(x, TabTopMargin + CornerRadius));

            // Convex top-left corner
            sgc.ArcTo(
                new Point(x + CornerRadius, TabTopMargin),
                new Size(CornerRadius, CornerRadius),
                0, false, SweepDirection.Clockwise);

            // Line across the top
            sgc.LineTo(new Point(x + tabW - CornerRadius, TabTopMargin));

            // Convex top-right corner
            sgc.ArcTo(
                new Point(x + tabW, TabTopMargin + CornerRadius),
                new Size(CornerRadius, CornerRadius),
                0, false, SweepDirection.Clockwise);

            // Line down to bottom-right corner area
            sgc.LineTo(new Point(x + tabW, bottom - ConcaveRadius));

            // Concave curve bottom-right: arc from tab right down to bar surface
            sgc.ArcTo(
                new Point(x + tabW + ConcaveRadius, bottom),
                new Size(ConcaveRadius, ConcaveRadius),
                0, false, SweepDirection.CounterClockwise);

            sgc.EndFigure(true);
        }
        ctx.DrawGeometry(_theme.TabActiveBackground, null, geo);

        // Label (bold for active tab)
        var label = GetTabLabel(index);
        var maxTextW = tabW - TabPaddingLeft - CloseButtonSize - TabPaddingRight - 4;
        var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFontBold, TabFontSize, _theme.TabForeground) {
            MaxTextWidth = Math.Max(1, maxTextW),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;
        ctx.DrawText(ft, new Point(x + TabPaddingLeft, textY));

        // Close button (always visible on active tab)
        DrawCloseButton(ctx, index);
    }

    private void DrawCloseButton(DrawingContext ctx, int index) {
        var x = _tabXPositions[index];
        var tabW = _tabWidths[index];
        var closeX = x + tabW - CloseButtonSize - TabPaddingRight;
        var closeY = TabTopMargin + (TabHeight - CloseButtonSize) / 2;

        // Hover highlight — square with small rounded corners
        if (_hoverZone == HitZone.CloseButton && _hoverTabIndex == index) {
            var hoverRect = new Rect(closeX, closeY, CloseButtonSize, CloseButtonSize);
            var hoverGeo = CreateRoundedRect(hoverRect, 4);
            ctx.DrawGeometry(_theme.TabCloseHoverBg, null, hoverGeo);
        }

        // Draw "×" character, sized to fill the button well
        var closeFt = new FormattedText("\u00D7",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFont, CloseFontSize, _theme.TabCloseForeground);
        var cx = closeX + (CloseButtonSize - closeFt.Width) / 2;
        var cy = closeY + (CloseButtonSize - closeFt.Height) / 2;
        ctx.DrawText(closeFt, new Point(cx, cy));
    }

    private void DrawPlusButton(DrawingContext ctx, double x) {
        var isHovered = _hoverZone == HitZone.PlusButton;

        // Measure the "+" text first so we can center the hover bg around it
        var ft = new FormattedText("+",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFont, PlusFontSize, _theme.TabPlusForeground);
        var textX = x + (PlusButtonWidth - ft.Width) / 2;
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;

        if (isHovered) {
            // Center the hover rect around the actual "+" text position
            var hoverY = textY - 2;
            var hoverH = ft.Height + 4;
            var hoverRect = new Rect(x, hoverY, PlusButtonWidth, hoverH);
            var hoverGeo = CreateRoundedRect(hoverRect, 4);
            ctx.DrawGeometry(_theme.TabInactiveHoverBg, null, hoverGeo);
        }

        ctx.DrawText(ft, new Point(textX, textY));
    }

    private void DrawOverflowButton(DrawingContext ctx) {
        var isHovered = _hoverZone == HitZone.OverflowButton;

        var ft = new FormattedText("\u25BC",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFont, TabFontSize, _theme.TabPlusForeground);
        var textX = _overflowButtonX + (OverflowButtonWidth - ft.Width) / 2;
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;

        if (isHovered) {
            var hoverY = textY - 2;
            var hoverH = ft.Height + 4;
            var hoverRect = new Rect(_overflowButtonX, hoverY, OverflowButtonWidth, hoverH);
            var hoverGeo = CreateRoundedRect(hoverRect, 4);
            ctx.DrawGeometry(_theme.TabInactiveHoverBg, null, hoverGeo);
        }

        ctx.DrawText(ft, new Point(textX, textY));
    }

    /// <summary>
    /// Creates a rounded rectangle where only the top corners are rounded
    /// and the bottom corners are square.
    /// </summary>
    private static StreamGeometry CreateTopRoundedRect(Rect rect, double radius) {
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) {
            // Start at top-left (after radius)
            sgc.BeginFigure(new Point(rect.Left + radius, rect.Top), true);
            // Top edge
            sgc.LineTo(new Point(rect.Right - radius, rect.Top));
            // Top-right rounded corner
            sgc.ArcTo(new Point(rect.Right, rect.Top + radius),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            // Right edge straight down
            sgc.LineTo(new Point(rect.Right, rect.Bottom));
            // Bottom edge straight across (square corners)
            sgc.LineTo(new Point(rect.Left, rect.Bottom));
            // Left edge straight up
            sgc.LineTo(new Point(rect.Left, rect.Top + radius));
            // Top-left rounded corner
            sgc.ArcTo(new Point(rect.Left + radius, rect.Top),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.EndFigure(true);
        }
        return geo;
    }

    private static StreamGeometry CreateRoundedRect(Rect rect, double radius) {
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) {
            sgc.BeginFigure(new Point(rect.Left + radius, rect.Top), true);
            sgc.LineTo(new Point(rect.Right - radius, rect.Top));
            sgc.ArcTo(new Point(rect.Right, rect.Top + radius),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(rect.Right, rect.Bottom - radius));
            sgc.ArcTo(new Point(rect.Right - radius, rect.Bottom),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(rect.Left + radius, rect.Bottom));
            sgc.ArcTo(new Point(rect.Left, rect.Bottom - radius),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(rect.Left, rect.Top + radius));
            sgc.ArcTo(new Point(rect.Left + radius, rect.Top),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.EndFigure(true);
        }
        return geo;
    }

    // -------------------------------------------------------------------------
    // Pointer events
    // -------------------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        var (zone, idx) = HitTest(pt);
        if (zone != _hoverZone || idx != _hoverTabIndex) {
            _hoverZone = zone;
            _hoverTabIndex = idx;
            Cursor = zone == HitZone.DragArea
                ? Cursor.Default
                : new Cursor(StandardCursorType.Arrow);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        _hoverZone = HitZone.None;
        _hoverTabIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        LastPointerPressedArgs = e;
        var pt = e.GetPosition(this);
        var (zone, idx) = HitTest(pt);

        switch (zone) {
            case HitZone.Tab:
                TabClicked?.Invoke(idx);
                e.Handled = true;
                break;
            case HitZone.CloseButton:
                TabCloseClicked?.Invoke(idx);
                e.Handled = true;
                break;
            case HitZone.PlusButton:
                PlusClicked?.Invoke();
                e.Handled = true;
                break;
            case HitZone.OverflowButton:
                OverflowClicked?.Invoke();
                e.Handled = true;
                break;
            case HitZone.DragArea:
                DragAreaPressed?.Invoke();
                e.Handled = true;
                break;
        }
    }
}
