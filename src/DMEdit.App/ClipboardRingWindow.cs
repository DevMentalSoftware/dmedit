using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DMEdit.App.Services;

namespace DMEdit.App;

/// <summary>
/// Popup window showing the clipboard ring contents as a numbered list.
/// The user can pick an entry with arrow keys + Enter, or press 0–9 to
/// select by index directly.
/// </summary>
public class ClipboardRingWindow : Window {
    private readonly ClipboardRing _ring;
    private readonly StackPanel _listPanel;
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _hintText;
    private readonly Border _rootBorder;

    private readonly List<Border> _rows = [];
    private int _selectedIndex = -1;

    private EditorTheme _theme = EditorTheme.Light;

    /// <summary>
    /// Index into the clipboard ring the user selected, or -1 if cancelled.
    /// </summary>
    public int SelectedIndex { get; private set; } = -1;

    public ClipboardRingWindow(ClipboardRing ring, EditorTheme theme) {
        _ring = ring;
        _theme = theme;

        Title = "Clipboard Ring";
        Width = 480;
        Height = Math.Min(40 + ring.Count * 36, 420);
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;
        Focusable = true;

        _listPanel = new StackPanel { Spacing = 0 };
        _listScroll = new ScrollViewer {
            Content = _listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
            Margin = new Thickness(8),
        };

        _hintText = new TextBlock {
            Text = "Clipboard ring is empty",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            IsVisible = ring.Count == 0,
        };

        var root = new DockPanel();
        root.Children.Add(_hintText);
        root.Children.Add(_listScroll);

        _rootBorder = new Border {
            Child = root,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        Content = _rootBorder;

        BuildList();
        ApplyTheme(theme);

        Opened += (_, _) => Focus();
    }

    // =================================================================
    // List building
    // =================================================================

    private void BuildList() {
        _listPanel.Children.Clear();
        _rows.Clear();
        _selectedIndex = -1;

        for (var i = 0; i < _ring.Count; i++) {
            var text = _ring.Get(i) ?? "";
            var row = CreateRow(i, text);
            _listPanel.Children.Add(row);
            _rows.Add(row);
        }

        if (_rows.Count > 0) {
            SetSelected(0);
        }
    }

    private Border CreateRow(int index, string text) {
        // Truncate long entries to a single preview line.
        var preview = TruncatePreview(text, 80);

        var indexText = new TextBlock {
            Text = index.ToString(),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 20,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var previewText = new TextBlock {
            Text = preview,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
        };
        Grid.SetColumn(indexText, 0);
        Grid.SetColumn(previewText, 1);
        grid.Children.Add(indexText);
        grid.Children.Add(previewText);

        var border = new Border {
            Child = grid,
            Padding = new Thickness(8, 5, 8, 5),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
        };

        var capturedIndex = index;
        border.PointerPressed += (_, e) => {
            SetSelected(capturedIndex);
            Confirm();
            e.Handled = true;
        };
        border.PointerEntered += (_, _) => SetSelected(capturedIndex);

        return border;
    }

    private static string TruncatePreview(string text, int maxLen) {
        // Replace newlines with visible markers, then truncate.
        var single = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = single.AsSpan();
        var nlIdx = firstLine.IndexOf('\n');
        if (nlIdx >= 0) {
            firstLine = firstLine[..nlIdx];
        }
        var result = firstLine.Length > maxLen
            ? new string(firstLine[..maxLen]) + "\u2026"
            : new string(firstLine);
        if (nlIdx >= 0) {
            result += " \u21B5\u2026"; // ↵…
        }
        return result;
    }

    // =================================================================
    // Selection management
    // =================================================================

    private void SetSelected(int index) {
        if (index < 0 || index >= _rows.Count) return;
        if (_selectedIndex >= 0 && _selectedIndex < _rows.Count) {
            _rows[_selectedIndex].Background = Brushes.Transparent;
        }
        _selectedIndex = index;
        _rows[index].Background = _theme.SettingsRowSelection;
        _rows[index].BringIntoView();
    }

    private void MoveSelection(int delta) {
        if (_rows.Count == 0) return;
        var newIdx = Math.Clamp(_selectedIndex + delta, 0, _rows.Count - 1);
        SetSelected(newIdx);
    }

    private void Confirm() {
        if (_selectedIndex >= 0 && _selectedIndex < _rows.Count) {
            SelectedIndex = _selectedIndex;
        }
        Close();
    }

    // =================================================================
    // Keyboard handling
    // =================================================================

    protected override void OnKeyDown(KeyEventArgs e) {
        switch (e.Key) {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Return:
                Confirm();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(+10);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-10);
                e.Handled = true;
                break;
            default:
                // Number keys 0–9 select by index directly.
                var digit = KeyToDigit(e.Key);
                if (digit >= 0 && digit < _rows.Count) {
                    SetSelected(digit);
                    Confirm();
                    e.Handled = true;
                }
                break;
        }
        if (!e.Handled) base.OnKeyDown(e);
    }

    private static int KeyToDigit(Key key) => key switch {
        Key.D0 or Key.NumPad0 => 0,
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        _ => -1,
    };

    // =================================================================
    // Theming
    // =================================================================

    private void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        Background = theme.EditorBackground;
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
        _rootBorder.BorderBrush = theme.TabBarBorder;
        _hintText.Foreground = theme.SettingsDimForeground;

        foreach (var border in _rows) {
            if (border.Child is not Grid grid) continue;
            foreach (var child in grid.Children) {
                if (child is TextBlock tb) {
                    var col = Grid.GetColumn(tb);
                    tb.Foreground = col == 0
                        ? theme.SettingsDimForeground   // index number
                        : theme.EditorForeground;        // preview text
                }
            }
        }

        if (_selectedIndex >= 0 && _selectedIndex < _rows.Count) {
            _rows[_selectedIndex].Background = theme.SettingsRowSelection;
        }
    }
}
