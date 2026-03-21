using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

/// <summary>
/// A lightweight popup for navigating to a specific line and optional column.
/// Accepts input in the form "line" or "line:col".
/// </summary>
public class GoToLineWindow : Window {
    private readonly TextBox _input;
    private readonly Border _rootBorder;

    /// <summary>1-based line number to go to, or <c>null</c> if cancelled.</summary>
    public long? TargetLine { get; private set; }

    /// <summary>1-based column number, or <c>null</c> if not specified.</summary>
    public long? TargetCol { get; private set; }

    public GoToLineWindow(EditorTheme theme, long currentLine = 1) {
        Title = "Go to Line";
        Width = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _input = new TextBox {
            Watermark = "Line[:Column]",
            FontSize = 14,
            Text = currentLine.ToString(),
        };
        _input.AddHandler(
            KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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

        var row = new Grid {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(6),
        };
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(closeBtn, 1);
        closeBtn.Margin = new Thickness(4, 0, 0, 0);
        row.Children.Add(_input);
        row.Children.Add(closeBtn);

        _rootBorder = new Border {
            Child = row,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        Content = _rootBorder;

        ApplyTheme(theme);
        Opened += (_, _) => {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.Enter) {
            e.Handled = true;
            if (TryParse(_input.Text)) {
                Close();
            }
        }
    }

    private bool TryParse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        text = text.Trim();

        // Accept "line" or "line:col"
        var colonIdx = text.IndexOf(':');
        if (colonIdx < 0) {
            if (long.TryParse(text, out var line) && line >= 1) {
                TargetLine = line;
                return true;
            }
            return false;
        }

        var linePart = text[..colonIdx];
        var colPart = text[(colonIdx + 1)..];
        if (long.TryParse(linePart, out var ln) && ln >= 1) {
            TargetLine = ln;
            if (long.TryParse(colPart, out var col) && col >= 1) {
                TargetCol = col;
            }
            return true;
        }
        return false;
    }

    private void ApplyTheme(EditorTheme theme) {
        _rootBorder.Background = theme.StatusBarBackground;
        _rootBorder.BorderBrush = theme.StatusBarBorder;
        _input.Foreground = theme.EditorForeground;
        _input.Background = theme.EditorBackground;
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }
}
