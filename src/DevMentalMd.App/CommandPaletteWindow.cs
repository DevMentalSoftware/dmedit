using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

/// <summary>
/// A lightweight popup window that lists all registered commands with their
/// current key bindings. The user can type to filter, use arrow keys to
/// navigate, and press Enter to execute the selected command. Similar to
/// VS Code's Ctrl+Shift+P command palette.
/// </summary>
public class CommandPaletteWindow : Window {
    private readonly KeyBindingService _keyBindings;
    private readonly TextBox _filterBox;
    private readonly StackPanel _listPanel;
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _hintText;
    private readonly Border _rootBorder;

    // Flat list of visible command rows, kept in sync with the panel.
    private readonly List<(Border border, CommandDescriptor cmd)> _visibleRows = [];
    private int _selectedIndex = -1;

    // Suppresses redundant filter updates while arrow navigation is filling the box.
    private bool _suppressFilter;

    private EditorTheme _theme = EditorTheme.Light;

    /// <summary>
    /// The command ID to execute, set when the user confirms a selection.
    /// Read by the caller after the window closes.
    /// </summary>
    public string? SelectedCommandId { get; private set; }

    public CommandPaletteWindow(KeyBindingService keyBindings, EditorTheme theme) {
        _keyBindings = keyBindings;
        _theme = theme;

        Title = "Command Palette";
        Width = 520;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;

        // Filter text box.
        _filterBox = new TextBox {
            Watermark = "Type to filter commands\u2026",
            FontSize = 14,
            Margin = new Thickness(8, 8, 8, 4),
        };
        _filterBox.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty && !_suppressFilter) {
                RebuildList();
            }
        };
        _filterBox.AddHandler(
            KeyDownEvent, OnFilterKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Command list.
        _listPanel = new StackPanel { Spacing = 0 };
        _listScroll = new ScrollViewer {
            Content = _listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
            Margin = new Thickness(8, 0, 8, 8),
        };

        _hintText = new TextBlock {
            Text = "No matching commands",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            IsVisible = false,
        };

        var root = new DockPanel();
        DockPanel.SetDock(_filterBox, Dock.Top);
        root.Children.Add(_filterBox);
        root.Children.Add(_hintText);
        root.Children.Add(_listScroll);

        _rootBorder = new Border {
            Child = root,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        Content = _rootBorder;

        ApplyTheme(theme);
        RebuildList();

        // Focus the filter box once the window is open.
        Opened += (_, _) => _filterBox.Focus();
    }

    // =================================================================
    // Filtering & list building
    // =================================================================

    private void RebuildList() {
        _listPanel.Children.Clear();
        _visibleRows.Clear();
        _selectedIndex = -1;

        var filter = _filterBox.Text?.Trim() ?? "";
        var hasFilter = filter.Length > 0;

        foreach (var cmd in CommandRegistry.All) {
            // Skip low-level typing primitives that aren't useful in the palette.
            if (IsHiddenCommand(cmd.Id)) {
                continue;
            }

            if (hasFilter) {
                var matchesName = cmd.DisplayName.Contains(filter,
                    StringComparison.OrdinalIgnoreCase);
                var matchesId = cmd.Id.Contains(filter,
                    StringComparison.OrdinalIgnoreCase);
                var matchesCategory = cmd.Category.Contains(filter,
                    StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesId && !matchesCategory) {
                    continue;
                }
            }

            var row = CreateRow(cmd);
            _listPanel.Children.Add(row);
            _visibleRows.Add((row, cmd));
        }

        _hintText.IsVisible = _visibleRows.Count == 0;

        // Auto-select first item.
        if (_visibleRows.Count > 0) {
            SetSelected(0);
        }
    }

    /// <summary>
    /// Commands that are low-level typing primitives (single character input,
    /// backspace, delete, newline, tab) are hidden from the palette since
    /// executing them from here doesn't make practical sense.
    /// </summary>
    private static bool IsHiddenCommand(string id) =>
        id is CommandIds.EditNewline
            or CommandIds.EditTab
            or CommandIds.EditBackspace
            or CommandIds.EditDelete;

    private Border CreateRow(CommandDescriptor cmd) {
        var gesture1 = _keyBindings.GetGestureText(cmd.Id) ?? "";
        var gesture2 = _keyBindings.GetGesture2Text(cmd.Id) ?? "";
        var gestureDisplay = gesture1;
        if (gesture2.Length > 0) {
            gestureDisplay += gestureDisplay.Length > 0 ? $"  |  {gesture2}" : gesture2;
        }

        var nameText = new TextBlock {
            Text = cmd.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var categoryText = new TextBlock {
            Text = cmd.Category,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var gestureText = new TextBlock {
            Text = gestureDisplay,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*"),
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(categoryText, 1);
        Grid.SetColumn(gestureText, 2);
        grid.Children.Add(nameText);
        grid.Children.Add(categoryText);
        grid.Children.Add(gestureText);

        var border = new Border {
            Child = grid,
            Padding = new Thickness(8, 5, 8, 5),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = cmd.Id,
        };

        border.PointerPressed += (_, e) => {
            var idx = _visibleRows.FindIndex(r => r.border == border);
            if (idx >= 0) {
                SetSelected(idx);
                Confirm();
            }
            e.Handled = true;
        };

        border.PointerEntered += (_, _) => {
            var idx = _visibleRows.FindIndex(r => r.border == border);
            if (idx >= 0) {
                SetSelected(idx);
            }
        };

        return border;
    }

    // =================================================================
    // Selection management
    // =================================================================

    private void SetSelected(int index) {
        if (index < 0 || index >= _visibleRows.Count) return;

        // Clear old highlight.
        if (_selectedIndex >= 0 && _selectedIndex < _visibleRows.Count) {
            _visibleRows[_selectedIndex].border.Background = Brushes.Transparent;
        }

        _selectedIndex = index;
        _visibleRows[index].border.Background = _theme.SettingsRowSelection;

        // Scroll selection into view.
        ScrollSelectedIntoView();
    }

    private void ScrollSelectedIntoView() {
        if (_selectedIndex < 0 || _selectedIndex >= _visibleRows.Count) return;
        var border = _visibleRows[_selectedIndex].border;
        // Use BringIntoView which is the Avalonia standard approach.
        border.BringIntoView();
    }

    private void MoveSelection(int delta) {
        if (_visibleRows.Count == 0) return;
        var newIdx = _selectedIndex + delta;
        if (newIdx < 0) {
            newIdx = 0;
        } else if (newIdx >= _visibleRows.Count) {
            newIdx = _visibleRows.Count - 1;
        }
        SetSelected(newIdx);

        // Arrow navigation fills the textbox with the selected command name.
        _suppressFilter = true;
        _filterBox.Text = _visibleRows[newIdx].cmd.DisplayName;
        // Move caret to end.
        _filterBox.CaretIndex = _filterBox.Text?.Length ?? 0;
        _suppressFilter = false;
    }

    private void Confirm() {
        if (_selectedIndex >= 0 && _selectedIndex < _visibleRows.Count) {
            SelectedCommandId = _visibleRows[_selectedIndex].cmd.Id;
        }
        Close();
    }

    // =================================================================
    // Keyboard handling
    // =================================================================

    private void OnFilterKeyDown(object? sender, KeyEventArgs e) {
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
                // If arrow-navigated, execute the highlighted command.
                // If typed text, execute if there's a unique match.
                if (_selectedIndex >= 0) {
                    Confirm();
                }
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
        }
    }

    // Also intercept Escape at the window level in case focus is elsewhere.
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // =================================================================
    // Theming
    // =================================================================

    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        Background = theme.EditorBackground;
        _rootBorder.BorderBrush = theme.TabBarBorder;

        _filterBox.Foreground = theme.EditorForeground;
        _hintText.Foreground = theme.SettingsDimForeground;

        foreach (var (border, cmd) in _visibleRows) {
            ApplyRowTheme(border, cmd);
        }

        // Re-highlight selection.
        if (_selectedIndex >= 0 && _selectedIndex < _visibleRows.Count) {
            _visibleRows[_selectedIndex].border.Background = theme.SettingsRowSelection;
        }
    }

    private void ApplyRowTheme(Border border, CommandDescriptor cmd) {
        if (border.Child is not Grid grid) return;
        foreach (var child in grid.Children) {
            if (child is TextBlock tb) {
                var col = Grid.GetColumn(tb);
                tb.Foreground = col switch {
                    1 => _theme.SettingsDimForeground,   // category
                    2 => _theme.SettingsAccent,           // gesture
                    _ => _theme.EditorForeground,         // name
                };
            }
        }
    }
}
