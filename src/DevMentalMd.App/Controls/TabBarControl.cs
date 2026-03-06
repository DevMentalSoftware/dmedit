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
    private const double TabComfortWidth = 120; // preferred min when space allows
    private const double CloseButtonSize = 24;   // hit area for close "×"
    private const double CornerRadius = 6;       // convex top corners
    private const double ConcaveRadius = 6;      // concave bottom curves
    private const double PlusButtonWidth = 28;   // "+" button area
    private const double PlusButtonHeight = 24;  // "+" hover bg height
    private const double IconSize = 24;
    private const double IconLeftMargin = 6;
    private const double TabGap = 2;             // gap between tabs
    private const double CaptionButtonsReserved = 140; // space for min/max/close
    private const double TabIconGap = 6;             // gap between tab icon and label

    private static readonly Typeface TabFont = new("Segoe UI, Inter, sans-serif");
    private static readonly Typeface TabFontBold = new("Segoe UI, Inter, sans-serif",
        FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface IconFont = IconGlyphs.Face;
    private const double TabFontSize = 12;
    private const double IconButtonFontSize = 12;
    private const double TabIconFontSize = 16;

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

    // Overflow state — set by ComputeTabLayout.
    // Non-contiguous: any subset of tabs may be visible.
    private bool[] _isVisible = Array.Empty<bool>();
    private double _overflowButtonX;      // x position of the ▼ button
    private const double OverflowButtonWidth = 28;

    // Drag-to-reorder state
    private int _dragTabIndex = -1;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 6; // pixels before drag starts

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    public event Action<int>? TabClicked;
    public event Action<int>? TabCloseClicked;
    public event Action? PlusClicked;
    public event Action? DragAreaPressed;
    public event Action? OverflowClicked;
    public event Action<int>? CloseTabsToRightClicked;
    public event Action<int>? CloseOtherTabsClicked;
    public event Action<int, int>? TabReordered; // (fromIndex, toIndex)

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
            _isVisible = Array.Empty<bool>();
            return;
        }

        // Find active tab index
        var activeIdx = 0;
        for (var i = 0; i < _tabs.Count; i++) {
            if (_tabs[i] == _activeTab) { activeIdx = i; break; }
        }

        // 1. Measure two widths per tab.  Always use the bold typeface so
        //    that widths stay stable when the active tab changes.
        var naturalWidths = new double[_tabs.Count];
        var comfortWidths = new double[_tabs.Count];
        for (var i = 0; i < _tabs.Count; i++) {
            var text = GetTabLabel(i);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, TabFontBold, TabFontSize, Brushes.Black);
            var w = ft.Width + TabPaddingLeft + CloseButtonSize + TabPaddingRight + 8;
            if (_tabs[i].IsSettings) {
                w += IconButtonFontSize + TabIconGap; // gear icon before label
            }
            naturalWidths[i] = w;
            comfortWidths[i] = Math.Max(w, TabComfortWidth);
        }

        // 2. Available space
        var availableWidth = Bounds.Width - _contentStartX - PlusButtonWidth - 16
                             - CaptionButtonsReserved;
        if (availableWidth < 1) availableWidth = 1;

        // 3. Try fitting ALL tabs
        var totalComfort = 0.0;
        var totalNatural = 0.0;
        var totalGaps = (_tabs.Count - 1) * TabGap;
        for (var i = 0; i < _tabs.Count; i++) {
            totalComfort += comfortWidths[i];
            totalNatural += naturalWidths[i];
        }

        if (totalComfort + totalGaps <= availableWidth) {
            _isVisible = new bool[_tabs.Count];
            for (var i = 0; i < _tabs.Count; i++) {
                _isVisible[i] = true;
                _tabWidths[i] = comfortWidths[i];
            }
        } else if (totalNatural + totalGaps <= availableWidth) {
            _isVisible = new bool[_tabs.Count];
            for (var i = 0; i < _tabs.Count; i++) _isVisible[i] = true;
            SizeVisibleTabs(naturalWidths, comfortWidths, availableWidth);
        } else {
            // 4. Overflow needed.
            var availOverflow = availableWidth - OverflowButtonWidth - TabGap;

            // Was the active tab previously visible?
            var prevVisible = _isVisible;
            var activeWasVisible = activeIdx < prevVisible.Length && prevVisible[activeIdx];

            // Build new visibility set
            _isVisible = new bool[_tabs.Count];

            if (activeWasVisible) {
                // Stable mode — keep the same visible set.
                // Copy previous visibility (clamped to new count).
                for (var i = 0; i < _tabs.Count && i < prevVisible.Length; i++) {
                    _isVisible[i] = prevVisible[i];
                }
                // Ensure active is visible (always)
                _isVisible[activeIdx] = true;
            } else {
                // New tab or overflow click — active becomes the rightmost
                // visible tab.  Hide everything with index > activeIdx,
                // keep everything that was visible with index < activeIdx.
                for (var i = 0; i < _tabs.Count; i++) {
                    if (i == activeIdx) {
                        _isVisible[i] = true;
                    } else if (i < activeIdx && i < prevVisible.Length && prevVisible[i]) {
                        _isVisible[i] = true;
                    }
                }
            }

            // Trim: hide rightmost visible non-active tabs until it fits
            while (VisibleNaturalSum(naturalWidths) > availOverflow) {
                var victim = RightmostVisibleExcept(activeIdx);
                if (victim < 0) break;
                _isVisible[victim] = false;
            }

            // Expand: try adding hidden tabs closest to the active tab
            // first, radiating outward (left before right at equal distance).
            var el = activeIdx - 1;
            var er = activeIdx + 1;
            while (el >= 0 || er < _tabs.Count) {
                if (el >= 0) {
                    if (!_isVisible[el]) {
                        var cost = naturalWidths[el] + TabGap;
                        if (VisibleNaturalSum(naturalWidths) + cost <= availOverflow) {
                            _isVisible[el] = true;
                        }
                    }
                    el--;
                }
                if (er < _tabs.Count) {
                    if (!_isVisible[er]) {
                        var cost = naturalWidths[er] + TabGap;
                        if (VisibleNaturalSum(naturalWidths) + cost <= availOverflow) {
                            _isVisible[er] = true;
                        }
                    }
                    er++;
                }
            }

            // Size the visible tabs
            SizeVisibleTabs(naturalWidths, comfortWidths, availOverflow);

            // Last resort: if only the active tab is visible and too wide
            var visCount = VisibleCount();
            if (visCount == 1 && _tabWidths[activeIdx] > availOverflow) {
                _tabWidths[activeIdx] = Math.Max(1, availOverflow);
            }
        }

        // 5. Compute x positions for visible tabs only
        var x = _contentStartX;
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            _tabXPositions[i] = x;
            x += _tabWidths[i] + TabGap;
        }

        // 6. Overflow button position
        _overflowButtonX = x + 4;
    }

    /// <summary>Sum of natural widths + gaps for currently visible tabs.</summary>
    private double VisibleNaturalSum(double[] naturalWidths) {
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            sum += naturalWidths[i];
            count++;
        }
        return sum + Math.Max(0, count - 1) * TabGap;
    }

    /// <summary>Returns the highest-index visible tab that is not activeIdx, or -1.</summary>
    private int RightmostVisibleExcept(int activeIdx) {
        for (var i = _tabs.Count - 1; i >= 0; i--) {
            if (_isVisible[i] && i != activeIdx) return i;
        }
        return -1;
    }

    /// <summary>How many tabs are currently visible.</summary>
    private int VisibleCount() {
        var c = 0;
        for (var i = 0; i < _isVisible.Length; i++) {
            if (_isVisible[i]) c++;
        }
        return c;
    }

    /// <summary>
    /// Sizes visible tabs: comfort widths if they fit, otherwise shrink
    /// proportionally toward natural widths.
    /// </summary>
    private void SizeVisibleTabs(double[] naturalWidths, double[] comfortWidths,
                                 double availableSpace) {
        var count = 0;
        var sumComfort = 0.0;
        var sumNatural = 0.0;
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            sumComfort += comfortWidths[i];
            sumNatural += naturalWidths[i];
            count++;
        }
        var gaps = Math.Max(0, count - 1) * TabGap;
        var spaceForTabs = availableSpace - gaps;

        if (sumComfort <= spaceForTabs) {
            for (var i = 0; i < _tabs.Count; i++) {
                if (_isVisible[i]) _tabWidths[i] = comfortWidths[i];
            }
        } else {
            var surplus = spaceForTabs - sumNatural;
            var totalPadding = sumComfort - sumNatural;
            for (var i = 0; i < _tabs.Count; i++) {
                if (!_isVisible[i]) continue;
                var padding = comfortWidths[i] - naturalWidths[i];
                var share = totalPadding > 0 ? padding / totalPadding : 0;
                _tabWidths[i] = naturalWidths[i] + Math.Max(0, surplus) * share;
            }
        }
    }

    private string GetTabLabel(int index) => _tabs[index].DisplayName;

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
        for (var i = _tabs.Count - 1; i >= 0; i--) {
            if (!_isVisible[i]) continue;
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
    private bool HasOverflow => VisibleCount() < _tabs.Count;

    /// <summary>Bounding rect of the overflow button, for menu placement.</summary>
    public Rect OverflowButtonRect =>
        new(_overflowButtonX, TabTopMargin, OverflowButtonWidth, TabHeight);

    /// <summary>Returns the list of tab indices that are in the overflow menu.</summary>
    public IReadOnlyList<(int index, string label)> GetOverflowTabs() {
        var result = new List<(int, string)>();
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) result.Add((i, GetTabLabel(i)));
        }
        return result;
    }

    private double GetPlusButtonX() {
        if (_tabs.Count == 0) return _contentStartX;
        // Find rightmost visible tab
        var last = -1;
        for (var i = _tabs.Count - 1; i >= 0; i--) {
            if (_isVisible[i]) { last = i; break; }
        }
        if (last < 0) return _contentStartX;
        var afterLast = _tabXPositions[last] + _tabWidths[last] + TabGap + 4;
        return HasOverflow
            ? _overflowButtonX + OverflowButtonWidth + TabGap + 4
            : afterLast;
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
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            if (_tabs[i] == _activeTab) {
                activeIndex = i;
                continue;
            }
            DrawInactiveTab(ctx, i);
        }

        // Draw separators on right edge of visible inactive tabs.
        // Find the "next visible" tab to suppress separators adjacent
        // to the active or hovered tab.
        for (var i = 0; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            if (_tabs[i] == _activeTab) continue;
            if (_hoverTabIndex == i && _hoverZone is HitZone.Tab or HitZone.CloseButton) continue;

            // Find next visible tab after i
            var next = -1;
            for (var j = i + 1; j < _tabs.Count; j++) {
                if (_isVisible[j]) { next = j; break; }
            }
            if (next < 0) continue; // last visible tab — no separator
            if (_tabs[next] == _activeTab) continue;
            if (_hoverTabIndex == next
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
        var bottom = Bounds.Height;

        if (isHovered) {
            // Hovered inactive: same background as active tab, but with a
            // bottom border line so it doesn't appear to flow into the panel
            // below. Square bottom corners.
            var hoverRect = new Rect(x, TabTopMargin, tabW, bottom - TabTopMargin);
            // Top has rounded corners, bottom is square
            var hoverGeo = CreateTopRoundedRect(hoverRect, CornerRadius);
            ctx.DrawGeometry(_theme.TabActiveBackground, null, hoverGeo);

            // Bottom border line to separate from content area
            var borderPen = new Pen(_theme.TabBorder, 1);
            ctx.DrawLine(borderPen, new Point(x, bottom), new Point(x + tabW, bottom));
        }

        // Tab icon (settings gear) + label
        var labelX = x + TabPaddingLeft;
        if (_tabs[index].IsSettings) {
            labelX += DrawTabIcon(ctx, IconGlyphs.Settings, x + TabPaddingLeft,
                _theme.TabForeground);
        }
        var label = GetTabLabel(index);
        var maxTextW = tabW - (labelX - x) - CloseButtonSize - TabPaddingRight - 4;
        var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFont, TabFontSize, _theme.TabForeground) {
            MaxTextWidth = Math.Max(1, maxTextW),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;
        ctx.DrawText(ft, new Point(labelX, textY));

        // Close button: on hover, or dirty dot when tab has unsaved changes
        if (isHovered || _tabs[index].IsDirty) {
            DrawCloseButton(ctx, index);
        }
    }

    private void DrawActiveTab(DrawingContext ctx, int index) {
        var x = _tabXPositions[index];
        var tabW = _tabWidths[index];
        var bottom = Bounds.Height; // extend to control edge so tab touches menu bar

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

        // Tab icon (settings gear) + label (bold for active tab)
        var labelX = x + TabPaddingLeft;
        if (_tabs[index].IsSettings) {
            labelX += DrawTabIcon(ctx, IconGlyphs.Settings, x + TabPaddingLeft,
                _theme.TabForeground);
        }
        var label = GetTabLabel(index);
        var maxTextW = tabW - (labelX - x) - CloseButtonSize - TabPaddingRight - 4;
        var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TabFontBold, TabFontSize, _theme.TabForeground) {
            MaxTextWidth = Math.Max(1, maxTextW),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        var textY = TabTopMargin + (TabHeight - ft.Height) / 2;
        ctx.DrawText(ft, new Point(labelX, textY));

        // Close button (always visible on active tab)
        DrawCloseButton(ctx, index);
    }

    /// <summary>
    /// Draws a small icon glyph in the tab label area and returns the
    /// total horizontal advance (icon width + gap) for label positioning.
    /// </summary>
    private double DrawTabIcon(DrawingContext ctx, string glyph, double x, IBrush fg) {
        var ft = new FormattedText(glyph,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconFont, TabIconFontSize, fg);
        var iconY = TabTopMargin + (TabHeight - ft.Height) / 2;
        ctx.DrawText(ft, new Point(x, iconY));
        return ft.Width + TabIconGap;
    }

    private void DrawIconButton(
        DrawingContext ctx, double x, double y, double w, double h,
        bool isHovered, IBrush hoverBg, string glyph, IBrush foreground) {
        if (isHovered) {
            var hoverGeo = CreateRoundedRect(new Rect(x, y, w, h), 4);
            ctx.DrawGeometry(hoverBg, null, hoverGeo);
        }
        var ft = new FormattedText(glyph,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconFont, IconButtonFontSize, foreground);
        ctx.DrawText(ft, new Point(
            x + (w - ft.Width) / 2,
            y + (h - ft.Height) / 2));
    }

    private void DrawCloseButton(DrawingContext ctx, int index) {
        var x = _tabXPositions[index];
        var tabW = _tabWidths[index];
        var closeX = x + tabW - CloseButtonSize - TabPaddingRight;
        var closeY = TabTopMargin + (TabHeight - CloseButtonSize) / 2;
        var isHoveringClose = _hoverZone == HitZone.CloseButton && _hoverTabIndex == index;
        var isDirty = _tabs[index].IsDirty;

        // Show dirty dot when the tab has unsaved changes and the
        // close button is not being hovered; otherwise show the X.
        var glyph = isDirty && !isHoveringClose ? IconGlyphs.Dirty : IconGlyphs.Close;
        DrawIconButton(ctx, closeX, closeY, CloseButtonSize, CloseButtonSize,
            isHoveringClose, _theme.TabCloseHoverBg, glyph, _theme.TabCloseForeground);
    }

    private void DrawPlusButton(DrawingContext ctx, double x) {
        var y = TabTopMargin + (TabHeight - PlusButtonHeight) / 2;
        DrawIconButton(ctx, x, y, PlusButtonWidth, PlusButtonHeight,
            _hoverZone == HitZone.PlusButton, _theme.TabInactiveHoverBg,
            IconGlyphs.Add, _theme.TabPlusForeground);
    }

    private void DrawOverflowButton(DrawingContext ctx) {
        var y = TabTopMargin + (TabHeight - PlusButtonHeight) / 2;
        DrawIconButton(ctx, _overflowButtonX, y, OverflowButtonWidth, PlusButtonHeight,
            _hoverZone == HitZone.OverflowButton, _theme.TabInactiveHoverBg,
            IconGlyphs.ChevronDown, _theme.TabPlusForeground);
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

        // Tab drag-to-reorder
        if (_dragTabIndex >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
            if (!_isDragging) {
                var dx = Math.Abs(pt.X - _dragStartPoint.X);
                if (dx >= DragThreshold) {
                    _isDragging = true;
                    _hoverZone = HitZone.None;
                    _hoverTabIndex = -1;
                    e.Pointer.Capture(this);
                    ToolTip.SetIsOpen(this, false);
                    ToolTip.SetTip(this, null);
                }
            }
            if (_isDragging) {
                // Find which visible tab slot the pointer is over
                var target = FindDropTarget(pt.X);
                if (target >= 0 && target != _dragTabIndex) {
                    TabReordered?.Invoke(_dragTabIndex, target);
                    _dragTabIndex = target; // track the tab's new index
                }
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
                return;
            }
        }

        // Suppress hover effects while any mouse button is down (e.g.
        // during pre-drag or active drag) so tabs don't highlight.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.GetCurrentPoint(this).Properties.IsRightButtonPressed) {
            return;
        }

        var (zone, idx) = HitTest(pt);
        if (zone != _hoverZone || idx != _hoverTabIndex) {
            _hoverZone = zone;
            _hoverTabIndex = idx;
            Cursor = zone == HitZone.DragArea
                ? Cursor.Default
                : new Cursor(StandardCursorType.Arrow);

            // Show file path tooltip when hovering over a tab
            if (zone is HitZone.Tab or HitZone.CloseButton
                && idx >= 0 && idx < _tabs.Count
                && _tabs[idx].FilePath != null) {
                UiHelpers.SetPathToolTip(this, _tabs[idx].FilePath);
                ToolTip.SetIsOpen(this, true);
            } else {
                ToolTip.SetIsOpen(this, false);
                ToolTip.SetTip(this, null);
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (_isDragging) {
            _isDragging = false;
            e.Pointer.Capture(null);
            Cursor = new Cursor(StandardCursorType.Arrow);
            InvalidateVisual();
        }
        _dragTabIndex = -1;
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_isDragging) return; // pointer is captured — keep dragging
        _hoverZone = HitZone.None;
        _hoverTabIndex = -1;
        _dragTabIndex = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Finds the drop target for a drag-to-reorder operation. Only swaps
    /// when the pointer crosses the center of an immediate visible neighbor,
    /// which provides natural hysteresis and prevents 1px oscillation.
    /// </summary>
    private int FindDropTarget(double x) {
        if (_dragTabIndex < 0 || _dragTabIndex >= _tabs.Count) return -1;

        // Check if pointer crossed the center of the immediate left neighbor
        for (var i = _dragTabIndex - 1; i >= 0; i--) {
            if (!_isVisible[i]) continue;
            var center = _tabXPositions[i] + _tabWidths[i] / 2;
            if (x < center) return i;
            break;
        }

        // Check if pointer crossed the center of the immediate right neighbor
        for (var i = _dragTabIndex + 1; i < _tabs.Count; i++) {
            if (!_isVisible[i]) continue;
            var center = _tabXPositions[i] + _tabWidths[i] / 2;
            if (x > center) return i;
            break;
        }

        return _dragTabIndex;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var pt = e.GetPosition(this);
        var (zone, idx) = HitTest(pt);

        // Right-click context menu on tabs
        if (props.IsRightButtonPressed && zone is HitZone.Tab or HitZone.CloseButton
            && idx >= 0 && idx < _tabs.Count) {
            ShowTabContextMenu(idx, pt);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        LastPointerPressedArgs = e;

        switch (zone) {
            case HitZone.Tab:
                _dragTabIndex = idx;
                _dragStartPoint = pt;
                _isDragging = false;
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

    private void ShowTabContextMenu(int tabIndex, Point position) {
        var menu = new ContextMenu();

        var closeRight = new MenuItem { Header = "Close Tabs to the _Right" };
        closeRight.Click += (_, _) => CloseTabsToRightClicked?.Invoke(tabIndex);
        menu.Items.Add(closeRight);

        var closeOthers = new MenuItem { Header = "Close _Other Tabs" };
        closeOthers.Click += (_, _) => CloseOtherTabsClicked?.Invoke(tabIndex);
        menu.Items.Add(closeOthers);

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(this);
    }
}
