using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Avalonia.Threading;

using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

/// <summary>
/// Custom settings section for keyboard shortcut configuration. Displays a
/// profile selector, name/shortcut filter row, a scrollable 3-column command
/// list, and a bottom row with key capture box, conflict label, and
/// assign/remove/reset buttons.
/// </summary>
public class KeyboardSettingsSection : UserControl {
    private readonly KeyBindingService _keyBindings;
    private readonly AppSettings _settings;

    // -- Toolbar --
    private readonly ComboBox _profileCombo;
    private readonly TextBox _nameFilter;
    private readonly Button _nameFilterClearBtn;
    private readonly Border _keyFilterPanel;
    private readonly TextBlock _keyFilterText;
    private readonly Button _keyFilterClearBtn;

    // -- Command list --
    private readonly StackPanel _commandList;
    private readonly ScrollViewer _commandScroll;

    // -- Error banner --
    private readonly TextBlock _errorBanner;

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
    private ChordGesture? _captured;         // single key or chord
    private KeyGesture? _keyFilter;          // keystroke filter
    private EditorTheme _theme = EditorTheme.Light;

    // Tracks all command row borders for selection highlighting.
    private readonly List<(Border border, string commandId)> _commandRows = [];
    // Tracks category headers for filtering.
    private readonly List<(TextBlock header, string category, List<Border> rows)> _categoryGroups = [];
    // Tracks command IDs with duplicate gesture conflicts.
    private HashSet<string> _duplicateCommandIds = [];

    /// <summary>
    /// Fired when any binding is changed (assigned, removed, or reset).
    /// </summary>
    public event Action? BindingChanged;

    public KeyboardSettingsSection(KeyBindingService keyBindings, AppSettings settings) {
        _keyBindings = keyBindings;
        _settings = settings;

        // =====================================================================
        // Toolbar: profile selector
        // =====================================================================
        var toolbar = new WrapPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };

        // Profile selector
        _profileCombo = new ComboBox {
            FontSize = 12,
            Padding = new Thickness(8, 3),
            Margin = new Thickness(0, 0, 8, 4),
            MinWidth = 130,
        };
        foreach (var id in ProfileLoader.ProfileIds) {
            var displayName = ProfileLoader.GetDisplayName(id);
            if (id == "default") displayName = "Default Profile";
            _profileCombo.Items.Add(new ComboBoxItem {
                Content = displayName,
                Tag = id,
            });
        }
        // Select current profile.
        var activeId = _keyBindings.ActiveProfileId;
        for (var i = 0; i < ProfileLoader.ProfileIds.Count; i++) {
            if (ProfileLoader.ProfileIds[i] == activeId) {
                _profileCombo.SelectedIndex = i;
                break;
            }
        }
        _profileCombo.SelectionChanged += OnProfileSelectionChanged;
        toolbar.Children.Add(_profileCombo);

        // Name filter (text search for command display names)
        _nameFilter = new TextBox {
            Watermark = "Search commands\u2026",
            FontSize = 13,
            MinWidth = 160,
        };
        _nameFilterClearBtn = new Button {
            Content = "\u2715",
            FontSize = 11,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        _nameFilterClearBtn.Click += (_, _) => {
            _nameFilter.Text = "";
            // ApplyFilter fires via PropertyChanged
        };
        _nameFilter.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty) {
                _nameFilterClearBtn.IsVisible = !string.IsNullOrEmpty(_nameFilter.Text);
                ApplyFilter();
            }
        };

        // Name filter and key filter are placed in a separate row below the
        // toolbar, aligned with the command list columns (see filterRow below).

        // Keystroke filter panel (focusable border, not a TextBox)
        _keyFilterText = new TextBlock {
            Text = "Filter by shortcut\u2026",
            FontSize = 13,
            Foreground = _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 2),
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

        // Filter row: matches the 3-column grid of command rows (*,130,*).
        // Name filter sits in column 0 (aligned with command names), key
        // filter sits in column 1 (aligned with Keystroke column).
        var nameFilterWrapper = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };
        Grid.SetColumn(_nameFilter, 0);
        Grid.SetColumnSpan(_nameFilter, 2);
        Grid.SetColumn(_nameFilterClearBtn, 1);
        nameFilterWrapper.Children.Add(_nameFilter);
        nameFilterWrapper.Children.Add(_nameFilterClearBtn);

        var filterRow = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,150,*"),
            Margin = new Thickness(24, 0, 8, 4),
            Width = 600,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(nameFilterWrapper, 0);
        Grid.SetColumn(_keyFilterPanel, 1);
        filterRow.Children.Add(nameFilterWrapper);
        filterRow.Children.Add(_keyFilterPanel);

        // =====================================================================
        // Command list (scrollable)
        // =====================================================================
        _commandList = new StackPanel { Spacing = 0 };
        _commandScroll = new ScrollViewer {
            Content = _commandList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
            Width = 600,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // Ctrl+Scroll routes to inner scrollviewer, plain scroll bubbles up.
        _commandScroll.AddHandler(PointerWheelChangedEvent, OnCommandListWheel,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // =====================================================================
        // Error banner (duplicate gesture warnings)
        // =====================================================================
        _errorBanner = new TextBlock {
            FontSize = 12,
            Foreground = _theme.SettingsWarnForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false,
        };

        // =====================================================================
        // Shortcut display
        // =====================================================================
        _shortcutLabel = new TextBlock {
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 4),
        };

        // =====================================================================
        // Bottom controls: capture box + clear + conflict + buttons
        // =====================================================================
        _captureBox = new TextBox {
            Watermark = "Press shortcut keys\u2026",
            FontSize = 13,
            IsReadOnly = true,
            MinWidth = 180,
        };
        _captureBox.AddHandler(
            KeyDownEvent, OnCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _captureClearBtn = new Button {
            Content = "\u2715",
            FontSize = 11,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        _captureClearBtn.Click += (_, _) => ClearCapturedGesture();

        // Wrap capture box + clear button in a Grid so the ✕ sits inside the box.
        var captureWrapper = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            MaxWidth = 260,
        };
        Grid.SetColumn(_captureBox, 0);
        Grid.SetColumnSpan(_captureBox, 2); // TextBox spans full width
        Grid.SetColumn(_captureClearBtn, 1); // Button overlays on the right
        captureWrapper.Children.Add(_captureBox);
        captureWrapper.Children.Add(_captureClearBtn);

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
        bottomRow.Children.Add(captureWrapper);
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
            IsVisible = false, // Shown dynamically when outer page needs scrolling.
        };

        var root = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(filterRow, Dock.Top);
        DockPanel.SetDock(_scrollHint, Dock.Top);
        DockPanel.SetDock(_errorBanner, Dock.Bottom);
        DockPanel.SetDock(_shortcutLabel, Dock.Bottom);
        DockPanel.SetDock(bottomRow, Dock.Bottom);
        // Order matters: dock top/bottom chrome first, then _commandScroll
        // fills the remaining space as the last (undocked) child.
        root.Children.Add(toolbar);
        root.Children.Add(filterRow);
        root.Children.Add(_scrollHint);
        root.Children.Add(bottomRow);
        root.Children.Add(_shortcutLabel);
        root.Children.Add(_errorBanner);
        root.Children.Add(_commandScroll);

        Content = root;

        BuildCommandList();
        UpdateButtonStates();
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
        _keyFilterText.Text = "Filter by shortcut\u2026";
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

        // Update hint synchronously during scroll (layout is already current).
        _scrollHint.IsVisible = outerNeedsScroll;

        var delta = e.Delta.Y * 50;
        if (scrollInner) {
            _commandScroll.Offset = new Vector(
                _commandScroll.Offset.X,
                _commandScroll.Offset.Y - delta);
        } else {
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
        DetectDuplicateGestures();

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
        var gesture2Text = _keyBindings.GetGesture2Text(cmd.Id) ?? "";

        var g1Modified = IsBindingModified(cmd.Id, 1);
        var g2Modified = IsBindingModified(cmd.Id, 2);
        var isDuplicate = _duplicateCommandIds.Contains(cmd.Id);

        var nameText = new TextBlock {
            Text = cmd.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = isDuplicate ? "error" : "name",
        };

        var gestureLabel = new TextBlock {
            Text = gestureText,
            FontSize = 12,
            Foreground = isDuplicate ? _theme.SettingsWarnForeground
                : g1Modified ? _theme.SettingsAccent
                : _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = isDuplicate ? "error" : g1Modified ? "modified" : "dim",
        };

        var gesture2Label = new TextBlock {
            Text = gesture2Text,
            FontSize = 12,
            Foreground = isDuplicate ? _theme.SettingsWarnForeground
                : g2Modified ? _theme.SettingsAccent
                : _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = isDuplicate ? "error" : g2Modified ? "modified" : "dim",
        };

        // 3-column grid: Name (flex) | Gesture (fixed) | Gesture2 (flex)
        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,130,*"),
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(gestureLabel, 1);
        Grid.SetColumn(gesture2Label, 2);
        grid.Children.Add(nameText);
        grid.Children.Add(gestureLabel);
        grid.Children.Add(gesture2Label);

        var isModified = g1Modified || g2Modified;
        var border = new Border {
            Child = grid,
            Padding = new Thickness(21, 4, 8, 4),   // 21px left padding (24 - 3 border)
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = isModified ? _theme.SettingsAccent : Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = cmd.Id,
        };

        border.PointerPressed += (_, e) => {
            SelectCommand(cmd.Id);
            e.Handled = true;
        };

        border.DoubleTapped += (_, e) => {
            SelectCommand(cmd.Id);
            _captureBox.Focus();
            e.Handled = true;
        };

        return border;
    }

    private void SelectCommand(string commandId) {
        _selectedCommandId = commandId;
        _captured = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";
        _captureClearBtn.IsVisible = false;

        // Update selection highlight
        foreach (var (border, id) in _commandRows) {
            border.Background = id == commandId
                ? _theme.SettingsRowSelection
                : Brushes.Transparent;
        }

        UpdateShortcutLabel(commandId);
        UpdateButtonStates();
    }

    private void UpdateShortcutLabel(string commandId) {
        var gesture1 = _keyBindings.GetGestureText(commandId);
        var gesture2 = _keyBindings.GetGesture2Text(commandId);
        var desc = KeyBindingService.GetDescriptor(commandId);
        var name = desc?.DisplayName ?? commandId;

        var primary = gesture1 ?? "(none)";
        var secondary = gesture2 ?? "(none)";
        _shortcutLabel.Text = $"{name}:  Primary: {primary}  |  Secondary: {secondary}";
    }

    // =====================================================================
    // Key capture box
    // =====================================================================

    private void ClearCapturedGesture() {
        _captured = null;
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

        var gesture = new KeyGesture(e.Key, e.KeyModifiers);

        // Auto-detect chord: if a single gesture was already captured, the
        // second key press completes a chord. If a chord was captured, the
        // next key starts over as a new single gesture.
        if (_captured is { IsChord: true }) {
            // Chord was captured — start over with new single gesture.
            _captured = new ChordGesture(gesture);
        } else if (_captured != null) {
            // Single gesture was captured — second key completes chord.
            _captured = new ChordGesture(_captured.First, gesture);
        } else {
            // Nothing captured — first key press.
            _captured = new ChordGesture(gesture);
        }

        _captureBox.Text = _captured.ToString();
        _captureClearBtn.IsVisible = true;

        // Check for conflicts.
        if (_selectedCommandId != null) {
            var conflict = _keyBindings.FindConflict(_captured, _selectedCommandId);
            _conflictLabel.Text = conflict != null
                ? $"Conflict: {KeyBindingService.GetDescriptor(conflict)?.DisplayName ?? conflict}"
                : "";
        }

        UpdateButtonStates();
        e.Handled = true;
    }

    // =====================================================================
    // Assign / Remove / Reset
    // =====================================================================

    private void OnAssign(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null || _captured == null) return;
        // Auto-slot: fill the first available slot. If primary is empty use it,
        // otherwise use (overwrite) secondary.
        var hasPrimary = _keyBindings.GetGesture(_selectedCommandId) != null;
        if (!hasPrimary) {
            _keyBindings.SetBinding(_selectedCommandId, _captured);
        } else {
            _keyBindings.SetBinding2(_selectedCommandId, _captured);
        }
        RefreshAfterChange();
    }

    private void OnRemove(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null) return;
        // Remove the last-filled slot: secondary first, then primary.
        var hasSecondary = _keyBindings.GetGesture2(_selectedCommandId) != null;
        if (hasSecondary) {
            _keyBindings.SetBinding2(_selectedCommandId, null);
        } else {
            _keyBindings.SetBinding(_selectedCommandId, null);
        }
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

        _captured = null;
        _captureBox.Text = "";
        _conflictLabel.Text = "";
        _captureClearBtn.IsVisible = false;
        UpdateButtonStates();

        BindingChanged?.Invoke();
    }

    private void UpdateButtonStates() {
        _assignBtn.IsEnabled = _selectedCommandId != null && _captured != null;

        if (_selectedCommandId != null) {
            var hasAny = _keyBindings.GetGesture(_selectedCommandId) != null
                || _keyBindings.GetGesture2(_selectedCommandId) != null;
            _removeBtn.IsEnabled = hasAny;
        } else {
            _removeBtn.IsEnabled = false;
        }

        _resetBtn.IsEnabled = _selectedCommandId != null;
    }

    // =====================================================================
    // Combined filter: category buttons + keystroke filter
    // =====================================================================

    private void ApplyFilter() {
        var nameSearch = _nameFilter.Text?.Trim();
        var hasNameFilter = !string.IsNullOrEmpty(nameSearch);

        foreach (var (header, category, rows) in _categoryGroups) {
            var anyVisible = false;

            foreach (var row in rows) {
                if (row.Tag is string commandId) {
                    var visible = true;

                    // Name filter: match display name or category (case-insensitive substring).
                    if (visible && hasNameFilter) {
                        var desc = KeyBindingService.GetDescriptor(commandId);
                        visible = desc != null
                            && (desc.DisplayName.Contains(nameSearch!,
                                    StringComparison.OrdinalIgnoreCase)
                                || desc.Category.Contains(nameSearch!,
                                    StringComparison.OrdinalIgnoreCase));
                    }

                    // Keystroke filter: match by key (modifiers must be subset).
                    // Checks both gesture slots; chords match on either key.
                    if (visible && _keyFilter != null) {
                        visible = MatchesKeyFilter(_keyBindings.GetGesture(commandId))
                            || MatchesKeyFilter(_keyBindings.GetGesture2(commandId));
                    }

                    row.IsVisible = visible;
                    if (visible) {
                        anyVisible = true;
                    }
                }
            }

            header.IsVisible = anyVisible || (_keyFilter == null && !hasNameFilter);
        }

        UpdateScrollHintVisibility();
    }

    private bool MatchesKeyFilter(ChordGesture? gesture) {
        if (gesture == null || _keyFilter == null) return false;
        if (gesture.IsChord) {
            return MatchesSingleKey(gesture.First)
                || MatchesSingleKey(gesture.Second!);
        }
        return MatchesSingleKey(gesture.First);
    }

    private bool MatchesSingleKey(KeyGesture g) =>
        _keyFilter != null
        && g.Key == _keyFilter.Key
        && (g.KeyModifiers & _keyFilter.KeyModifiers) == _keyFilter.KeyModifiers;

    // =====================================================================
    // Scroll hint visibility
    // =====================================================================

    /// <summary>
    /// Recalculates whether the "Ctrl+Scroll" hint should be visible.
    /// Deferred to run after layout so extent/viewport sizes are current.
    /// </summary>
    private void UpdateScrollHintVisibility() {
        Dispatcher.UIThread.Post(() => {
            var outer = FindOuterScrollViewer(_commandScroll);
            _scrollHint.IsVisible = outer != null
                && outer.Extent.Height > outer.Viewport.Height;
        });
    }

    // =====================================================================
    // Profile selection
    // =====================================================================

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_profileCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string profileId) {
            return;
        }
        if (profileId == _keyBindings.ActiveProfileId) {
            return;
        }
        // Keep user overrides — they represent intentional customizations that
        // should survive a profile switch. The new profile's defaults fill in
        // for any commands that have no override.
        _keyBindings.SetProfile(profileId);
        RefreshAfterChange();
    }

    // =====================================================================
    // Modified / duplicate detection
    // =====================================================================

    /// <summary>
    /// Returns true if the user has a custom override for the given command
    /// in the given slot (differing from or absent in the active profile).
    /// </summary>
    private bool IsBindingModified(string commandId, int slot) {
        var overrides = slot == 1
            ? _settings.KeyBindingOverrides
            : _settings.KeyBinding2Overrides;
        return overrides != null && overrides.ContainsKey(commandId);
    }

    /// <summary>
    /// Scans all effective bindings for duplicate single-key gestures and
    /// duplicate chords. Populates <see cref="_duplicateCommandIds"/> and
    /// updates the error banner.
    /// </summary>
    private void DetectDuplicateGestures() {
        _duplicateCommandIds = [];
        var singleKeySeen = new Dictionary<KeyGesture, string>(KeyGestureComparer.Instance);
        var chordSeen = new Dictionary<(int, int, int, int), string>();
        var errors = new List<string>();

        foreach (var cmd in CommandRegistry.All) {
            CheckGestureForDuplicate(cmd.Id, _keyBindings.GetGesture(cmd.Id),
                singleKeySeen, chordSeen, errors);
            CheckGestureForDuplicate(cmd.Id, _keyBindings.GetGesture2(cmd.Id),
                singleKeySeen, chordSeen, errors);
        }

        _errorBanner.IsVisible = errors.Count > 0;
        _errorBanner.Text = errors.Count > 0
            ? string.Join("  |  ", errors)
            : "";
    }

    private void CheckGestureForDuplicate(string cmdId, ChordGesture? gesture,
        Dictionary<KeyGesture, string> singleKeySeen,
        Dictionary<(int, int, int, int), string> chordSeen,
        List<string> errors) {
        if (gesture == null) return;

        if (gesture.IsChord) {
            var key = (
                (int)gesture.First.Key, (int)gesture.First.KeyModifiers,
                (int)gesture.Second!.Key, (int)gesture.Second.KeyModifiers);
            if (chordSeen.TryGetValue(key, out var existing)) {
                _duplicateCommandIds.Add(cmdId);
                _duplicateCommandIds.Add(existing);
                var desc1 = KeyBindingService.GetDescriptor(existing)?.DisplayName ?? existing;
                var desc2 = KeyBindingService.GetDescriptor(cmdId)?.DisplayName ?? cmdId;
                errors.Add($"{gesture}: {desc1} / {desc2}");
            } else {
                chordSeen[key] = cmdId;
            }
        } else {
            if (singleKeySeen.TryGetValue(gesture.First, out var existing)) {
                _duplicateCommandIds.Add(cmdId);
                _duplicateCommandIds.Add(existing);
                var desc1 = KeyBindingService.GetDescriptor(existing)?.DisplayName ?? existing;
                var desc2 = KeyBindingService.GetDescriptor(cmdId)?.DisplayName ?? cmdId;
                errors.Add($"{gesture}: {desc1} / {desc2}");
            } else {
                singleKeySeen[gesture.First] = cmdId;
            }
        }
    }

    // =====================================================================
    // Sizing
    // =====================================================================

    /// <summary>
    /// Sizes the command list to fill the given height, subtracting the
    /// heights of all sibling controls (toolbar, hint, label, buttons).
    /// Called by <see cref="SettingsControl"/> when the viewport changes.
    /// </summary>
    public void SetAvailableHeight(double height) {
        // The root DockPanel handles internal layout: toolbar and bottom row
        // are docked, and _commandScroll fills the remaining space.
        // We just set the section's overall height; DockPanel does the rest.
        Height = Math.Max(200, height);
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

        // Command rows: name/modified/error/dim + left modified bar.
        foreach (var (border, cmdId) in _commandRows) {
            if (border.Child is Grid grid) {
                foreach (var child in grid.Children) {
                    if (child is TextBlock tb) {
                        tb.Foreground = tb.Tag switch {
                            "dim" => theme.SettingsDimForeground,
                            "modified" => theme.SettingsAccent,
                            "error" => theme.SettingsWarnForeground,
                            _ => theme.EditorForeground,
                        };
                    }
                }
            }

            // Re-apply left modified bar with new accent color.
            if (border.BorderBrush is SolidColorBrush scb && scb.Color.A > 0) {
                border.BorderBrush = theme.SettingsAccent;
            }

            // Re-apply selection highlight with new theme color.
            border.Background = cmdId == _selectedCommandId
                ? theme.SettingsRowSelection
                : Brushes.Transparent;
        }

        // Error banner
        _errorBanner.Foreground = theme.SettingsWarnForeground;

        // Scroll hint
        _scrollHint.Foreground = theme.SettingsDimForeground;

        // Labels
        _shortcutLabel.Foreground = theme.EditorForeground;
        _conflictLabel.Foreground = theme.SettingsWarnForeground;

        // Name filter + its clear button
        _nameFilter.Foreground = theme.EditorForeground;
        _nameFilterClearBtn.Foreground = theme.EditorForeground;

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

    }
}
