using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Services;

namespace DMEdit.App.Controls;

/// <summary>
/// Describes a single toolbar button — either a simple command or a toggle.
/// </summary>
public sealed class ToolbarItem {
    public required string CommandId { get; init; }
    public required string Glyph { get; init; }
    public required string Tooltip { get; init; }
    public bool IsToggle { get; init; }
    public Func<bool>? IsChecked { get; init; }
}

/// <summary>
/// Custom-drawn toolbar that renders a horizontal row of icon buttons.
/// When the available width is too narrow, excess buttons overflow behind
/// a chevron-down dropdown — the same pattern used by <see cref="TabBarControl"/>
/// for tabs that don't fit.
/// </summary>
public sealed class ToolbarControl : Control {
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const double ButtonSize = 26;
    private const double ButtonGap = 2;
    private const double OverflowBtnWidth = 24;
    private const double IconFontSize = 18;
    private const double CheckFontSize = 8;
    private const double CornerRadius = 4;

    private static readonly Typeface IconFont = IconGlyphs.Face;

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private EditorTheme _theme = EditorTheme.Light;
    private IBrush _foreground = Brushes.Black;
    // Match ButtonTheme.axaml: ButtonBackgroundPointerOver
    private IBrush _hoverBg = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    private IBrush _toggleBg = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7));
    // Match ButtonTheme.axaml: ButtonForegroundDisabled
    private IBrush _disabledFg = new SolidColorBrush(Color.FromRgb(0x89, 0x89, 0x89));

    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        _foreground = theme.TabPlusForeground;
        // Match ButtonTheme.axaml exactly
        _hoverBg = theme == EditorTheme.Dark
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
        _toggleBg = theme == EditorTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(0x40, 0x33, 0x99, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7));
        _disabledFg = theme == EditorTheme.Dark
            ? new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A))
            : new SolidColorBrush(Color.FromRgb(0x89, 0x89, 0x89));
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private IReadOnlyList<ToolbarItem> _items = Array.Empty<ToolbarItem>();
    private int _visibleCount;     // how many buttons fit (left-to-right)
    private bool _showOverflow;    // true when some buttons are hidden
    private double _contentOffsetX; // horizontal offset to center buttons
    private int _hoverIndex = -1;  // -1 = none, Count = overflow button

    /// <summary>Fires when the overflow chevron is clicked.</summary>
    public event Action? OverflowClicked;

    /// <summary>Fires when a toolbar button is clicked (command ID).</summary>
    public event Action<string>? ButtonClicked;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void SetItems(IReadOnlyList<ToolbarItem> items) {
        _items = items;
        ComputeLayout();
        InvalidateVisual();
    }

    /// <summary>
    /// Returns the overflow button's screen-relative rect so MainWindow can
    /// anchor a context menu to it.
    /// </summary>
    public Rect OverflowButtonRect {
        get {
            var x = _contentOffsetX + _visibleCount * (ButtonSize + ButtonGap);
            return new Rect(x, 0, OverflowBtnWidth, Bounds.Height);
        }
    }

    /// <summary>
    /// Returns items that are not visible (overflow items).
    /// </summary>
    public List<ToolbarItem> GetOverflowItems() {
        var result = new List<ToolbarItem>();
        for (var i = _visibleCount; i < _items.Count; i++) {
            result.Add(_items[i]);
        }
        return result;
    }

    /// <summary>
    /// Call after toggling a setting to repaint checked state.
    /// </summary>
    public void Refresh() => InvalidateVisual();

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    public ToolbarControl() {
        PropertyChanged += (_, e) => {
            if (e.Property == BoundsProperty && _items.Count > 0) {
                ComputeLayout();
                InvalidateVisual();
            }
        };
    }

    private void ComputeLayout() {
        if (_items.Count == 0) {
            _visibleCount = 0;
            _showOverflow = false;
            _contentOffsetX = 0;
            return;
        }

        var available = Bounds.Width;
        var totalNeeded = _items.Count * (ButtonSize + ButtonGap) - ButtonGap;

        if (totalNeeded <= available) {
            _visibleCount = _items.Count;
            _showOverflow = false;
            // Center the buttons within the available width
            _contentOffsetX = Math.Floor((available - totalNeeded) / 2);
        } else {
            // Reserve space for overflow button
            var usable = available - OverflowBtnWidth - ButtonGap;
            _visibleCount = Math.Max(0, (int)((usable + ButtonGap) / (ButtonSize + ButtonGap)));
            _showOverflow = true;
            // Center the visible buttons + overflow within available width
            var usedWidth = _visibleCount * (ButtonSize + ButtonGap) + OverflowBtnWidth;
            _contentOffsetX = Math.Floor((available - usedWidth) / 2);
        }
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext ctx) {
        base.Render(ctx);
        // Fill the full bounds so Avalonia treats the entire surface as hit-testable.
        // Without this, pointer events only fire over rendered glyph pixels.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_items.Count == 0) return;

        var h = Bounds.Height;
        var y = (h - ButtonSize) / 2;

        for (var i = 0; i < _visibleCount; i++) {
            var x = _contentOffsetX + i * (ButtonSize + ButtonGap);
            DrawButton(ctx, i, x, y);
        }

        if (_showOverflow) {
            var ox = _contentOffsetX + _visibleCount * (ButtonSize + ButtonGap);
            DrawOverflowButton(ctx, ox, y);
        }
    }

    private void DrawButton(DrawingContext ctx, int index, double x, double y) {
        var item = _items[index];
        var isEnabled = Cmd.TryGet(item.CommandId)?.IsEnabled ?? true;
        var isHovered = _hoverIndex == index && isEnabled;
        var isChecked = item.IsToggle && item.IsChecked?.Invoke() == true;

        // Background: toggle checked bg, then hover bg (only when enabled)
        if (isChecked) {
            var geo = CreateRoundedRect(new Rect(x, y, ButtonSize, ButtonSize), CornerRadius);
            ctx.DrawGeometry(_toggleBg, null, geo);
        }
        if (isHovered) {
            var geo = CreateRoundedRect(new Rect(x, y, ButtonSize, ButtonSize), CornerRadius);
            ctx.DrawGeometry(_hoverBg, null, geo);
        }

        // Icon glyph
        var fg = isEnabled ? _foreground : _disabledFg;
        var ft = new FormattedText(item.Glyph, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconFont, IconFontSize, fg);
        ctx.DrawText(ft, new Point(
            x + (ButtonSize - ft.Width) / 2,
            y + (ButtonSize - ft.Height) / 2));
    }

    private void DrawOverflowButton(DrawingContext ctx, double x, double y) {
        var isHovered = _hoverIndex == _items.Count; // sentinel value
        if (isHovered) {
            var geo = CreateRoundedRect(new Rect(x, y, OverflowBtnWidth, ButtonSize), CornerRadius);
            ctx.DrawGeometry(_hoverBg, null, geo);
        }
        var ft = new FormattedText(IconGlyphs.ChevronDown, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconFont, IconFontSize, _foreground);
        ctx.DrawText(ft, new Point(
            x + (OverflowBtnWidth - ft.Width) / 2,
            y + (ButtonSize - ft.Height) / 2));
    }

    // -------------------------------------------------------------------------
    // Pointer handling
    // -------------------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var old = _hoverIndex;
        _hoverIndex = HitTest(pos);
        if (_hoverIndex != old) {
            InvalidateVisual();
            UpdateTooltip();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hoverIndex != -1) {
            _hoverIndex = -1;
            InvalidateVisual();
            ToolTip.SetTip(this, null);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        var idx = HitTest(pos);

        if (idx >= 0 && idx < _visibleCount) {
            ButtonClicked?.Invoke(_items[idx].CommandId);
            InvalidateVisual();
        } else if (idx == _items.Count && _showOverflow) {
            OverflowClicked?.Invoke();
        }
    }

    /// <summary>
    /// Returns button index, or _items.Count for overflow button, or -1 for none.
    /// </summary>
    private int HitTest(Point pos) {
        var h = Bounds.Height;
        var y = (h - ButtonSize) / 2;
        if (pos.Y < y || pos.Y > y + ButtonSize) return -1;

        for (var i = 0; i < _visibleCount; i++) {
            var x = _contentOffsetX + i * (ButtonSize + ButtonGap);
            if (pos.X >= x && pos.X < x + ButtonSize) return i;
        }

        if (_showOverflow) {
            var ox = _contentOffsetX + _visibleCount * (ButtonSize + ButtonGap);
            if (pos.X >= ox && pos.X < ox + OverflowBtnWidth) return _items.Count;
        }

        return -1;
    }

    private void UpdateTooltip() {
        if (_hoverIndex >= 0 && _hoverIndex < _items.Count) {
            ToolTip.SetTip(this, _items[_hoverIndex].Tooltip);
        } else if (_hoverIndex == _items.Count) {
            ToolTip.SetTip(this, "More");
        } else {
            ToolTip.SetTip(this, null);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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
}
