using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

public partial class SettingsControl : UserControl {
    private AppSettings? _settings;
    private KeyBindingService? _keyBindings;
    private KeyboardSettingsSection? _keyboardSection;
    private readonly Dictionary<string, List<Border>> _rowsByCategory = new();
    private readonly Dictionary<string, TextBlock> _headersByCategory = new();
    private readonly List<Border> _allRows = [];
    private readonly List<TextBlock> _allHeaders = [];
    private string _selectedCategory = "All Settings";

    /// <summary>
    /// Fired when any setting value changes. The string is the AppSettings property name.
    /// </summary>
    public event Action<string>? SettingChanged;

    /// <summary>
    /// Fired when any keyboard binding changes.
    /// </summary>
    public event Action? KeyBindingChanged;

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
                UpdateKeyboardSectionHeight();
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
            // "Keyboard" category gets a custom section instead of standard rows.
            if (cat == "Keyboard") {
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
                    _keyboardSection = new KeyboardSettingsSection(_keyBindings, _settings);
                    _keyboardSection.IsVisible = false;
                    _keyboardSection.BindingChanged += () => KeyBindingChanged?.Invoke();
                    SettingsContent.Children.Add(_keyboardSection);
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

            // Setting rows for this category
            var descriptors = SettingsRegistry.All.Where(d => d.Category == cat);
            foreach (var desc in descriptors) {
                var row = SettingRowFactory.CreateRow(desc, _settings, key => {
                    SettingChanged?.Invoke(key);
                });
                _rowsByCategory[cat].Add(row);
                _allRows.Add(row);
                SettingsContent.Children.Add(row);
            }
        }
    }

    private void WireSearch() {
        SearchBox.TextChanged += (_, _) => {
            SearchClearBtn.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            ApplyFilter();
        };
        SearchClearBtn.Click += (_, _) => {
            SearchBox.Text = "";
        };
    }

    private void ApplyFilter() {
        var search = SearchBox.Text?.Trim() ?? "";
        var showAllCategories = _selectedCategory == "All Settings";
        var isKeyboardSelected = _selectedCategory == "Keyboard";

        // Hide/show the keyboard section and its header based on category selection.
        var showKeyboard = isKeyboardSelected
            || (showAllCategories && string.IsNullOrEmpty(search));
        if (_keyboardSection != null)
            _keyboardSection.IsVisible = showKeyboard;
        if (_headersByCategory.TryGetValue("Keyboard", out var kbH))
            kbH.IsVisible = showKeyboard;
        if (showKeyboard && _keyboardSection != null)
            Dispatcher.UIThread.Post(UpdateKeyboardSectionHeight);

        // Hide/show standard settings rows.
        foreach (var cat in SettingsRegistry.Categories) {
            if (cat == "Keyboard") continue;

            var showCategory = (showAllCategories || _selectedCategory == cat) && !isKeyboardSelected;
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

    private void UpdateKeyboardSectionHeight() {
        if (_keyboardSection == null || !_keyboardSection.IsVisible) return;

        var viewportHeight = ContentScroll.Viewport.Height;
        if (viewportHeight <= 0) return;

        var headerHeight = _headersByCategory.TryGetValue("Keyboard", out var kbH)
            ? kbH.Bounds.Height + kbH.Margin.Top + kbH.Margin.Bottom : 0;
        var sectionMargin = _keyboardSection.Margin.Top + _keyboardSection.Margin.Bottom;
        var available = viewportHeight - headerHeight - sectionMargin
            - SettingsContent.Margin.Bottom;
        _keyboardSection.SetAvailableHeight(available);
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

        SearchClearBtn.Foreground = theme.EditorForeground;

        foreach (var header in _allHeaders) {
            header.Foreground = theme.EditorForeground;
        }

        // Update factory theme so value-change callbacks use the right colors.
        SettingRowFactory.CurrentTheme = theme;

        // Re-theme standard setting rows: descriptions, labels, modified indicators.
        foreach (var row in _allRows) {
            ThemeSettingRow(row, theme);
        }

        _keyboardSection?.ApplyTheme(theme);
    }

    /// <summary>
    /// Walks a single setting row border and applies theme colors to
    /// description TextBlocks (tag "dim") and the modified-state accent.
    /// </summary>
    private static void ThemeSettingRow(Border row, EditorTheme theme) {
        // Modified indicator: non-transparent border means "modified".
        if (row.BorderBrush is SolidColorBrush scb && scb.Color.A > 0) {
            row.BorderBrush = theme.SettingsAccent;
        }

        // Walk the visual tree for tagged TextBlocks.
        ThemeChildren(row, theme);
    }

    private static void ThemeChildren(Control parent, EditorTheme theme) {
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
            ThemeChildren(child, theme);
        }
    }
}
