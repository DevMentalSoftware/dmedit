using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

public partial class SettingsControl : UserControl {
    private AppSettings? _settings;
    private readonly Dictionary<string, List<Border>> _rowsByCategory = new();
    private readonly Dictionary<string, TextBlock> _headersByCategory = new();
    private readonly List<Border> _allRows = [];
    private readonly List<TextBlock> _allHeaders = [];
    private string _selectedCategory = "All Settings";

    /// <summary>
    /// Fired when any setting value changes. The string is the AppSettings property name.
    /// </summary>
    public event Action<string>? SettingChanged;

    public SettingsControl() {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the control with the given settings. Call once after construction.
    /// </summary>
    public void Initialize(AppSettings settings) {
        _settings = settings;
        BuildCategoryList();
        BuildSettingRows();
        WireSearch();
    }

    private void BuildCategoryList() {
        CategoryList.Items.Add("All Settings");
        foreach (var cat in SettingsRegistry.Categories) {
            CategoryList.Items.Add(cat);
        }
        CategoryList.SelectedIndex = 0;

        CategoryList.SelectionChanged += (_, _) => {
            if (CategoryList.SelectedItem is string cat) {
                _selectedCategory = cat;
                ApplyFilter();
            }
        };
    }

    private void BuildSettingRows() {
        if (_settings is null) return;

        foreach (var cat in SettingsRegistry.Categories) {
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
        SearchBox.TextChanged += (_, _) => ApplyFilter();
    }

    private void ApplyFilter() {
        var search = SearchBox.Text?.Trim() ?? "";
        var showAllCategories = _selectedCategory == "All Settings";

        foreach (var cat in SettingsRegistry.Categories) {
            var showCategory = showAllCategories || _selectedCategory == cat;
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

    /// <summary>
    /// Applies theme colors to the settings panel.
    /// </summary>
    public void ApplyTheme(EditorTheme theme) {
        Background = theme.EditorBackground;
        SearchBarBorder.BorderBrush = theme.StatusBarBorder;
        SearchBarBorder.Background = theme.EditorBackground;
        ToolbarBorder.BorderBrush = theme.StatusBarBorder;
        ToolbarBorder.Background = theme.EditorBackground;
        SidebarBorder.BorderBrush = theme.StatusBarBorder;
        SidebarBorder.Background = theme.EditorBackground;

        foreach (var header in _allHeaders) {
            header.Foreground = theme.EditorForeground;
        }
    }
}
