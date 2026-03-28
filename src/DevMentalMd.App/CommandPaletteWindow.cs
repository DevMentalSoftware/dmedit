using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using DevMentalMd.App.Commands;
using Cmd = DevMentalMd.App.Commands.Commands;
using DevMentalMd.App.Controls;
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
    private readonly AppSettings _settings;
    private readonly DMTextBox _filterBox;
    private readonly ToggleButton _groupToggle;
    private readonly StackPanel _listPanel;
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _hintText;
    private readonly Border _rootBorder;

    // Flat list of visible command rows, kept in sync with the panel.
    private readonly List<(Border border, Command cmd)> _visibleRows = [];
    private int _selectedIndex = -1;

    // Suppresses redundant filter updates while arrow navigation is filling the box.
    private bool _suppressFilter;

    private bool _grouped;
    private EditorTheme _theme = EditorTheme.Light;

    /// <summary>
    /// The command ID to execute, set when the user confirms a selection.
    /// Read by the caller after the window closes.
    /// </summary>
    public string? SelectedCommandId { get; private set; }

    public CommandPaletteWindow(KeyBindingService keyBindings,
                                AppSettings settings, EditorTheme theme) {
        _keyBindings = keyBindings;
        _settings = settings;
        _theme = theme;
        _grouped = settings.CommandPaletteGroupByCategory;

        Title = "Command Palette";
        Width = 520;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        // Filter text box with built-in clear button.
        _filterBox = new DMTextBox {
            Watermark = "Type to filter commands\u2026",
            FontSize = 14,
        };
        _filterBox.PropertyChanged += (_, e) => {
            if (e.Property == DMTextBox.TextProperty && !_suppressFilter) {
                RebuildList();
            }
        };
        _filterBox.TemplateApplied += (_, _) => {
            _filterBox.InnerTextBox?.AddHandler(
                KeyDownEvent, OnFilterKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        };

        // Grouping toggle button.
        _groupToggle = new ToggleButton {
            Content = "C",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            IsChecked = _grouped,
            Padding = new Thickness(5, 3),
            MinHeight = 0,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_groupToggle, "Group by category");
        _groupToggle.IsCheckedChanged += (_, _) => {
            _grouped = _groupToggle.IsChecked == true;
            _settings.CommandPaletteGroupByCategory = _grouped;
            _settings.ScheduleSave();
            RebuildList();
        };

        // Close button (same style as find bar close).
        var closeBtn = new Button {
            Width = 24, Height = 24,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock {
                Text = IconGlyphs.Close,
                FontFamily = IconGlyphs.Family,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        ToolTip.SetTip(closeBtn, "Close (Escape)");
        closeBtn.Click += (_, _) => Close();

        var filterRow = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            Margin = new Thickness(8, 8, 8, 4),
        };
        Grid.SetColumn(_filterBox, 0);
        Grid.SetColumn(_groupToggle, 1);
        Grid.SetColumn(closeBtn, 2);
        _groupToggle.Margin = new Thickness(4, 0, 0, 0);
        closeBtn.Margin = new Thickness(2, 0, 0, 0);
        filterRow.Children.Add(_filterBox);
        filterRow.Children.Add(_groupToggle);
        filterRow.Children.Add(closeBtn);

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
            Margin = new Thickness(12, 16, 0, 0),
            IsVisible = false,
        };

        var root = new DockPanel();
        DockPanel.SetDock(filterRow, Dock.Top);
        root.Children.Add(filterRow);
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

        // Collect filtered commands.
        var filtered = new List<Command>();
        foreach (var cmd in Cmd.All) {
            if (cmd.Category == "Menu") continue; // pseudo-commands for Alt access keys
            if (cmd.Category == "Dev" && !_settings.DevMode) continue;
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
            filtered.Add(cmd);
        }

        if (_grouped) {
            BuildGroupedList(filtered);
        } else {
            BuildFlatList(filtered);
        }

        _hintText.IsVisible = _visibleRows.Count == 0;

        // Auto-select first item.
        if (_visibleRows.Count > 0) {
            SetSelected(0);
        }
    }

    private void BuildFlatList(List<Command> commands) {
        commands.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
            StringComparison.OrdinalIgnoreCase));
        foreach (var cmd in commands) {
            var row = CreateRow(cmd, indent: false);
            _listPanel.Children.Add(row);
            _visibleRows.Add((row, cmd));
        }
    }

    private void BuildGroupedList(List<Command> commands) {
        // Group by category, preserving first-seen order.
        var groupMap = new Dictionary<string, List<Command>>();
        var groupOrder = new List<string>();
        foreach (var cmd in commands) {
            if (!groupMap.TryGetValue(cmd.Category, out var list)) {
                list = [];
                groupMap[cmd.Category] = list;
                groupOrder.Add(cmd.Category);
            }
            list.Add(cmd);
        }
        var groups = groupOrder.Select(c => (Category: c, Commands: groupMap[c]));

        foreach (var group in groups) {
            // Category header (not selectable).
            var header = new TextBlock {
                Text = group.Category,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(4, 8, 0, 2),
                Foreground = _theme.EditorForeground,
            };
            _listPanel.Children.Add(header);

            foreach (var cmd in group.Commands) {
                var row = CreateRow(cmd, indent: true);
                _listPanel.Children.Add(row);
                _visibleRows.Add((row, cmd));
            }
        }
    }

    private Border CreateRow(Command cmd, bool indent) {
        var gesture1 = _keyBindings.GetGestureText(cmd.Id) ?? "";
        var gesture2 = _keyBindings.GetGesture2Text(cmd.Id) ?? "";
        var gestureDisplay = gesture1;
        if (gesture2.Length > 0) {
            gestureDisplay += gestureDisplay.Length > 0 ? $"  |  {gesture2}" : gesture2;
        }

        var gestureText = new TextBlock {
            Text = gestureDisplay,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Grid grid;
        if (_grouped) {
            var nameText = new TextBlock {
                Text = cmd.DisplayName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(gestureText, 1);
            grid.Children.Add(nameText);
            grid.Children.Add(gestureText);
        } else {
            // Fixed-width category column (52px fits "Window" at 11px) so
            // command names align. Inlines in a single TextBlock share baselines.
            var nameBlock = new TextBlock {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 52,
            };
            nameBlock.Inlines!.Add(new Run(cmd.Category) { FontSize = 11 });
            var nameText = new TextBlock {
                Text = cmd.DisplayName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*") };
            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(gestureText, 2);
            nameBlock.Margin = new Thickness(0, 0, 8, 0);
            grid.Children.Add(nameBlock);
            grid.Children.Add(nameText);
            grid.Children.Add(gestureText);
        }

        var border = new Border {
            Child = grid,
            Padding = new Thickness(indent ? 20 : 8, 5, 8, 5),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Tag = cmd.Id,
        };

        border.PointerPressed += (_, e) => {
            var idx = _visibleRows.FindIndex(r => r.border == border);
            if (idx >= 0 && cmd.IsEnabled) {
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

        ApplyRowTheme(border, cmd);
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
        if (_filterBox.InnerTextBox != null) {
            _filterBox.InnerTextBox.CaretIndex = _filterBox.Text?.Length ?? 0;
        }
        _suppressFilter = false;
    }

    private void Confirm() {
        if (_selectedIndex >= 0 && _selectedIndex < _visibleRows.Count
            && _visibleRows[_selectedIndex].cmd.IsEnabled) {
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
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
        _rootBorder.BorderBrush = theme.TabBarBorder;

        _hintText.Foreground = theme.SettingsDimForeground;

        foreach (var (border, cmd) in _visibleRows) {
            ApplyRowTheme(border, cmd);
        }

        // Re-highlight selection.
        if (_selectedIndex >= 0 && _selectedIndex < _visibleRows.Count) {
            _visibleRows[_selectedIndex].border.Background = theme.SettingsRowSelection;
        }
    }

    private void ApplyRowTheme(Border border, Command cmd) {
        if (border.Child is not Grid grid) return;
        border.Opacity = cmd.IsEnabled ? 1.0 : 0.4;
        var lastCol = grid.ColumnDefinitions.Count - 1;
        foreach (var child in grid.Children) {
            if (child is not TextBlock tb) continue;
            var col = Grid.GetColumn(tb);
            if (col == lastCol) {
                // Gesture (always the last column).
                tb.Foreground = _theme.SettingsAccent;
            } else if (tb.Inlines is { Count: > 0 }) {
                // Category block with Inlines (ungrouped mode, col 0).
                tb.Foreground = _theme.SettingsDimForeground;
            } else {
                // Command name.
                tb.Foreground = _theme.EditorForeground;
            }
        }
    }
}
