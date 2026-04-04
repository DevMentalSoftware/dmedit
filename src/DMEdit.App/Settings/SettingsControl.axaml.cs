using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DMEdit.App.Commands;
using DMEdit.App.Services;

namespace DMEdit.App.Settings;

public partial class SettingsControl : UserControl {
    private AppSettings? _settings;
    private KeyBindingService? _keyBindings;
    private CommandsSettingsSection? _commandsSection;
    private readonly Dictionary<string, List<Border>> _rowsByCategory = new();
    private readonly Dictionary<string, TextBlock> _headersByCategory = new();
    private readonly List<Border> _allRows = [];
    private readonly List<TextBlock> _allHeaders = [];
    private readonly Dictionary<string, List<Border>> _dependentRows = new();
    private Button? _checkForUpdatesButton;
    private Border? _updateButtonBorder;
    private string _selectedCategory = "All Settings";

    /// <summary>
    /// Fired when any setting value changes. The string is the AppSettings property name.
    /// </summary>
    public event Action<string>? SettingChanged;

    /// <summary>
    /// Fired when any keyboard binding changes.
    /// </summary>
    public event Action? KeyBindingChanged;

    /// <summary>
    /// Fired when a menu or toolbar checkbox changes in the Commands section.
    /// </summary>
    public event Action? MenuOrToolbarChanged;

    /// <summary>
    /// Fired when the user clicks the "Check for Updates" button.
    /// </summary>
    public event Action? CheckForUpdatesRequested;

    public SettingsControl() {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the control with the given settings. Call once after construction.
    /// </summary>
    public void Initialize(AppSettings settings, KeyBindingService keyBindings) {
        _settings = settings;
        _keyBindings = keyBindings;
        BuildCategoryList();
        BuildSettingRows();
        WireSearch();
        ApplyFilter();

        ContentScroll.PropertyChanged += (_, e) => {
            if (e.Property == BoundsProperty) {
                UpdateCommandsSectionHeight();
            }
        };
    }

    private void BuildCategoryList() {
        CategoryList.Items.Add("All Settings");
        foreach (var cat in SettingsRegistry.Categories) {
            CategoryList.Items.Add(cat);
        }

        // Restore last-used category, or default to "All Settings".
        var restored = _settings?.LastSettingsPage;
        var restoredIndex = restored != null ? CategoryList.Items.IndexOf(restored) : -1;
        CategoryList.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
        _selectedCategory = CategoryList.SelectedItem as string ?? "All Settings";

        CategoryList.SelectionChanged += (_, _) => {
            if (CategoryList.SelectedItem is string cat) {
                _selectedCategory = cat;
                if (_settings != null) {
                    _settings.LastSettingsPage = cat == "All Settings" ? null : cat;
                    _settings.ScheduleSave();
                }
                ApplyFilter();
            }
        };
    }

    private void BuildSettingRows() {
        if (_settings is null) return;

        foreach (var cat in SettingsRegistry.Categories) {
            // "Commands" category gets a custom section instead of standard rows.
            if (cat == "Commands") {
                _rowsByCategory[cat] = [];
                var kbHeader = new TextBlock {
                    Text = cat,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(12, 16, 12, 4),
                    Tag = cat,
                };
                _allHeaders.Add(kbHeader);
                _headersByCategory[cat] = kbHeader;
                SettingsContent.Children.Add(kbHeader);
                if (_keyBindings != null) {
                    _commandsSection = new CommandsSettingsSection(_keyBindings, _settings);
                    _commandsSection.IsVisible = false;
                    _commandsSection.BindingChanged += () => KeyBindingChanged?.Invoke();
                    _commandsSection.MenuOrToolbarChanged += () => MenuOrToolbarChanged?.Invoke();
                    SettingsContent.Children.Add(_commandsSection);
                }
                continue;
            }

            _rowsByCategory[cat] = [];

            // Category header
            var header = new TextBlock {
                Text = cat,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(12, 16, 12, 4),
                Tag = cat,
            };
            _allHeaders.Add(header);
            _headersByCategory[cat] = header;
            SettingsContent.Children.Add(header);

            // Font row at the top of Display (composite, not a standard descriptor).
            if (cat == "Display") {
                var fontRow = SettingRowFactory.CreateFontRow(_settings, key => {
                    SettingChanged?.Invoke(key);
                });
                _rowsByCategory[cat].Add(fontRow);
                _allRows.Add(fontRow);
                SettingsContent.Children.Add(fontRow);
            }

            // Setting rows for this category
            var showHidden = string.Equals(
                Environment.GetEnvironmentVariable("DMEDIT_DEVMODE"),
                "true", StringComparison.OrdinalIgnoreCase);
            var descriptors = SettingsRegistry.All.Where(d => d.Category == cat && (!d.Hidden || showHidden));
            foreach (var desc in descriptors) {
                var row = SettingRowFactory.CreateRow(desc, _settings, key => {
                    SettingChanged?.Invoke(key);
                    UpdateDependentRows(key);
                });
                _rowsByCategory[cat].Add(row);
                _allRows.Add(row);
                SettingsContent.Children.Add(row);

                if (desc.EnabledWhenKey is { } depKey) {
                    if (!_dependentRows.TryGetValue(depKey, out var list)) {
                        list = [];
                        _dependentRows[depKey] = list;
                    }
                    list.Add(row);
                    UpdateRowEnabled(row, depKey);
                }
            }

            // "Update to …" button in the Advanced section — hidden until
            // an update is discovered.
            if (cat == "Advanced") {
                _checkForUpdatesButton = new Button {
                    Content = "Update",
                    Margin = new Thickness(12, 8, 12, 4),
                    Padding = new Thickness(12, 4),
                    IsVisible = false,
                };
                _checkForUpdatesButton.Click += (_, _) => CheckForUpdatesRequested?.Invoke();
                var btnBorder = new Border {
                    Child = _checkForUpdatesButton,
                    Padding = new Thickness(0),
                    Tag = "updateButton",
                };
                _updateButtonBorder = btnBorder;
                _rowsByCategory[cat].Add(btnBorder);
                _allRows.Add(btnBorder);
                SettingsContent.Children.Add(btnBorder);
            }
        }
    }

    private void UpdateDependentRows(string changedKey) {
        if (!_dependentRows.TryGetValue(changedKey, out var rows)) return;
        foreach (var row in rows) {
            UpdateRowEnabled(row, changedKey);
        }
    }

    private void UpdateRowEnabled(Border row, string depKey) {
        if (_settings is null) return;
        var prop = typeof(AppSettings).GetProperty(depKey);
        var enabled = prop?.GetValue(_settings) is true;
        row.IsEnabled = enabled;
        row.Opacity = enabled ? 1.0 : 0.4;
    }

    private void WireSearch() {
        SearchBox.PropertyChanged += (_, e) => {
            if (e.Property == Controls.DMTextBox.TextProperty) {
                ApplyFilter();
            }
        };
    }

    private void ApplyFilter() {
        var search = SearchBox.Text?.Trim() ?? "";
        var showAllCategories = _selectedCategory == "All Settings";
        var isCommandsSelected = _selectedCategory == "Commands";

        // Hide/show the keyboard section and its header based on category selection.
        var showCommands = isCommandsSelected
            || (showAllCategories && string.IsNullOrEmpty(search));
        if (_commandsSection != null)
            _commandsSection.IsVisible = showCommands;
        if (_headersByCategory.TryGetValue("Commands", out var kbH))
            kbH.IsVisible = showCommands;
        if (showCommands && _commandsSection != null)
            Dispatcher.UIThread.Post(UpdateCommandsSectionHeight);

        // Hide/show standard settings rows.
        foreach (var cat in SettingsRegistry.Categories) {
            if (cat == "Commands") continue;

            var showCategory = (showAllCategories || _selectedCategory == cat) && !isCommandsSelected;
            var anyVisible = false;

            if (_rowsByCategory.TryGetValue(cat, out var rows)) {
                foreach (var row in rows) {
                    if (row.Tag is SettingDescriptor desc) {
                        var matchesSearch = string.IsNullOrEmpty(search)
                            || desc.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || (desc.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
                        var visible = showCategory && matchesSearch;
                        row.IsVisible = visible;
                        if (visible) anyVisible = true;
                    }
                }
            }

            if (_headersByCategory.TryGetValue(cat, out var header)) {
                header.IsVisible = showCategory && (anyVisible || string.IsNullOrEmpty(search));
            }
        }
    }

    private void UpdateCommandsSectionHeight() {
        if (_commandsSection == null || !_commandsSection.IsVisible) return;

        var viewportHeight = ContentScroll.Viewport.Height;
        if (viewportHeight <= 0) return;

        var headerHeight = _headersByCategory.TryGetValue("Commands", out var kbH)
            ? kbH.Bounds.Height + kbH.Margin.Top + kbH.Margin.Bottom : 0;
        var sectionMargin = _commandsSection.Margin.Top + _commandsSection.Margin.Bottom;
        var available = viewportHeight - headerHeight - sectionMargin
            - SettingsContent.Margin.Bottom;
        _commandsSection.SetAvailableHeight(available);
    }

    /// <summary>
    /// Shows the update button with the available version. Call when an
    /// update has been discovered (whether downloaded or not).
    /// </summary>
    public void SetUpdateAvailable(string version, bool downloaded) {
        if (_checkForUpdatesButton is null) return;
        _checkForUpdatesButton.IsVisible = true;
        _checkForUpdatesButton.IsEnabled = true;
        _checkForUpdatesButton.Content = downloaded
            ? $"Restart to apply {version}"
            : $"Update to {version}";
    }

    /// <summary>
    /// Shows a "Downloading…" state on the update button.
    /// </summary>
    public void SetUpdateDownloading() {
        if (_checkForUpdatesButton is null) return;
        _checkForUpdatesButton.IsEnabled = false;
        _checkForUpdatesButton.Content = "Downloading\u2026";
    }

    /// <summary>
    /// Clears transient UI state: search text, scroll position, and keyboard
    /// section state. Called when the settings tab is closed.
    /// </summary>
    public void ResetState() {
        SearchBox.Text = "";
        ContentScroll.Offset = default;
        _commandsSection?.ResetState();
    }

    /// <summary>
    /// Applies theme colors to the settings panel.
    /// </summary>
    public void ApplyTheme(EditorTheme theme) {
        Background = theme.MenuBackground;
        SearchBarBorder.BorderBrush = theme.TabActiveBackground;
        SearchBarBorder.Background = theme.TabActiveBackground;
        SidebarBorder.BorderBrush = theme.TabActiveBackground;
        SidebarBorder.Background = theme.TabActiveBackground;

        foreach (var header in _allHeaders) {
            header.Foreground = theme.EditorForeground;
        }

        // Update factory theme so value-change callbacks use the right colors.
        SettingRowFactory.CurrentTheme = theme;

        // Re-theme standard setting rows: descriptions, labels, modified indicators.
        foreach (var row in _allRows) {
            ThemeSettingRow(row, theme);
        }

        _commandsSection?.ApplyTheme(theme);
    }

    /// <summary>
    /// Walks a single setting row border and applies theme colors to
    /// description TextBlocks (tag "dim") and the modified-state accent.
    /// </summary>
    private void ThemeSettingRow(Border row, EditorTheme theme) {
        // Modified indicator: non-transparent border means "modified".
        if (row.BorderBrush is SolidColorBrush scb && scb.Color.A > 0) {
            row.BorderBrush = theme.SettingsAccent;
        }

        // Walk the visual tree for tagged TextBlocks.
        ThemeChildren(row, theme);
    }

    private void ThemeChildren(Control parent, EditorTheme theme) {
        IEnumerable<Control>? children = parent switch {
            Panel p => p.Children.OfType<Control>(),
            ContentControl cc when cc.Content is Control c => [c],
            Decorator d when d.Child is Control dc => [dc],
            _ => null,
        };

        if (children is null) return;

        foreach (var child in children) {
            if (child is TextBlock tb) {
                if (tb.Tag is "dim") {
                    tb.Foreground = theme.SettingsDimForeground;
                } else if (tb.Tag is not string) {
                    // Display name labels (no tag) get foreground.
                    tb.Foreground = theme.EditorForeground;
                }
            }
            if (child is TextBox txb && txb.Tag is "preview") {
                txb.Foreground = theme.EditorForeground;
                txb.CaretBrush = theme.EditorForeground;
                if (_settings != null)
                    txb.SelectionBrush = SettingRowFactory.GetSelectionBrush(_settings);
            }
            if (child is Border b && b.Tag is "previewBorder") {
                b.Background = theme.EditorBackground;
                b.BorderBrush = theme.SettingsInputBorder;
            }
            if (child is Controls.DMEditableCombo combo && _settings != null) {
                var displayFont = SettingRowFactory.GetDisplayFontFamily(_settings);
                var brush = SettingRowFactory.IsFontInstalled(displayFont)
                    ? theme.EditorForeground
                    : theme.SettingsErrorForeground;
                combo.Foreground = brush;
                if (combo.InnerTextBox != null) {
                    combo.InnerTextBox.Foreground = brush;
                }
            }
            ThemeChildren(child, theme);
        }
    }
}
