using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

public enum ErrorDialogButton {
    OK,
    SaveAs,
    CloseTab,
}

/// <summary>
/// General-purpose error dialog shown when any operation fails. Replaces the
/// former SaveErrorDialog and SaveFailedDialog with a single, flexible dialog.
/// In dev mode an expandable panel shows the full stack trace.
/// </summary>
public class ErrorDialog : Window {
    public ErrorDialogButton Result { get; private set; } = ErrorDialogButton.OK;

    /// <param name="title">Window title.</param>
    /// <param name="heading">Bold heading line inside the dialog.</param>
    /// <param name="detail">Main message body (file path, error message, etc.).</param>
    /// <param name="buttons">Which buttons to show.</param>
    /// <param name="crashReportPath">Optional path to a crash report file.</param>
    /// <param name="stackTrace">Full exception string; shown in dev mode expander.</param>
    /// <param name="devMode">Whether to show the stack trace expander.</param>
    /// <param name="theme">Current editor theme for styling.</param>
    public ErrorDialog(
        string title,
        string heading,
        string detail,
        ErrorDialogButton[] buttons,
        string? crashReportPath = null,
        string? stackTrace = null,
        bool devMode = false,
        EditorTheme? theme = null) {

        Title = title;
        Width = 520;
        MinWidth = 360;
        MinHeight = 200;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock {
            Text = heading,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = Brushes.OrangeRed,
        });

        if (crashReportPath is not null) {
            panel.Children.Add(new TextBlock {
                Text = $"Crash report saved to:\n{crashReportPath}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 11,
                Opacity = 0.7,
            });
        }

        // Dev mode: expandable stack trace panel.
        if (devMode && stackTrace is not null) {
            var traceBox = new TextBox {
                Text = stackTrace,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 260,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            };

            var expander = new Expander {
                Header = "Stack Trace",
                Content = traceBox,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12),
            };
            panel.Children.Add(expander);
        }

        // Buttons.
        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        foreach (var btn in buttons) {
            var (label, result) = btn switch {
                ErrorDialogButton.SaveAs => ("Save As...", ErrorDialogButton.SaveAs),
                ErrorDialogButton.CloseTab => ("Close Tab", ErrorDialogButton.CloseTab),
                _ => ("OK", ErrorDialogButton.OK),
            };
            var button = new Button { Content = label, MinWidth = 90 };
            button.Click += (_, _) => {
                Result = result;
                Close();
            };
            buttonPanel.Children.Add(button);
        }

        panel.Children.Add(buttonPanel);
        Content = panel;

        if (theme is not null) {
            Background = theme.TabActiveBackground;
            Foreground = theme.TabForeground;
            RequestedThemeVariant = theme == EditorTheme.Dark
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }
    }
}
