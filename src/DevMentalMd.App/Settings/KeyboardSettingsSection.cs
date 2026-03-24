using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

/// <summary>
/// Custom settings section for keyboard shortcut configuration. Layout is
/// defined in KeyboardSettingsSection.axaml — profile selector, name/shortcut
/// filter row, scrollable 3-column command list, and a bottom row with key
/// capture box, conflict label, and assign/remove/reset buttons.
/// </summary>
public partial class KeyboardSettingsSection : UserControl {
    private readonly KeyBindingService _keyBindings;
    private readonly AppSettings _settings;

    // -- State --
    private string? _selectedCommandId;
    private ChordGesture? _captured;         // single key or chord
    private bool _showModifiedOnly;          // "M" toggle: show only customised rows
    private bool _reservedConflict;           // captured gesture conflicts with a Menu.* reserved binding
    private EditorTheme _theme = EditorTheme.Light;
    private KeyGesture? _findFirstGesture;   // first key of a potential chord in FindShortcut

    // Tracks all command row borders for selection highlighting.
    private readonly List<(Border border, string commandId)> _commandRows = [];
    // Tracks category headers for filtering.
    private readonly List<(TextBlock header, string category, List<Border> rows)> _categoryGroups = [];

    /// <summary>
    /// Fired when any binding is changed (assigned, removed, or reset).
    /// </summary>
    public event Action? BindingChanged;

    /// <summary>Design-time constructor for AXAML previewer.</summary>
    public KeyboardSettingsSection() {
        _keyBindings = null!;
        _settings = null!;
        InitializeComponent();
    }

    public KeyboardSettingsSection(KeyBindingService keyBindings, AppSettings settings) {
        _keyBindings = keyBindings;
        _settings = settings;
        InitializeComponent();

        // =====================================================================
        // Populate profile selector
        // =====================================================================
        foreach (var id in ProfileLoader.ProfileIds) {
            var displayName = ProfileLoader.GetDisplayName(id);
            ProfileCombo.Items.Add(new ComboBoxItem {
                Content = displayName,
                Tag = id,
            });
        }
        var activeId = _keyBindings.ActiveProfileId;
        for (var i = 0; i < ProfileLoader.ProfileIds.Count; i++) {
            if (ProfileLoader.ProfileIds[i] == activeId) {
                ProfileCombo.SelectedIndex = i;
                break;
            }
        }
        ProfileCombo.SelectionChanged += OnProfileSelectionChanged;
        ProfileCombo.AddHandler(PointerPressedEvent, (_, e) => {
            if (!e.GetCurrentPoint(ProfileCombo).Properties.IsLeftButtonPressed)
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // =====================================================================
        // Wire event handlers
        // =====================================================================
        NameFilter.PropertyChanged += (_, e) => {
            if (e.Property == Controls.DMTextBox.TextProperty) {
                ApplyFilter();
            }
        };

        // InnerTextBox is null until OnApplyTemplate runs (after first layout),
        // so defer handler wiring until the template is applied.
        FindShortcut.TemplateApplied += (_, _) => {
            FindShortcut.InnerTextBox?.AddHandler(
                KeyDownEvent, OnFindShortcutKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            if (FindShortcut.InnerTextBox != null)
                FindShortcut.InnerTextBox.ContextMenu = null;
        };

        // Clear button on FindShortcut should reset chord state.
        FindShortcut.PropertyChanged += (_, e) => {
            if (e.Property == Controls.DMTextBox.TextProperty
                && string.IsNullOrEmpty(FindShortcut.Text)
                && _findFirstGesture != null) {
                _findFirstGesture = null;
            }
        };

        ModifiedFilterBtn.IsCheckedChanged += (_, _) => {
            _showModifiedOnly = ModifiedFilterBtn.IsChecked == true;
            ApplyFilter();
        };

        CommandScroll.AddHandler(PointerWheelChangedEvent, OnCommandListWheel,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        CaptureBox.TemplateApplied += (_, _) => {
            CaptureBox.InnerTextBox?.AddHandler(
                KeyDownEvent, OnCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            if (CaptureBox.InnerTextBox != null)
                CaptureBox.InnerTextBox.ContextMenu = null;
        };

        // When the clear button empties the capture box, reset the captured
        // gesture and conflict message so the next keypress starts fresh.
        CaptureBox.PropertyChanged += (_, e) => {
            if (e.Property == Controls.DMTextBox.TextProperty
                && string.IsNullOrEmpty(CaptureBox.Text)
                && _captured != null) {
                _captured = null;
                SetConflict(null);
                UpdateButtonStates();
            }
        };

        AssignBtn.Click += OnAssign;

        // =====================================================================
        // Initial build
        // =====================================================================
        BuildCommandList();
        UpdateButtonStates();
        ApplyTheme(_theme);
        
        // Guard against an unbounded layout before SetAvailableHeight is first called.
        CommandScroll.MaxHeight = 500;
        CommandScroll.MinHeight = 500;
    }


    // =====================================================================
    // Ctrl+Scroll on command list
    // =====================================================================

    private void OnCommandListWheel(object? sender, PointerWheelEventArgs e) {
        // Always consume in the tunnel so the inner ScrollViewer never scrolls
        // on its own. We then manually scroll the right target.
        e.Handled = true;

        var outer = FindOuterScrollViewer(CommandScroll);
        var outerNeedsScroll = outer != null && outer.Extent.Height > outer.Viewport.Height;
        var scrollInner = e.KeyModifiers.HasFlag(KeyModifiers.Control) || !outerNeedsScroll;

        var delta = e.Delta.Y * 50;
        if (scrollInner) {
            CommandScroll.Offset = new Vector(
                CommandScroll.Offset.X,
                CommandScroll.Offset.Y - delta);
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
        CommandList.Children.Clear();
        _commandRows.Clear();
        _categoryGroups.Clear();

        foreach (var category in _keyBindings.Registry.Categories) {
            var commands = _keyBindings.Registry.All
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
            CommandList.Children.Add(header);

            var rowsInCategory = new List<Border>();

            foreach (var cmd in commands) {
                var row = CreateCommandRow(cmd);
                CommandList.Children.Add(row);
                _commandRows.Add((row, cmd.Id));
                rowsInCategory.Add(row);
            }

            _categoryGroups.Add((header, category, rowsInCategory));
        }
    }

    private Border CreateCommandRow(Command cmd) {
        var gestureText = _keyBindings.GetGestureText(cmd.Id) ?? "";
        var gesture2Text = _keyBindings.GetGesture2Text(cmd.Id) ?? "";

        var g1Modified = IsBindingModified(cmd.Id, 1);
        var g2Modified = IsBindingModified(cmd.Id, 2);
        var isReserved = cmd.Id.StartsWith("Menu.", StringComparison.Ordinal);

        var nameText = new TextBlock {
            Text = cmd.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "name",
        };

        var gestureLabel = new TextBlock {
            Text = gestureText,
            FontSize = 12,
            Foreground = g1Modified ? _theme.SettingsAccent : _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = g1Modified ? "modified" : "dim",
        };

        var gesture2Label = new TextBlock {
            Text = gesture2Text,
            FontSize = 12,
            Foreground = g2Modified ? _theme.SettingsAccent : _theme.SettingsDimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = g2Modified ? "modified" : "dim",
        };

        var isModified = g1Modified || g2Modified;

        // Per-slot action button: reset (undo) if modified, remove (trash)
        // if bound but unmodified, hidden if empty. Reserved (Menu.*) rows
        // never show buttons.
        var btn1 = CreateSlotButton(cmd.Id, 1, g1Modified, gestureText, isReserved);
        var btn2 = CreateSlotButton(cmd.Id, 2, g2Modified, gesture2Text, isReserved);

        // 6-column grid: Name | Gesture1 | Btn1 | Gesture2 | Btn2
        // All non-name columns are fixed width so columns stay aligned.
        var grid = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,104,26,104,26"),
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(gestureLabel, 1);
        Grid.SetColumn(btn1, 2);
        Grid.SetColumn(gesture2Label, 3);
        Grid.SetColumn(btn2, 4);
        grid.Children.Add(nameText);
        grid.Children.Add(gestureLabel);
        grid.Children.Add(btn1);
        grid.Children.Add(gesture2Label);
        grid.Children.Add(btn2);

        var border = new Border {
            Child = grid,
            Padding = new Thickness(21, 4, 4, 4),   // 21px left padding (24 - 3 border)
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = isModified ? _theme.SettingsAccent : Brushes.Transparent,
            Background = Brushes.Transparent,
            Tag = cmd.Id,
        };

        border.Tapped += (_, e) => {
            SelectCommand(cmd.Id);
            e.Handled = true;
        };

        border.DoubleTapped += (_, e) => {
            SelectCommand(cmd.Id);
            CaptureBox.Focus();
            e.Handled = true;
        };

        return border;
    }

    /// <summary>
    /// Creates the per-slot action button for a command row. Returns a reset
    /// (undo) button if the slot is modified (even if cleared to empty), a
    /// remove (trash) button if the slot has a binding but is unmodified, or
    /// a hidden placeholder otherwise. Reserved (Menu.*) commands always get
    /// a hidden placeholder.
    /// </summary>
    private Control CreateSlotButton(string cmdId, int slot, bool isModified,
                                     string gestureText, bool isReserved) {
        if (isReserved) {
            // Hidden fixed-width placeholder so the column stays aligned.
            return new Border { Width = 22, Height = 22 };
        }

        if (isModified) {
            var btn = SettingRowFactory.CreateResetIconButton();
            btn.Margin = new Thickness(0);
            btn.Click += (_, _) => {
                var defaultGesture = _keyBindings.GetProfileDefault(cmdId, slot);
                if (defaultGesture != null) {
                    RemoveConflict(defaultGesture, cmdId);
                }
                if (slot == 1) {
                    _keyBindings.SetBinding(cmdId, _keyBindings.GetProfileDefault(cmdId, 1));
                } else {
                    _keyBindings.SetBinding2(cmdId, _keyBindings.GetProfileDefault(cmdId, 2));
                }
                RefreshAfterChange();
            };
            return btn;
        }

        if (string.IsNullOrEmpty(gestureText)) {
            // No binding and not modified — hidden placeholder.
            return new Border { Width = 22, Height = 22 };
        }

        var removeBtn = SettingRowFactory.CreateRemoveIconButton();
        removeBtn.Margin = new Thickness(0);
        removeBtn.Click += (_, _) => {
            if (slot == 1) {
                _keyBindings.SetBinding(cmdId, null);
            } else {
                _keyBindings.SetBinding2(cmdId, null);
            }
            RefreshAfterChange();
        };
        return removeBtn;
    }

    private void SelectCommand(string commandId) {
        _selectedCommandId = commandId;
        _captured = null;
        CaptureBox.Text = "";
        SetConflict(null);

        // Update selection highlight
        foreach (var (border, id) in _commandRows) {
            border.Background = id == commandId
                ? _theme.SettingsRowSelection
                : Brushes.Transparent;
        }

        UpdateButtonStates();
    }

    // =====================================================================
    // Key capture box
    // =====================================================================

    private void ClearCapturedGesture() {
        _captured = null;
        CaptureBox.Text = "";
        SetConflict(null);
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

        // Bare Enter triggers Assign; Enter with modifiers (e.g. Ctrl+Enter)
        // is a valid shortcut and falls through to normal capture.
        if (e.Key is Key.Return or Key.Enter && e.KeyModifiers == KeyModifiers.None) {
            e.Handled = true;
            OnAssign(sender, e);
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

        CaptureBox.Text = _captured.ToString();

        // Check for conflicts.
        if (_selectedCommandId != null) {
            SetConflict(_keyBindings.FindConflict(_captured, _selectedCommandId));
        }

        UpdateButtonStates();
        e.Handled = true;
    }

    // =====================================================================
    // Find shortcut box
    // =====================================================================

    private void ClearFindShortcut() {
        _findFirstGesture = null;
        FindShortcut.Text = "";
    }

    private void OnFindShortcutKeyDown(object? sender, KeyEventArgs e) {
        // Ignore bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) {
            e.Handled = true;
            return;
        }

        var gesture = new KeyGesture(e.Key, e.KeyModifiers);

        if (_findFirstGesture != null) {
            // Second key press — try to resolve as a chord.
            var chord = new ChordGesture(_findFirstGesture, gesture);
            FindShortcut.Text = chord.ToString();
            var commandId = _keyBindings.ResolveChord(_findFirstGesture, e.Key, e.KeyModifiers);
            if (commandId != null) {
                SelectCommandAndScrollTo(commandId);
            }
            // Chord is complete (matched or not) — clear.
            ClearFindShortcut();
        } else {
            // First key press.
            FindShortcut.Text = gesture.ToString();

            if (_keyBindings.IsChordPrefix(e.Key, e.KeyModifiers)) {
                // Could be the start of a chord — wait for second key.
                _findFirstGesture = gesture;
            } else {
                // Not a chord prefix — try single-key resolve.
                var commandId = _keyBindings.Resolve(e.Key, e.KeyModifiers);
                if (commandId != null) {
                    SelectCommandAndScrollTo(commandId);
                }
                // Done (matched or not) — clear.
                ClearFindShortcut();
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Selects a command row and scrolls it into view in the command list.
    /// </summary>
    private void SelectCommandAndScrollTo(string commandId) {
        SelectCommand(commandId);

        // Find the row border and scroll it into view.
        foreach (var (border, id) in _commandRows) {
            if (id == commandId) {
                // Use BringIntoView to scroll the CommandScroll so the row is visible.
                border.BringIntoView();
                break;
            }
        }
    }

    // =====================================================================
    // Assign / Remove / Reset
    // =====================================================================

    private void OnAssign(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (_selectedCommandId == null || _captured == null) return;
        // Menu.* pseudo-commands have fixed bindings — cannot be reassigned.
        if (_selectedCommandId.StartsWith("Menu.", StringComparison.Ordinal)) return;

        // If the captured gesture is already bound to another command, evict it.
        RemoveConflict(_captured, _selectedCommandId);

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
        CaptureBox.Text = "";
        SetConflict(null);
        UpdateButtonStates();

        BindingChanged?.Invoke();
    }

    private void UpdateButtonStates() {
        AssignBtn.IsEnabled = _selectedCommandId != null && _captured != null
            && !_reservedConflict
            && !_selectedCommandId.StartsWith("Menu.", StringComparison.Ordinal);
    }

    // =====================================================================
    // Combined filter
    // =====================================================================

    private void ApplyFilter() {
        var nameSearch = NameFilter.Text?.Trim();
        var hasNameFilter = !string.IsNullOrEmpty(nameSearch);

        foreach (var (header, category, rows) in _categoryGroups) {
            var anyVisible = false;

            foreach (var row in rows) {
                if (row.Tag is string commandId) {
                    var visible = true;

                    // Name filter: match display name or category (case-insensitive substring).
                    if (visible && hasNameFilter) {
                        var desc = _keyBindings.GetCommand(commandId);
                        visible = desc != null
                            && (desc.DisplayName.Contains(nameSearch!,
                                    StringComparison.OrdinalIgnoreCase)
                                || desc.Category.Contains(nameSearch!,
                                    StringComparison.OrdinalIgnoreCase));
                    }

                    // Modified filter: only show rows with at least one customised slot.
                    if (visible && _showModifiedOnly) {
                        visible = IsBindingModified(commandId, 1)
                            || IsBindingModified(commandId, 2);
                    }

                    row.IsVisible = visible;
                    if (visible) {
                        anyVisible = true;
                    }
                }
            }

            header.IsVisible = anyVisible || (!hasNameFilter && !_showModifiedOnly);
        }

        UpdateScrollHintVisibility();
    }

  
    // =====================================================================
    // Scroll hint visibility
    // =====================================================================

    /// <summary>
    /// Recalculates whether the "Ctrl+Scroll" hint should be visible.
    /// Deferred to run after layout so extent/viewport sizes are current.
    /// </summary>
    private void UpdateScrollHintVisibility() {
        Dispatcher.UIThread.Post(() => {
            var outer = FindOuterScrollViewer(CommandScroll);
            ScrollHint.IsVisible = outer != null
                && outer.Extent.Height > outer.Viewport.Height;
        });
    }

    // =====================================================================
    // Profile selection
    // =====================================================================

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (ProfileCombo.SelectedItem is not ComboBoxItem item
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
    /// in the given slot that differs from the active profile's default.
    /// </summary>
    private bool IsBindingModified(string commandId, int slot) {
        var overrides = slot == 1
            ? _settings.KeyBindingOverrides
            : _settings.KeyBinding2Overrides;
        if (overrides == null || !overrides.ContainsKey(commandId)) return false;
        // Compare effective gesture to profile default — if they match, the
        // override is a no-op (e.g. user removed a binding that was already
        // unbound in the profile) and shouldn't show as modified.
        var effective = slot == 1
            ? _keyBindings.GetGesture(commandId)
            : _keyBindings.GetGesture2(commandId);
        var profileDefault = _keyBindings.GetProfileDefault(commandId, slot);
        if (effective == null && profileDefault == null) return false;
        if (effective == null || profileDefault == null) return true;
        return effective.ToString() != profileDefault.ToString();
    }

    // =====================================================================
    // Sizing
    // =====================================================================

    /// <summary>
    /// Caps the command-list scroll area at the available viewport height so
    /// it can scroll internally when the full list is shown, while still
    /// auto-shrinking when the list is filtered short (no dead space).
    /// The section itself sizes to its content via the StackPanel root.
    /// Called by <see cref="SettingsControl"/> when the viewport changes.
    /// </summary>
    public void SetAvailableHeight(double height) {
        // Subtract a fixed estimate of the non-list elements (profile combo,
        // filter row, buttons, labels, margins ≈ 150px) so the list fills the
        // available space without overflowing.
        var max = Math.Max(100, height - 150);
        CommandScroll.MaxHeight = max;
        CommandScroll.MinHeight = max;
        UpdateScrollHintVisibility();
    }

    // =====================================================================
    // Conflict resolution
    // =====================================================================

    /// <summary>
    /// If <paramref name="gesture"/> is currently bound to any command other
    /// than <paramref name="excludeCommandId"/>, removes it from that command.
    /// When the evicted slot was the primary binding and a secondary exists,
    /// the secondary is promoted to primary so the command keeps a shortcut.
    /// </summary>
    private void RemoveConflict(ChordGesture gesture, string excludeCommandId) {
        var conflict = _keyBindings.FindConflict(gesture, excludeCommandId);
        if (conflict == null) return;
        var g1 = _keyBindings.GetGesture(conflict);
        var g2 = _keyBindings.GetGesture2(conflict);
        if (g1 != null && g1.ToString() == gesture.ToString()) {
            // Conflict is in slot 1: promote slot 2 into slot 1, clear slot 2.
            _keyBindings.SetBinding(conflict, g2);
            _keyBindings.SetBinding2(conflict, null);
        } else {
            // Conflict is in slot 2: just clear it.
            _keyBindings.SetBinding2(conflict, null);
        }
    }

    // =====================================================================
    // Chip helpers
    // =====================================================================

    /// <summary>
    /// Creates a rounded "chip" border that highlights a command name inline
    /// with warning text. Uses EditorForeground for text so the name stands
    /// out against the orange SettingsWarnForeground of the surrounding words.
    /// </summary>
    private Border MakeNameChip(string name, double fontSize) =>
        new() {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = _theme.SettingsInputBorder,
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(3, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "chip",
            Child = new TextBlock {
                Text = name,
                FontSize = fontSize,
                Foreground = _theme.EditorForeground,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    /// <summary>Creates a plain warning-coloured TextBlock for use beside chips.</summary>
    private TextBlock MakeWarnRun(string text, double fontSize) =>
        new() {
            Text = text,
            FontSize = fontSize,
            Foreground = _theme.SettingsWarnForeground,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// Rebuilds ConflictPanel to show "Already assigned to [chip]", or clears
    /// it when <paramref name="conflictingCommandId"/> is null.
    /// </summary>
    private void SetConflict(string? conflictingCommandId) {
        ConflictPanel.Children.Clear();
        _reservedConflict = false;
        if (conflictingCommandId == null) return;
        var name = _keyBindings.GetCommand(conflictingCommandId)?.DisplayName
            ?? conflictingCommandId;
        if (conflictingCommandId.StartsWith("Menu.", StringComparison.Ordinal)) {
            _reservedConflict = true;
            ConflictPanel.Children.Add(MakeWarnRun("This shortcut is reserved for", 11));
        } else {
            ConflictPanel.Children.Add(MakeWarnRun("Will replace the shortcut already assigned to", 11));
        }
        ConflictPanel.Children.Add(MakeNameChip(name, 11));
    }

    /// <summary>
    /// Re-applies theme colors to a single row of warning text and name chips.
    /// </summary>
    private static void ReThemeWarnRow(Panel panel, EditorTheme theme) {
        foreach (var child in panel.Children) {
            if (child is TextBlock tb)
                tb.Foreground = theme.SettingsWarnForeground;
            if (child is Border b && b.Tag is "chip") {
                b.BorderBrush = theme.SettingsInputBorder;
                if (b.Child is TextBlock inner)
                    inner.Foreground = theme.EditorForeground;
            }
        }
    }

    // =====================================================================
    // State reset
    // =====================================================================

    /// <summary>
    /// Clears transient UI state: deselects the active command, clears the
    /// capture and find-shortcut boxes, resets the modified-only filter,
    /// and scrolls to top. Called when the settings tab is closed.
    /// </summary>
    public void ResetState() {
        _selectedCommandId = null;
        _captured = null;
        _findFirstGesture = null;
        CaptureBox.Text = "";
        FindShortcut.Text = "";
        NameFilter.Text = "";
        SetConflict(null);

        // Deselect all rows.
        foreach (var (border, _) in _commandRows) {
            border.Background = Brushes.Transparent;
        }

        // Reset modified-only filter toggle and reapply filter.
        _showModifiedOnly = false;
        ModifiedFilterBtn.IsChecked = false;
        ApplyFilter();

        UpdateButtonStates();
        CommandScroll.Offset = default;
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

        // Replacement notice chips
        ReThemeWarnRow(ConflictPanel, theme);
    }
}
