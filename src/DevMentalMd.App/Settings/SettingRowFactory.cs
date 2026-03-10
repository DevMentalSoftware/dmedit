using System;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Settings;

/// <summary>
/// Creates Avalonia controls for individual settings and wires two-way
/// binding to an <see cref="AppSettings"/> instance.
/// </summary>
public static class SettingRowFactory {
    /// <summary>
    /// Current theme used for row colors. Set by <see cref="SettingsControl"/>
    /// whenever the theme changes.
    /// </summary>
    internal static EditorTheme CurrentTheme { get; set; } = EditorTheme.Light;

    /// <summary>
    /// Builds a row control for the given descriptor. Returns a Border
    /// containing the appropriate input control(s).
    /// </summary>
    public static Border CreateRow(SettingDescriptor desc, AppSettings settings, Action<string> onChanged) {
        var prop = typeof(AppSettings).GetProperty(desc.Key)
                   ?? throw new InvalidOperationException($"No property '{desc.Key}' on AppSettings");

        var border = new Border {
            Padding = new Thickness(12, 6, 12, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            Tag = desc,
        };

        var stack = new StackPanel { Spacing = 2 };

        Control input = desc.Kind switch {
            SettingKind.Bool => CreateBoolRow(desc, prop, settings, onChanged, border),
            SettingKind.Int => CreateIntRow(desc, prop, settings, onChanged, border),
            SettingKind.Long => CreateLongRow(desc, prop, settings, onChanged, border),
            SettingKind.Double => CreateDoubleRow(desc, prop, settings, onChanged, border),
            SettingKind.Enum => CreateEnumRow(desc, prop, settings, onChanged, border),
            _ => new TextBlock { Text = $"(unsupported kind: {desc.Kind})" },
        };

        stack.Children.Add(input);

        if (desc.Description is not null && desc.Kind != SettingKind.Bool) {
            stack.Children.Add(new TextBlock {
                Text = desc.Description,
                FontSize = 11,
                Foreground = CurrentTheme.SettingsDimForeground,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Tag = "dim",
            });
        }

        border.Child = stack;
        UpdateModifiedIndicator(border, desc, prop, settings);
        return border;
    }

    private static Control CreateBoolRow(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var isChecked = (bool)prop.GetValue(settings)!;

        var glyph = new TextBlock {
            Text = IconGlyphs.CheckMark,
            FontFamily = IconGlyphs.Family,
            FontSize = 18,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = isChecked ? 1.0 : 0.0,
            Margin = new Thickness(0, 1, 0, 0),
        };

        var contentStack = new StackPanel { Spacing = 1 };
        contentStack.Children.Add(new TextBlock {
            Text = desc.DisplayName,
            FontSize = 13,
        });
        if (desc.Description is not null) {
            contentStack.Children.Add(new TextBlock {
                Text = desc.Description,
                FontSize = 11,
                Foreground = CurrentTheme.SettingsDimForeground,
                TextWrapping = TextWrapping.Wrap,
                Tag = "dim",
            });
        }

        var row = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        row.Children.Add(glyph);
        row.Children.Add(contentStack);

        var hitArea = new Border {
            Child = row,
            Background = Brushes.Transparent
        };

        hitArea.PointerPressed += (_, e) => {
            var newVal = !(bool)prop.GetValue(settings)!;
            glyph.Opacity = newVal ? 1.0 : 0.0;
            prop.SetValue(settings, newVal);
            settings.ScheduleSave();
            UpdateModifiedIndicator(border, desc, prop, settings);
            onChanged(desc.Key);
            e.Handled = true;
        };

        return hitArea;
    }

    private static Panel CreateIntRow(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock {
            Text = desc.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : decimal.MinValue,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : decimal.MaxValue,
            Increment = 1,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0",
        };

        nud.ValueChanged += (_, _) => {
            if (nud.Value.HasValue) {
                prop.SetValue(settings, (int)nud.Value.Value);
                settings.ScheduleSave();
                UpdateModifiedIndicator(border, desc, prop, settings);
                onChanged(desc.Key);
            }
        };

        panel.Children.Add(nud);
        return panel;
    }

    private static Panel CreateLongRow(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock {
            Text = desc.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : decimal.MinValue,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : decimal.MaxValue,
            Increment = 1048576, // 1 MB steps
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0",
        };

        nud.ValueChanged += (_, _) => {
            if (nud.Value.HasValue) {
                prop.SetValue(settings, (long)nud.Value.Value);
                settings.ScheduleSave();
                UpdateModifiedIndicator(border, desc, prop, settings);
                onChanged(desc.Key);
            }
        };

        panel.Children.Add(nud);
        return panel;
    }

    private static Panel CreateDoubleRow(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock {
            Text = desc.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : 0,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : 1000,
            Increment = 0.1m,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0.0",
        };

        nud.ValueChanged += (_, _) => {
            if (nud.Value.HasValue) {
                prop.SetValue(settings, (double)nud.Value.Value);
                settings.ScheduleSave();
                UpdateModifiedIndicator(border, desc, prop, settings);
                onChanged(desc.Key);
            }
        };

        panel.Children.Add(nud);
        return panel;
    }

    private static Panel CreateEnumRow(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var enumType = desc.EnumType
                       ?? throw new InvalidOperationException($"EnumType required for {desc.Key}");

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock {
            Text = desc.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var combo = new ComboBox {
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var names = Enum.GetNames(enumType);
        foreach (var name in names) {
            combo.Items.Add(name);
        }

        var current = prop.GetValue(settings)!.ToString()!;
        combo.SelectedItem = current;

        combo.SelectionChanged += (_, _) => {
            if (combo.SelectedItem is string selected) {
                var val = Enum.Parse(enumType, selected);
                prop.SetValue(settings, val);
                settings.ScheduleSave();
                UpdateModifiedIndicator(border, desc, prop, settings);
                onChanged(desc.Key);
            }
        };

        panel.Children.Add(combo);
        return panel;
    }

    private static void UpdateModifiedIndicator(
        Border border, SettingDescriptor desc, PropertyInfo prop, AppSettings settings) {
        var current = prop.GetValue(settings);
        var isDefault = Equals(current, desc.DefaultValue);
        border.BorderBrush = isDefault ? Brushes.Transparent : CurrentTheme.SettingsAccent;
    }
}
