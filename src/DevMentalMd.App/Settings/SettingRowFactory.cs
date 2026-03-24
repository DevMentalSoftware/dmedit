using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DevMentalMd.App.Controls;
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

    /// <summary>
    /// Creates a small icon button that restores a setting to its default
    /// value. Hidden when the setting already has its default.
    /// </summary>
    private static Button CreateResetButton(
        SettingDescriptor desc, PropertyInfo prop, AppSettings settings,
        Action<string> onChanged, Border border) {
        var btn = CreateResetIconButton();
        btn.IsVisible = !Equals(prop.GetValue(settings), desc.DefaultValue);

        btn.Click += (_, _) => {
            prop.SetValue(settings, desc.DefaultValue);
            settings.ScheduleSave();
            UpdateModifiedIndicator(border, desc, prop, settings);
            onChanged(desc.Key);
        };

        return btn;
    }

    /// <summary>
    /// Creates the shared icon-button visual for reset/undo. Callers wire
    /// their own Click handler and visibility logic.
    /// </summary>
    internal static Button CreateResetIconButton() {
        var btn = new Button {
            Width = 22, Height = 22,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = "reset",
            Content = new TextBlock {
                Text = IconGlyphs.ArrowUndo,
                FontFamily = IconGlyphs.Family,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        ToolTip.SetTip(btn, "Reset to default");
        return btn;
    }

    internal static Button CreateRemoveIconButton() {
        var btn = new Button {
            Width = 22, Height = 22,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = "remove",
            Content = new TextBlock {
                Text = IconGlyphs.Delete,
                FontFamily = IconGlyphs.Family,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        ToolTip.SetTip(btn, "Remove shortcut");
        return btn;
    }

    /// <summary>
    /// Creates a horizontal row with a setting label and a reset button.
    /// Used by non-bool row types.
    /// </summary>
    private static Panel CreateLabelRow(string displayName, Button resetBtn) {
        var row = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
        };
        row.Children.Add(new TextBlock {
            Text = displayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(resetBtn);
        return row;
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
            if (!e.GetCurrentPoint(hitArea).Properties.IsLeftButtonPressed) return;
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

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : decimal.MinValue,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : decimal.MaxValue,
            Increment = 1,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0",
        };

        var resetBtn = CreateResetButton(desc, prop, settings, onChanged, border);
        resetBtn.Click += (_, _) => nud.Value = Convert.ToDecimal(desc.DefaultValue);

        panel.Children.Add(CreateLabelRow(desc.DisplayName, resetBtn));

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

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : decimal.MinValue,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : decimal.MaxValue,
            Increment = 1048576, // 1 MB steps
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0",
        };

        var resetBtn = CreateResetButton(desc, prop, settings, onChanged, border);
        resetBtn.Click += (_, _) => nud.Value = Convert.ToDecimal(desc.DefaultValue);

        panel.Children.Add(CreateLabelRow(desc.DisplayName, resetBtn));

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

        var nud = new NumericUpDown {
            Value = Convert.ToDecimal(prop.GetValue(settings)),
            Minimum = desc.Min is not null ? Convert.ToDecimal(desc.Min) : 0,
            Maximum = desc.Max is not null ? Convert.ToDecimal(desc.Max) : 1000,
            Increment = 0.1m,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0.0",
        };

        var resetBtn = CreateResetButton(desc, prop, settings, onChanged, border);
        resetBtn.Click += (_, _) => nud.Value = Convert.ToDecimal(desc.DefaultValue);

        panel.Children.Add(CreateLabelRow(desc.DisplayName, resetBtn));

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

        var combo = new ComboBox {
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        combo.AddHandler(Control.PointerPressedEvent, (_, e) => {
            if (!e.GetCurrentPoint(combo).Properties.IsLeftButtonPressed)
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var names = Enum.GetNames(enumType);
        foreach (var name in names) {
            combo.Items.Add(name);
        }

        var current = prop.GetValue(settings)!.ToString()!;
        combo.SelectedItem = current;

        var resetBtn = CreateResetButton(desc, prop, settings, onChanged, border);
        resetBtn.Click += (_, _) => combo.SelectedItem = desc.DefaultValue.ToString();

        panel.Children.Add(CreateLabelRow(desc.DisplayName, resetBtn));

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
        // Toggle reset button visibility.
        if (FindByTag(border, "reset") is Control resetBtn) {
            resetBtn.IsVisible = !isDefault;
        }
    }

    /// <summary>
    /// Walks the visual tree under <paramref name="parent"/> looking for a
    /// control whose <see cref="Control.Tag"/> matches <paramref name="tag"/>.
    /// </summary>
    private static Control? FindByTag(Control parent, string tag) {
        if (parent is Panel panel) {
            foreach (var child in panel.Children) {
                if (child is Control c && Equals(c.Tag, tag)) return c;
                if (child is Control cp && FindByTag(cp, tag) is { } found) return found;
            }
        }
        if (parent is ContentControl { Content: Control content }) {
            if (Equals(content.Tag, tag)) return content;
            return FindByTag(content, tag);
        }
        if (parent is Decorator { Child: Control dec }) {
            if (Equals(dec.Tag, tag)) return dec;
            return FindByTag(dec, tag);
        }
        return null;
    }

    // =================================================================
    // Font setting (composite: family picker + toggle + size NUD + preview)
    // =================================================================

    private static List<string>? _allFontNames;
    private static List<string>? _monoFontNames;
    private static HashSet<string>? _installedFontSet;

    private const string SampleText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs. " +
        "0123456789 !@#$%^&*() {}[]|\\/<>";

    /// <summary>
    /// Preferred monospace fonts in priority order. The first one found on
    /// the system is used as the default when no setting is saved.
    /// </summary>
    private static readonly string[] PreferredFonts = [
        "Cascadia Code", "Consolas", "DejaVu Sans Mono",
        "Liberation Mono", "Courier New",
    ];

    /// <summary>
    /// Returns the effective font family name, resolving null (no setting)
    /// to the first available font from <see cref="PreferredFonts"/>.
    /// When the saved name is not installed, falls back to the default.
    /// </summary>
    internal static string GetEffectiveFontFamily(AppSettings settings) {
        if (settings.EditorFontFamily is { } saved && IsFontInstalled(saved)) {
            return saved;
        }
        return GetDefaultFontFamily();
    }

    /// <summary>
    /// Returns the raw saved font name (even if not installed).
    /// Used by the font picker to display what the user typed.
    /// </summary>
    internal static string GetDisplayFontFamily(AppSettings settings) =>
        settings.EditorFontFamily ?? GetDefaultFontFamily();

    /// <summary>
    /// Returns the auto-detected default font (first match from
    /// <see cref="PreferredFonts"/> that is installed).
    /// </summary>
    private static string GetDefaultFontFamily() {
        var installed = GetInstalledFontSet();
        return PreferredFonts.FirstOrDefault(p => installed.Contains(p))
            ?? "Courier New";
    }

    private static HashSet<string> GetInstalledFontSet() {
        _installedFontSet ??= new HashSet<string>(
            FontManager.Current.SystemFonts.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        return _installedFontSet;
    }

    internal static bool IsFontInstalled(string name) =>
        GetInstalledFontSet().Contains(name);

    /// <summary>
    /// Returns the canonical (properly cased) font name if a
    /// case-insensitive match exists, otherwise returns the input as-is.
    /// </summary>
    private static string NormalizeFontName(string name) {
        var match = GetAllFontNames().FirstOrDefault(
            f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match ?? name;
    }

    private static List<string> GetAllFontNames() {
        _allFontNames ??= FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _allFontNames;
    }

    private static List<string> GetMonoFontNames() {
        _monoFontNames ??= GetAllFontNames().Where(IsMonospace).ToList();
        return _monoFontNames;
    }

    private static bool IsMonospace(string fontName) {
        try {
            var typeface = new Typeface(fontName);
            using var narrow = new TextLayout("i", typeface, 20, null);
            using var wide = new TextLayout("W", typeface, 20, null);
            return Math.Abs(narrow.WidthIncludingTrailingWhitespace
                - wide.WidthIncludingTrailingWhitespace) < 0.5;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Builds a composite font row: AutoCompleteBox (font name) with
    /// dropdown button, ToggleButton (monospace filter), NumericUpDown
    /// (size), and a sample-text preview paragraph below.
    /// </summary>
    public static Border CreateFontRow(AppSettings settings, Action<string> onChanged) {
        // Use a SettingDescriptor tag so ApplyFilter search works.
        var desc = new SettingDescriptor(
            "EditorFontFamily", "Editor Font",
            "Font family and size used for the editor.",
            "Display", SettingKind.Int, 0);

        var border = new Border {
            Padding = new Thickness(12, 6, 12, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            Tag = desc,
        };

        var stack = new StackPanel { Spacing = 4 };

        // Font lists (cached).
        var allFonts = GetAllFontNames();
        var monoFonts = GetMonoFontNames();
        var showMono = true;
        var defaultFont = GetDefaultFontFamily();

        // ── Editable font picker (DMEditableCombo: TextBox + dropdown) ──
        var fontBox = new DMEditableCombo {
            MinWidth = 220,
            ItemsSource = monoFonts,
            HighlightItem = defaultFont,
            ShowClearButton = false,
            Text = GetDisplayFontFamily(settings),
        };

        // ── Monospace filter toggle (checked by default) ─────────────
        var monoToggle = new ToggleButton {
            Content = "F",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(7, 3),
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(monoToggle, "Show fixed-width fonts only");

        monoToggle.IsCheckedChanged += (_, _) => {
            showMono = monoToggle.IsChecked == true;
            fontBox.ItemsSource = showMono ? monoFonts : allFonts;
        };

        // ── Size picker (points) ─────────────────────────────────────
        var sizeNud = new NumericUpDown {
            Value = settings.EditorFontSize,
            Minimum = 6,
            Maximum = 72,
            Increment = 1,
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Left,
            FormatString = "0",
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── Editable preview paragraph ────────────────────────────────
        var previewBox = new TextBox {
            Text = settings.FontPreviewText ?? SampleText,
            VerticalAlignment = VerticalAlignment.Stretch,
            FontSize = settings.EditorFontSize.ToPixels(),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Tag = "preview",
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
        };
        // Kill the hover/focus background on the inner border element
        // so the preview blends with its parent border at all times.
        previewBox.TemplateApplied += (_, e) => {
            var borderEl = e.NameScope.Find<Border>("PART_BorderElement");
            if (borderEl != null) {
                borderEl.Background = Brushes.Transparent;
                borderEl.BorderBrush = Brushes.Transparent;
                // Suppress themed state changes on the border.
                borderEl.PropertyChanged += (_, pe) => {
                    if (pe.Property == Border.BackgroundProperty
                        && !Equals(pe.NewValue, Brushes.Transparent)) {
                        borderEl.Background = Brushes.Transparent;
                    }
                    if (pe.Property == Border.BorderBrushProperty
                        && !Equals(pe.NewValue, Brushes.Transparent)) {
                        borderEl.BorderBrush = Brushes.Transparent;
                    }
                };
            }
        };
        var displayFont = GetDisplayFontFamily(settings);
        ApplyFontToPreview(previewBox, displayFont);
        UpdateFontBoxForeground(fontBox, displayFont);

        // Save custom preview text (debounced via ScheduleSave).
        // When cleared, revert to the default sample text.
        previewBox.LostFocus += (_, _) => {
            if (string.IsNullOrWhiteSpace(previewBox.Text)) {
                previewBox.Text = SampleText;
            }
        };
        previewBox.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty) {
                var text = previewBox.Text;
                settings.FontPreviewText = string.IsNullOrEmpty(text) || text == SampleText
                    ? null : text;
                settings.ScheduleSave();
            }
        };

        var previewBorder = new Border {
            Child = previewBox,
            Background = CurrentTheme.EditorBackground,
            BorderThickness = new Thickness(1),
            BorderBrush = CurrentTheme.SettingsInputBorder,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Width = 600,
            Height = 80,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = "previewBorder",
        };

        // ── Callbacks ────────────────────────────────────────────────

        void ApplyFontChange(string fontName) {
            settings.EditorFontFamily = fontName;
            settings.ScheduleSave();
            UpdateFontModified(border, settings);
            ApplyFontToPreview(previewBox, fontName);
            UpdateFontBoxForeground(fontBox, fontName);
            onChanged("EditorFontFamily");
        }

        sizeNud.ValueChanged += (_, _) => {
            if (sizeNud.Value.HasValue) {
                settings.EditorFontSize = (int)sizeNud.Value.Value;
                settings.ScheduleSave();
                UpdateFontModified(border, settings);
                previewBox.FontSize = settings.EditorFontSize.ToPixels();
                onChanged("EditorFontSize");
            }
        };

        // Apply font change when the user picks from the dropdown or
        // finishes editing (lost focus). DMEditableCombo strips the star
        // internally so Text is always a clean font name.
        fontBox.LostFocus += (_, _) => {
            var text = fontBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            // Normalize case to match the installed font name.
            var normalized = NormalizeFontName(text);
            if (normalized != text) {
                fontBox.Text = normalized;
            }
            if (normalized != GetDisplayFontFamily(settings)) {
                ApplyFontChange(normalized);
            }
        };
        fontBox.PropertyChanged += (_, e) => {
            if (e.Property != DMEditableCombo.TextProperty) return;
            if (fontBox.IsPopupOpen) return;
            var text = fontBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text) && text != GetDisplayFontFamily(settings)) {
                ApplyFontChange(text);
            }
        };

        // ── Reset button ──────────────────────────────────────────────

        var fontResetBtn = CreateResetIconButton();
        fontResetBtn.IsVisible = settings.EditorFontFamily != null || settings.EditorFontSize != 11;

        fontResetBtn.Click += (_, _) => {
            fontBox.Text = defaultFont;
            sizeNud.Value = 11;
            ApplyFontChange(defaultFont);
            settings.EditorFontFamily = null;
            settings.EditorFontSize = 11;
            settings.ScheduleSave();
            previewBox.FontSize = 11.ToPixels();
            UpdateFontModified(border, settings);
            onChanged("EditorFontFamily");
            onChanged("EditorFontSize");
        };

        // ── Assemble ─────────────────────────────────────────────────

        stack.Children.Add(CreateLabelRow("Editor Font", fontResetBtn));

        var controlRow = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        controlRow.Children.Add(fontBox);
        controlRow.Children.Add(monoToggle);
        controlRow.Children.Add(sizeNud);

        stack.Children.Add(controlRow);
        stack.Children.Add(previewBorder);

        stack.Children.Add(new TextBlock {
            Text = "Font family and size used for the editor.",
            FontSize = 11,
            Foreground = CurrentTheme.SettingsDimForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Tag = "dim",
        });

        border.Child = stack;
        UpdateFontModified(border, settings);
        return border;
    }

    /// <summary>
    /// Sets the preview font. Always uses editor foreground.
    /// When the font is not installed, falls back to the default font.
    /// </summary>
    private static void ApplyFontToPreview(Control preview, string fontName) {
        preview.SetValue(TextBlock.FontFamilyProperty, IsFontInstalled(fontName)
            ? new FontFamily(fontName)
            : new FontFamily(GetDefaultFontFamily()));
        preview.SetValue(TextBlock.ForegroundProperty, CurrentTheme.EditorForeground);
    }

    /// <summary>
    /// Turns the font picker text red when the font name is not installed,
    /// or restores the default foreground when it is.
    /// </summary>
    private static void UpdateFontBoxForeground(DMEditableCombo fontBox, string fontName) {
        var brush = IsFontInstalled(fontName)
            ? CurrentTheme.EditorForeground
            : CurrentTheme.SettingsErrorForeground;
        fontBox.Foreground = brush;
        if (fontBox.InnerTextBox != null) {
            fontBox.InnerTextBox.Foreground = brush;
        }
    }

    private static void UpdateFontModified(Border border, AppSettings settings) {
        var isModified = settings.EditorFontFamily != null || settings.EditorFontSize != 11;
        border.BorderBrush = isModified ? CurrentTheme.SettingsAccent : Brushes.Transparent;
        if (FindByTag(border, "reset") is Control resetBtn) {
            resetBtn.IsVisible = isModified;
        }
    }
}
