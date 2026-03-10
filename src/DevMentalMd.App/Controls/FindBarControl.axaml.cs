using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
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

    public string SearchTerm => SearchBox.Text ?? "";
    public string ReplaceTerm => ReplaceBox.Text ?? "";
    public bool MatchCase => MatchCaseBtn.IsChecked == true;
    public bool WholeWord => WholeWordBtn.IsChecked == true;

    public SearchMode SearchMode => SearchModeBox.SelectedIndex switch {
        1 => SearchMode.Wildcard,
        2 => SearchMode.Regex,
        _ => SearchMode.Normal,
    };

    private bool _isReplaceMode;
    public bool IsReplaceMode {
        get => _isReplaceMode;
        set {
            _isReplaceMode = value;
            ReplaceBox.IsVisible = value;
            ReplaceButtons.IsVisible = value;
            UpdateExpandGlyph();
        }
    }

    // Resize drag state (left-edge grip).  Coordinates are in VisualRoot
    // space so they stay stable as the right-aligned control changes width.
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;

    public FindBarControl() {
        InitializeComponent();

        // Expand/collapse chevron glyph
        ExpandBtn.Content = new TextBlock {
            Text = "\uF2B1",   // chevron_right_20
            FontFamily = IconGlyphs.Family,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        UpdateExpandGlyph();

        // Close button glyph — centered
        CloseBtn.Content = new TextBlock {
            Text = IconGlyphs.Close,
            FontFamily = IconGlyphs.Family,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Clear button glyphs (small ×)
        SetupClearButton(SearchClearBtn);
        SetupClearButton(ReplaceClearBtn);

        // Wire events
        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
        NextBtn.Click += (_, _) => FindRequested?.Invoke(true);
        PrevBtn.Click += (_, _) => FindRequested?.Invoke(false);
        ReplaceBtn.Click += (_, _) => ReplaceRequested?.Invoke(ReplaceTerm);
        ReplaceAllBtn.Click += (_, _) => ReplaceAllRequested?.Invoke();

        ExpandBtn.Click += (_, _) => {
            _isReplaceMode = !_isReplaceMode;
            ReplaceBox.IsVisible = _isReplaceMode;
            ReplaceButtons.IsVisible = _isReplaceMode;
            UpdateExpandGlyph();
        };

        // Search text changed → raise event + toggle clear button
        SearchBox.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty) {
                SearchTermChanged?.Invoke();
                SearchClearBtn.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            }
        };

        // Replace text changed → toggle clear button
        ReplaceBox.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty) {
                ReplaceClearBtn.IsVisible = !string.IsNullOrEmpty(ReplaceBox.Text);
            }
        };

        // Clear button clicks
        SearchClearBtn.Click += (_, _) => {
            SearchBox.Text = "";
            SearchBox.Focus();
        };
        ReplaceClearBtn.Click += (_, _) => {
            ReplaceBox.Text = "";
            ReplaceBox.Focus();
        };

        // Keyboard shortcuts within the text boxes
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        ReplaceBox.KeyDown += OnReplaceBoxKeyDown;

        // Left-edge resize grip drag
        ResizeGrip.PointerPressed += OnResizePointerPressed;
        ResizeGrip.PointerMoved += OnResizePointerMoved;
        ResizeGrip.PointerReleased += OnResizePointerReleased;
    }

    public void FocusSearchBox() {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void FocusReplaceBox() {
        ReplaceBox.Focus();
        ReplaceBox.SelectAll();
    }

    public void SetSearchTerm(string text) {
        SearchBox.Text = text;
    }

    public void SetMatchInfo(int current, int total) {
        MatchCount.Text = total == 0 ? "" : $"{current} / {total}";
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
        SearchBox.Foreground = theme.EditorForeground;
        ReplaceBox.Foreground = theme.EditorForeground;
        OptionsSeparator.Background = theme.TabBarBorder;
        OptionsSeparator2.Background = theme.TabBarBorder;
    }

    private static void SetupClearButton(Button btn) {
        btn.Content = new TextBlock {
            Text = IconGlyphs.Close,
            FontFamily = IconGlyphs.Family,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private void UpdateExpandGlyph() {
        if (ExpandBtn.Content is TextBlock tb) {
            // Down chevron when expanded, right chevron when collapsed
            tb.Text = _isReplaceMode ? "\uF2A3" : "\uF2B1";
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e) {
        switch (e.Key) {
            case Key.Return:
                var forward = (e.KeyModifiers & KeyModifiers.Shift) == 0;
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
        }
    }
}
