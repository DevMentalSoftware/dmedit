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

namespace DevMentalMd.App.Settings;

/// <summary>
/// Custom settings section for keyboard shortcut configuration. Displays a
/// toolbar with category quick-filter buttons and a keystroke filter, a
/// scrollable 3-column command list, and a bottom row with key capture box,
/// conflict label, and assign/remove/reset buttons.
/// </summary>
public class KeyboardSettingsSection : UserControl {
    private readonly KeyBindingService _keyBindings;
    private readonly AppSettings _settings;

    // -- Toolbar --
    private readonly List<Button> _categoryButtons = [];
    private readonly Border _keyFilterPanel;
    private readonly TextBlock _keyFilterText;
    private readonly Button _keyFilterClearBtn;

    // -- Command list --
    private readonly StackPanel _commandList;
    private readonly ScrollViewer _commandScroll;

    // -- Bottom controls --
    private readonly TextBlock _shortcutLabel;
    private readonly TextBox _captureBox;
    private readonly Button _captureClearBtn;
    private readonly TextBlock _conflictLabel;
    private readonly Button _assignBtn;
    private readonly Button _removeBtn;
    private readonly Button _resetBtn;
    private readonly TextBlock _scrollHint;

    // -- State --
    private string? _selectedCommandId;
    private KeyGesture? _capturedGesture;
    private string? _activeCategory;        // null = All
    private KeyGesture? _keyFilter;         // keystroke filter
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

        // =====================================================================
        // Toolbar: category buttons + keystroke filter
        // =====================================================================
        var toolbar = new WrapPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };

        // Category quick-filter buttons
        var categories = new List<String>(CommandRegistry.Categories.Count + 1);
        categories.Add("All");
        categories.AddRange(CommandRegistry.Categories);    
        foreach (var cat in categories) {
            var btn = new Button {
                Content = cat,
                FontSize = 12,
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Tag = cat,
            };
            btn.Click += OnCategoryButtonClick;
            _categoryButtons.Add(btn);
            toolbar.Children.Add(btn);
        }

        // Keystroke filter panel (focusable border, not a TextBox)
        _keyFilterText = new TextBlock {
            Text = "Filter by key\u2026",
            FontSize = 12,
            Foreground = _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _keyFilterClearBtn = new Button {
            Content = "\u2715",
            FontSize = 11,
            Padding = new Thickness(3, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        _keyFilterClearBtn.Click += (_, _) => ClearKeyFilter();

        var keyFilterContent = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };
        Grid.SetColumn(_keyFilterText, 0);
        Grid.SetColumn(_keyFilterClearBtn, 1);
        keyFilterContent.Children.Add(_keyFilterText);
        keyFilterContent.Children.Add(_keyFilterClearBtn);

        _keyFilterPanel = new Border {
            Child = keyFilterContent,
            BorderThickness = new Thickness(1),
            BorderBrush = _theme.SettingsInputBorder,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3),
            Margin = new Thickness(4, 0, 0, 4),
            MinWidth = 120,
            Focusable = true,
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        _keyFilterPanel.AddHandler(
            KeyDownEvent, OnKeyFilterKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _keyFilterPanel.GotFocus += (_, _) => {
            _keyFilterPanel.BorderBrush = _theme.SettingsAccent;
        };
        _keyFilterPanel.LostFocus += (_, _) => {
            _keyFilterPanel.BorderBrush = _theme.SettingsInputBorder;
        };
        toolbar.Children.Add(_keyFilterPanel);

        // =====================================================================
        // Command list (scrollable)
        // =====================================================================
        _commandList = new StackPanel { Spacing = 0 };
        _commandScroll = new ScrollViewer {
            Content = _commandList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 350,
            Width = 600,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // Ctrl+Scroll routes to inner scrollviewer, plain scroll bubbles up.
        _commandScroll.AddHandler(PointerWheelChangedEvent, OnCommandListWheel,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // =====================================================================
        // Shortcut display
        // =====================================================================
        _shortcutLabel = new TextBlock {
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 4),
        };

        // =====================================================================
        // Bottom controls: capture box + clear + conflict + buttons on one line
        // =====================================================================
        _captureBox = new TextBox {
            Watermark = "Press shortcut keys\u2026",
            FontSize = 13,
            IsReadOnly = true,
            MaxWidth = 260,
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _captureBox.AddHandler(
            KeyDownEvent, OnCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _captureClearBtn = new Button {
            Content = "\u2715",
            FontSize = 11,
            Padding = new Thickness(4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            IsVisible = false,
        };
        _captureClearBtn.Click += (_, _) => ClearCapturedGesture();

        _conflictLabel = new TextBlock {
            FontSize = 11,
            Foreground = _theme.SettingsWarnForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _assignBtn = new Button { Content = "Assign", Margin = new Thickness(8, 0, 4, 0) };
        _removeBtn = new Button { Content = "Remove", Margin = new Thickness(0, 0, 4, 0) };
        _resetBtn = new Button { Content = "Reset" };

        _assignBtn.Click += OnAssign;
        _removeBtn.Click += OnRemove;
        _resetBtn.Click += OnReset;

        var bottomRow = new WrapPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };
        bottomRow.Children.Add(_captureBox);
        bottomRow.Children.Add(_captureClearBtn);
        bottomRow.Children.Add(_conflictLabel);
        bottomRow.Children.Add(_assignBtn);
        bottomRow.Children.Add(_removeBtn);
        bottomRow.Children.Add(_resetBtn);

        // =====================================================================
        // Root layout
        // =====================================================================
        _scrollHint = new TextBlock {
            Text = "Ctrl+Scroll to scroll this list when the page also scrolls.",
            FontSize = 11,
            Foreground = _theme.SettingsDimForeground,
            Margin = new Thickness(24, 0, 0, 2),
        };

        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        root.Children.Add(toolbar);
        root.Children.Add(_scrollHint);
        root.Children.Add(_commandScroll);
        root.Children.Add(_shortcutLabel);
        root.Children.Add(bottomRow);

        Content = root;

        BuildCommandList();
        UpdateCategoryButtonStyles();
        UpdateButtonStates();
    }

    // =====================================================================
    // Category quick-filter buttons
    // =====================================================================

    private void OnCategoryButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (sender is Button btn && btn.Tag is string cat) {
            _activeCategory = cat == "All" ? null : cat;
            UpdateCategoryButtonStyles();
            ApplyFilter();
        }
    }

    private void UpdateCategoryButtonStyles() {
        foreach (var btn in _categoryButtons) {
            var isActive = (btn.Tag is string tag)
                && ((_activeCategory == null && tag == "All")
                    || (_activeCategory != null && tag == _activeCategory));
            btn.Background = isActive ? _theme.SettingsButtonActive : Brushes.Transparent;
        }
    }

    // =====================================================================
    // Keystroke filter panel
    // =====================================================================

    private void OnKeyFilterKeyDown(object? sender, KeyEventArgs e) {
        // Escape clears the filter.
        if (e.Key == Key.Escape) {
            ClearKeyFilter();
            e.Handled = true;
            return;
        }

        // Ignore bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) {
            e.Handled = true;
            return;
        }

        _keyFilter = new KeyGesture(e.Key, e.KeyModifiers);
        _keyFilterText.Text = _keyFilter.ToString();
        _keyFilterText.Foreground = _theme.EditorForeground;
        _keyFilterClearBtn.IsVisible = true;
        ApplyFilter();
        e.Handled = true;
    }

    private void ClearKeyFilter() {
        _keyFilter = null;
        _keyFilterText.Text = "Filter by key\u2026";
        _keyFilterText.Foreground = _theme.SettingsDimForeground;
        _keyFilterClearBtn.IsVisible = false;
        ApplyFilter();
    }

    // =====================================================================
    // Ctrl+Scroll on command list
    // =====================================================================

    private void OnCommandListWheel(object? sender, PointerWheelEventArgs e) {
        // Always consume in the tunnel so the inner ScrollViewer never scrolls
        // on its own. We then manually scroll the right target.
        e.Handled = true;

        var outer = FindOuterScrollViewer(_commandScroll);
        var outerNeedsScroll = outer != null && outer.Extent.Height > outer.Viewport.Height;
        var scrollInner = e.KeyModifiers.HasFlag(KeyModifiers.Control) || !outerNeedsScroll;

        if (scrollInner) {
            var delta = e.Delta.Y * 50;
            _commandScroll.Offset = new Vector(
                _commandScroll.Offset.X,
                _commandScroll.Offset.Y - delta);
        } else {
            var delta = e.Delta.Y * 50;
            outer!.Offset = new Vector(
                outer.Offset.X,
                outer.Offset.Y - delta);
        }
    }

    private static ScrollViewer? FindOuterScrollViewer(Control start) {
        for (Control? c = start.Parent as Control; c != null; c = c.Parent as Control) {
            if (c is ScrollViewer sv) {
                return sv;
            }
        }
        return null;
    }

    // =====================================================================
    // Command list
    // =====================================================================

    private void BuildCommandList() {
        _commandList.Children.Clear();
        _commandRows.Clear();
        _categoryGroups.Clear();

        foreach (var category in CommandRegistry.Categories) {
            var commands = CommandRegistry.All
                .Where(c => c.Category == category)
                .ToList();
            if (commands.Count == 0) {
                continue;
            }

            // Category header (bold, no indent)
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
            Tag = "name", // for theming identification
        };

        var gestureLabel = new TextBlock {
            Text = gestureText,
            FontSize = 12,
            Foreground = _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "dim",
        };

        var descLabel = new TextBlock {
            Text = cmd.Description ?? "",
            FontSize = 12,
            Foreground = _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = "dim",
        };

        // 3-column grid: Name (flex) | Gesture (fixed) | Description (flex)
        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,260,*"),
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(gestureLabel, 1);
        Grid.SetColumn(descLabel, 2);
        grid.Children.Add(nameText);
        grid.Children.Add(gestureLabel);
        grid.Children.Add(descLabel);

        var border = new Border {
            Child = grid,
            Padding = new Thickness(24, 4, 8, 4),   // 24px left indent
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
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
        _captureClearBtn.IsVisible = false;

        // Update selection highlight
        foreach (var (border, id) in _commandRows) {
            border.Background = id == commandId
                ? _theme.SettingsRowSelection
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

    // =====================================================================
    // Key capture box
    // =====================================================================

    private void ClearCapturedGesture() {
        _capturedGesture = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";
        _captureClearBtn.IsVisible = false;
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
        _captureClearBtn.IsVisible = true;

        // Check for conflicts
        if (_selectedCommandId != null) {
            var conflict = _keyBindings.FindConflict(_capturedGesture, _selectedCommandId);
            if (conflict != null) {
                var desc = KeyBindingService.GetDescriptor(conflict);
                _conflictLabel.Text = $"Conflict: {desc?.DisplayName ?? conflict}";
            } else {
                _conflictLabel.Text = "";
            }
        }

        UpdateButtonStates();
        e.Handled = true;
    }

    // =====================================================================
    // Assign / Remove / Reset
    // =====================================================================

    private void OnAssign(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null || _capturedGesture == null) {
            return;
        }
        _keyBindings.SetBinding(_selectedCommandId, _capturedGesture);
        RefreshAfterChange();
    }

    private void OnRemove(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null) {
            return;
        }
        _keyBindings.SetBinding(_selectedCommandId, null);
        RefreshAfterChange();
    }

    private void OnReset(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null) {
            return;
        }
        _keyBindings.ResetBinding(_selectedCommandId);
        RefreshAfterChange();
    }

    private void RefreshAfterChange() {
        // Rebuild the command list to reflect updated gesture text.
        var selected = _selectedCommandId;
        BuildCommandList();
        ApplyFilter();
        ApplyTheme(_theme);
        if (selected != null) {
            SelectCommand(selected);
        }

        _capturedGesture = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";
        _captureClearBtn.IsVisible = false;
        UpdateButtonStates();

        BindingChanged?.Invoke();
    }

    private void UpdateButtonStates() {
        _assignBtn.IsEnabled = _selectedCommandId != null && _capturedGesture != null;
        _removeBtn.IsEnabled = _selectedCommandId != null
                               && _keyBindings.GetGesture(_selectedCommandId) != null;
        _resetBtn.IsEnabled = _selectedCommandId != null;
    }

    // =====================================================================
    // Combined filter: category buttons + keystroke filter
    // =====================================================================

    private void ApplyFilter() {
        foreach (var (header, category, rows) in _categoryGroups) {
            var categoryMatch = _activeCategory == null || _activeCategory == category;
            var anyVisible = false;

            foreach (var row in rows) {
                if (row.Tag is string commandId) {
                    var visible = categoryMatch;

                    // Keystroke filter: match by key (modifiers must be subset).
                    if (visible && _keyFilter != null) {
                        var cmdGesture = _keyBindings.GetGesture(commandId);
                        if (cmdGesture == null) {
                            visible = false;
                        } else {
                            visible = cmdGesture.Key == _keyFilter.Key
                                && (cmdGesture.KeyModifiers & _keyFilter.KeyModifiers)
                                    == _keyFilter.KeyModifiers;
                        }
                    }

                    row.IsVisible = visible;
                    if (visible) {
                        anyVisible = true;
                    }
                }
            }

            header.IsVisible = categoryMatch && (anyVisible || _keyFilter == null);
        }
    }

    // =====================================================================
    // Theming
    // =====================================================================

    /// <summary>
    /// Applies theme colors to the keyboard settings section.
    /// </summary>
    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;

        // Category headers
        foreach (var (header, _, _) in _categoryGroups) {
            header.Foreground = theme.EditorForeground;
        }

        // Command rows: name → foreground, dim → dim.
        foreach (var (border, cmdId) in _commandRows) {
            if (border.Child is Grid grid) {
                foreach (var child in grid.Children) {
                    if (child is TextBlock tb) {
                        tb.Foreground = tb.Tag is "dim"
                            ? theme.SettingsDimForeground
                            : theme.EditorForeground;
                    }
                }
            }

            // Re-apply selection highlight with new theme color.
            border.Background = cmdId == _selectedCommandId
                ? theme.SettingsRowSelection
                : Brushes.Transparent;
        }

        // Scroll hint
        _scrollHint.Foreground = theme.SettingsDimForeground;

        // Labels
        _shortcutLabel.Foreground = theme.EditorForeground;
        _conflictLabel.Foreground = theme.SettingsWarnForeground;

        // Capture box + its clear button
        _captureBox.Foreground = theme.EditorForeground;
        _captureClearBtn.Foreground = theme.EditorForeground;

        // Key filter panel border (unfocused state)
        if (!_keyFilterPanel.IsFocused) {
            _keyFilterPanel.BorderBrush = theme.SettingsInputBorder;
        } else {
            _keyFilterPanel.BorderBrush = theme.SettingsAccent;
        }

        // Keystroke filter text + clear button
        _keyFilterClearBtn.Foreground = theme.EditorForeground;
        _keyFilterText.Foreground = _keyFilter != null
            ? theme.EditorForeground
            : theme.SettingsDimForeground;

        // Category button active states
        UpdateCategoryButtonStyles();
    }
}
