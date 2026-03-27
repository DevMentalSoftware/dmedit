using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Controls;

/// <summary>
/// Search mode for the find bar: plain text, simple wildcards, or full regex.
/// </summary>
public enum SearchMode {
    Normal,
    Wildcard,
    Regex,
}

public partial class FindBarControl : UserControl {
    // Events (stubs — no search logic yet)
    public event Action<bool>? FindRequested;
    public event Action? CloseRequested;
    public event Action<string>? ReplaceRequested;
    public event Action? ReplaceAllRequested;
    public event Action? SearchTermChanged;
    public event Action<double>? Resized;

    public string SearchTerm => SearchBox.Text ?? "";
    public string ReplaceTerm => ReplaceBox.Text ?? "";
    public bool MatchCase => MatchCaseBtn.IsChecked == true;
    public bool WholeWord => WholeWordBtn.IsChecked == true;

    public SearchMode SearchMode {
        get => WildcardBtn.IsChecked == true ? SearchMode.Wildcard :
               RegexBtn.IsChecked == true ? SearchMode.Regex :
               SearchMode.Normal;
        set {
            WildcardBtn.IsChecked = value == SearchMode.Wildcard;
            RegexBtn.IsChecked = value == SearchMode.Regex;
        }
    }

    private bool _isReplaceMode;
    public bool IsReplaceMode {
        get => _isReplaceMode;
        set {
            _isReplaceMode = value;
            ApplyReplaceMode();
        }
    }

    // Direction tracking: true = forward (right arrow), false = backward (left arrow).
    private bool _lastForward = true;

    // Resize drag state (left-edge grip).  Coordinates are in VisualRoot
    // space so they stay stable as the right-aligned control changes width.
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;

    public FindBarControl() {
        InitializeComponent();

        // Wire events
        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
        ReplaceBtn.Click += (_, _) => {
            ReplaceRequested?.Invoke(ReplaceTerm);
            SearchBox.InnerTextBox?.Focus();
        };
        ReplaceAllBtn.Click += (_, _) => {
            ReplaceAllRequested?.Invoke();
            SearchBox.InnerTextBox?.Focus();
        };

        // Direction button: find in current direction
        FindDirectionBtn.Click += (_, _) => {
            FindRequested?.Invoke(_lastForward);
            SearchBox.InnerTextBox?.Focus();
        };

        // Menu button: open flyout with Find Next / Find Previous
        var menuFlyout = new MenuFlyout();
        var findNextItem = new MenuItem { Header = "Find Next" };
        var findPrevItem = new MenuItem { Header = "Find Previous" };
        findNextItem.Click += (_, _) => {
            SetDirection(true);
            FindRequested?.Invoke(true);
            SearchBox.InnerTextBox?.Focus();
        };
        findPrevItem.Click += (_, _) => {
            SetDirection(false);
            FindRequested?.Invoke(false);
            SearchBox.InnerTextBox?.Focus();
        };
        menuFlyout.Items.Add(findNextItem);
        menuFlyout.Items.Add(findPrevItem);
        FindMenuBtn.Flyout = menuFlyout;

        ExpandBtn.Click += (_, _) => {
            _isReplaceMode = !_isReplaceMode;
            ApplyReplaceMode();
        };

        // Wildcard / Regex toggles: mutual exclusion — only one active at a time.
        WildcardBtn.Click += (_, _) => {
            if (WildcardBtn.IsChecked == true) RegexBtn.IsChecked = false;
            SearchTermChanged?.Invoke();
        };
        RegexBtn.Click += (_, _) => {
            if (RegexBtn.IsChecked == true) WildcardBtn.IsChecked = false;
            SearchTermChanged?.Invoke();
        };

        // Search text changed → raise event
        SearchBox.PropertyChanged += (_, e) => {
            if (e.Property == DMEditableCombo.TextProperty) {
                SearchTermChanged?.Invoke();
            }
        };

        // Keyboard shortcuts within the text boxes — InnerTextBox is null
        // until OnApplyTemplate runs, so defer wiring.
        SearchBox.TemplateApplied += (_, _) => {
            if (SearchBox.InnerTextBox != null)
                SearchBox.InnerTextBox.KeyDown += OnSearchBoxKeyDown;
        };
        ReplaceBox.TemplateApplied += (_, _) => {
            if (ReplaceBox.InnerTextBox != null)
                ReplaceBox.InnerTextBox.KeyDown += OnReplaceBoxKeyDown;
        };

        // Left-edge resize grip drag
        ResizeGrip.PointerPressed += OnResizePointerPressed;
        ResizeGrip.PointerMoved += OnResizePointerMoved;
        ResizeGrip.PointerReleased += OnResizePointerReleased;
    }

    /// <summary>
    /// Resets direction state and closes history popups. Called when the find
    /// bar is opened (or re-opened on a different tab).
    /// </summary>
    public void ResetState() {
        _lastForward = true;
        DirectionGlyph.Text = IconGlyphs.ArrowRight;
        SearchBox.ClosePopup();
        ReplaceBox.ClosePopup();
    }

    public void FocusSearchBox() {
        if (SearchBox.InnerTextBox is { } tb) {
            tb.Focus();
            tb.SelectAll();
        }
    }

    public void SetSearchTerm(string text) {
        SearchBox.Text = text;
    }

    public void SetReplaceTerm(string text) {
        ReplaceBox.Text = text;
    }

    public void SetMatchInfo(int current, int total, bool capped = false) {
        if (total == 0) {
            MatchCount.Text = "";
        } else if (capped) {
            MatchCount.Text = current > 0 ? $"{current} / {total}+" : $"{total}+";
        } else {
            MatchCount.Text = $"{current} / {total}";
        }
    }

    public void ClearMatchInfo() {
        MatchCount.Text = "";
    }

    public void ApplyTheme(EditorTheme theme) {
        OuterBorder.Background = theme.TabActiveBackground;
        // Concave ear corners + top strip share the same bg as the body
        EarLeft.Fill = theme.TabActiveBackground;
        EarRight.Fill = theme.TabActiveBackground;
        TopStrip.Background = theme.TabActiveBackground;
        MatchCount.Foreground = theme.SettingsDimForeground;
        OptionsSeparator.Background = theme.TabBarBorder;
        OptionsSeparator2.Background = theme.TabBarBorder;

        // Button hover/pressed colors — match the tab bar's DrawIconButton
        // hover background so all toolbar-style buttons look consistent.
        var hoverBrush = theme.TabInactiveHoverBg;
        var hoverColor = ((ISolidColorBrush)hoverBrush).Color;
        double factor = hoverColor.R > 128 ? 0.88 : 1.15;
        var pressedBrush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Clamp(hoverColor.R * factor, 0, 255),
            (byte)Math.Clamp(hoverColor.G * factor, 0, 255),
            (byte)Math.Clamp(hoverColor.B * factor, 0, 255)));
        Resources["ButtonBackgroundPointerOver"] = hoverBrush;
        Resources["ButtonBackgroundPressed"] = pressedBrush;
        Resources["ToggleButtonBackgroundPointerOver"] = hoverBrush;
        Resources["ToggleButtonBackgroundPressed"] = pressedBrush;
    }

    // -----------------------------------------------------------------
    // Direction tracking
    // -----------------------------------------------------------------

    private void SetDirection(bool forward) {
        _lastForward = forward;
        DirectionGlyph.Text = _lastForward ? IconGlyphs.ArrowRight : IconGlyphs.ArrowLeft;
    }

    private void ApplyReplaceMode() {
        ReplaceBox.IsVisible = _isReplaceMode;
        ReplaceBox.IsTabStop = _isReplaceMode;
        ReplaceButtons.IsVisible = _isReplaceMode;
        ReplaceBtn.IsTabStop = _isReplaceMode;
        ReplaceAllBtn.IsTabStop = _isReplaceMode;
        UpdateExpandGlyph();
    }

    private void UpdateExpandGlyph() {
        // Down chevron when expanded, right chevron when collapsed
        ExpandGlyph.Text = _isReplaceMode ? "\uF2A1" : "\uF2B1";
    }

    // -----------------------------------------------------------------
    // Keyboard handling
    // -----------------------------------------------------------------

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e) {
        // DMEditableCombo handles popup arrow-key/Enter/Escape internally.
        // We only handle find-bar-specific keys here.
        if (SearchBox.IsPopupOpen) return;

        switch (e.Key) {
            case Key.Return:
                var forward = (e.KeyModifiers & KeyModifiers.Shift) == 0;
                SetDirection(forward);
                FindRequested?.Invoke(forward);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceBoxKeyDown(object? sender, KeyEventArgs e) {
        if (ReplaceBox.IsPopupOpen) return;

        switch (e.Key) {
            case Key.Return:
                ReplaceRequested?.Invoke(ReplaceTerm);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    // -----------------------------------------------------------------
    // Horizontal resize via the LEFT-edge grip.  The bar is right-aligned
    // so dragging left increases width.  All coordinates use VisualRoot
    // space so they remain stable as the control changes width.
    // -----------------------------------------------------------------

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
            _resizing = true;
            _resizeStartX = e.GetPosition(VisualRoot as Visual ?? this).X;
            _resizeStartWidth = Bounds.Width;
            e.Pointer.Capture(ResizeGrip);
            e.Handled = true;
        }
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e) {
        if (!_resizing) {
            return;
        }
        var currentX = e.GetPosition(VisualRoot as Visual ?? this).X;
        var dx = currentX - _resizeStartX;
        // Dragging left (negative dx) → wider.
        var newWidth = Math.Max(MinWidth, _resizeStartWidth - dx);
        Width = newWidth;
        e.Handled = true;
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (_resizing) {
            _resizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (Width is > 0 and var w) {
                Resized?.Invoke(w);
            }
        }
    }
}
