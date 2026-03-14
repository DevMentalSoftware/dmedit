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
        Height = 70;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;

        _input = new TextBox {
            Watermark = "Line[:Column]",
            FontSize = 14,
            Text = currentLine.ToString(),
            Margin = new Thickness(8),
        };
        _input.AddHandler(
            KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _rootBorder = new Border {
            Child = _input,
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
    }
}
