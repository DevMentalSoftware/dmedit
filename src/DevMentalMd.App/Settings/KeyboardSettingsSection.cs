using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

/// <summary>
/// Custom settings section for keyboard shortcut configuration. Displays a
/// grouped command list with category headers, current bindings, a key
/// capture box, and assign/remove/reset buttons.
/// </summary>
public class KeyboardSettingsSection : UserControl {
    private readonly KeyBindingService _keyBindings;
    private readonly AppSettings _settings;

    private readonly TextBox _filterBox;
    private readonly StackPanel _commandList;
    private readonly ScrollViewer _commandScroll;
    private readonly TextBlock _shortcutLabel;
    private readonly TextBox _captureBox;
    private readonly TextBlock _conflictLabel;
    private readonly Button _assignBtn;
    private readonly Button _removeBtn;
    private readonly Button _resetBtn;

    private string? _selectedCommandId;
    private KeyGesture? _capturedGesture;
    private EditorTheme _theme = EditorTheme.Light;

    // Tracks all command row borders for selection highlighting.
    private readonly List<(Border border, string commandId)> _commandRows = [];
    // Tracks category headers for filtering.
    private readonly List<(TextBlock header, string category, List<Border> rows)> _categoryGroups = [];

    /// <summary>
    /// Fired when any binding is changed (assigned, removed, or reset).
    /// </summary>
    public event Action? BindingChanged;

    public KeyboardSettingsSection(KeyBindingService keyBindings, AppSettings settings) {
        _keyBindings = keyBindings;
        _settings = settings;

        // -- Filter box --
        _filterBox = new TextBox {
            Watermark = "Filter commands...",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _filterBox.TextChanged += (_, _) => ApplyFilter();

        // -- Command list --
        _commandList = new StackPanel { Spacing = 0 };
        _commandScroll = new ScrollViewer {
            Content = _commandList,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MinHeight = 150,
        };

        // -- Shortcut display --
        _shortcutLabel = new TextBlock {
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 4),
        };

        // -- Capture box with tunneling handler --
        _captureBox = new TextBox {
            Watermark = "Press shortcut keys...",
            FontSize = 13,
            IsReadOnly = true,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _captureBox.AddHandler(KeyDownEvent, OnCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // -- Conflict label --
        _conflictLabel = new TextBlock {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x00)),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        // -- Buttons --
        _assignBtn = new Button { Content = "Assign", Margin = new Thickness(0, 0, 6, 0) };
        _removeBtn = new Button { Content = "Remove", Margin = new Thickness(0, 0, 6, 0) };
        _resetBtn = new Button { Content = "Reset" };

        _assignBtn.Click += OnAssign;
        _removeBtn.Click += OnRemove;
        _resetBtn.Click += OnReset;

        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 0),
        };
        buttonPanel.Children.Add(_assignBtn);
        buttonPanel.Children.Add(_removeBtn);
        buttonPanel.Children.Add(_resetBtn);

        // -- Layout --
        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        root.Children.Add(_filterBox);
        root.Children.Add(_commandScroll);
        root.Children.Add(_shortcutLabel);

        var captureLabel = new TextBlock {
            Text = "Press shortcut keys:",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
        };
        root.Children.Add(captureLabel);
        root.Children.Add(_captureBox);
        root.Children.Add(_conflictLabel);
        root.Children.Add(buttonPanel);

        Content = root;

        BuildCommandList();
        UpdateButtonStates();
    }

    private void BuildCommandList() {
        _commandList.Children.Clear();
        _commandRows.Clear();
        _categoryGroups.Clear();

        foreach (var category in CommandRegistry.Categories) {
            var commands = CommandRegistry.All
                .Where(c => c.Category == category)
                .ToList();
            if (commands.Count == 0) continue;

            // Category header
            var header = new TextBlock {
                Text = category,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(4, 10, 0, 2),
            };
            _commandList.Children.Add(header);

            var rowsInCategory = new List<Border>();

            foreach (var cmd in commands) {
                var row = CreateCommandRow(cmd);
                _commandList.Children.Add(row);
                _commandRows.Add((row, cmd.Id));
                rowsInCategory.Add(row);
            }

            _categoryGroups.Add((header, category, rowsInCategory));
        }
    }

    private Border CreateCommandRow(CommandDescriptor cmd) {
        var gestureText = _keyBindings.GetGestureText(cmd.Id) ?? "";

        var nameText = new TextBlock {
            Text = cmd.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var gestureLabel = new TextBlock {
            Text = gestureText,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(gestureLabel, 1);
        grid.Children.Add(nameText);
        grid.Children.Add(gestureLabel);

        var border = new Border {
            Child = grid,
            Padding = new Thickness(16, 4, 8, 4),
            Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand),
            Tag = cmd.Id,
        };

        border.PointerPressed += (_, e) => {
            SelectCommand(cmd.Id);
            e.Handled = true;
        };

        return border;
    }

    private void SelectCommand(string commandId) {
        _selectedCommandId = commandId;
        _capturedGesture = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";

        // Update selection highlight
        foreach (var (border, id) in _commandRows) {
            border.Background = id == commandId
                ? new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7))
                : Brushes.Transparent;
        }

        // Show current binding
        var gesture = _keyBindings.GetGestureText(commandId);
        var desc = KeyBindingService.GetDescriptor(commandId);
        _shortcutLabel.Text = gesture != null
            ? $"Shortcut for {desc?.DisplayName ?? commandId}: {gesture}"
            : $"Shortcut for {desc?.DisplayName ?? commandId}: (none)";

        UpdateButtonStates();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e) {
        // Ignore bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) {
            e.Handled = true;
            return;
        }

        _capturedGesture = new KeyGesture(e.Key, e.KeyModifiers);
        _captureBox.Text = _capturedGesture.ToString();

        // Check for conflicts
        if (_selectedCommandId != null) {
            var conflict = _keyBindings.FindConflict(_capturedGesture, _selectedCommandId);
            if (conflict != null) {
                var desc = KeyBindingService.GetDescriptor(conflict);
                _conflictLabel.Text = $"Currently assigned to: {desc?.DisplayName ?? conflict} ({conflict})";
            } else {
                _conflictLabel.Text = "";
            }
        }

        UpdateButtonStates();
        e.Handled = true;
    }

    private void OnAssign(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null || _capturedGesture == null) return;

        _keyBindings.SetBinding(_selectedCommandId, _capturedGesture);
        RefreshAfterChange();
    }

    private void OnRemove(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null) return;

        _keyBindings.SetBinding(_selectedCommandId, null);
        RefreshAfterChange();
    }

    private void OnReset(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null) return;

        _keyBindings.ResetBinding(_selectedCommandId);
        RefreshAfterChange();
    }

    private void RefreshAfterChange() {
        // Rebuild the command list to reflect updated gesture text.
        var selected = _selectedCommandId;
        BuildCommandList();
        ApplyFilter();
        ApplyTheme(_theme);
        if (selected != null) SelectCommand(selected);

        _capturedGesture = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";
        UpdateButtonStates();

        BindingChanged?.Invoke();
    }

    private void UpdateButtonStates() {
        _assignBtn.IsEnabled = _selectedCommandId != null && _capturedGesture != null;
        _removeBtn.IsEnabled = _selectedCommandId != null
                               && _keyBindings.GetGesture(_selectedCommandId) != null;
        _resetBtn.IsEnabled = _selectedCommandId != null;
    }

    private void ApplyFilter() {
        var search = _filterBox.Text?.Trim() ?? "";

        foreach (var (header, category, rows) in _categoryGroups) {
            var anyVisible = false;
            foreach (var row in rows) {
                if (row.Tag is string commandId) {
                    var desc = KeyBindingService.GetDescriptor(commandId);
                    var matches = string.IsNullOrEmpty(search)
                        || (desc?.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
                        || commandId.Contains(search, StringComparison.OrdinalIgnoreCase);
                    row.IsVisible = matches;
                    if (matches) anyVisible = true;
                }
            }
            header.IsVisible = anyVisible || string.IsNullOrEmpty(search);
        }
    }

    /// <summary>
    /// Applies theme colors to the keyboard settings section.
    /// </summary>
    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;

        foreach (var (header, _, _) in _categoryGroups) {
            header.Foreground = theme.EditorForeground;
        }
        foreach (var (border, _) in _commandRows) {
            if (border.Child is Grid grid) {
                foreach (var child in grid.Children) {
                    if (child is TextBlock tb && tb.HorizontalAlignment != HorizontalAlignment.Right) {
                        tb.Foreground = theme.EditorForeground;
                    }
                }
            }
        }
        _shortcutLabel.Foreground = theme.EditorForeground;
    }
}
