using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using DMEdit.App.Services;

namespace DMEdit.App;

public enum ErrorDialogButton {
    OK,
    SaveAs,
    CloseTab,
    Exit,
    Continue,
}

/// <summary>
/// General-purpose error dialog shown when any operation fails. Replaces the
/// former SaveErrorDialog and SaveFailedDialog with a single, flexible dialog.
/// In dev mode an expandable panel shows the full stack trace.
/// </summary>
public class ErrorDialog : Window {
    public ErrorDialogButton Result { get; private set; } = ErrorDialogButton.OK;

    /// <param name="title">Window title.</param>
    /// <param name="detail">Main message body (file path, error message, etc.).</param>
    /// <param name="buttons">Which buttons to show.</param>
    /// <param name="crashReportPath">Optional path to a crash report file.</param>
    /// <param name="stackTrace">Full exception string; shown in dev mode expander.</param>
    /// <param name="devMode">Whether to show the stack trace expander.</param>
    /// <param name="theme">Current editor theme for styling.</param>
    public ErrorDialog(
        string title,
        string detail,
        ErrorDialogButton[] buttons,
        string? crashReportPath = null,
        string? stackTrace = null,
        bool devMode = false,
        EditorTheme? theme = null) {

        Title = title;
        Width = 720;
        MinWidth = 360;
        MinHeight = 200;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.Full;

        // Use DockPanel so the button row anchors to the bottom even when
        // the window is resized taller, and the stack trace fills remaining space.
        var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };

        // --- Buttons: dock to bottom-right ---
        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        foreach (var btn in buttons) {
            var (label, result) = btn switch {
                ErrorDialogButton.SaveAs => ("Save As...", ErrorDialogButton.SaveAs),
                ErrorDialogButton.CloseTab => ("Close Tab", ErrorDialogButton.CloseTab),
                ErrorDialogButton.Exit => ("Exit", ErrorDialogButton.Exit),
                ErrorDialogButton.Continue => ("Continue", ErrorDialogButton.Continue),
                _ => ("OK", ErrorDialogButton.OK),
            };
            var button = new Button { Content = label, MinWidth = 90 };
            button.Click += (_, _) => {
                Result = result;
                Close();
            };
            buttonPanel.Children.Add(button);
        }
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        root.Children.Add(buttonPanel);

        // --- Header content: dock to top ---
        var header = new StackPanel();

        header.Children.Add(new TextBlock {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.OrangeRed,
        });

        if (crashReportPath is not null) {
            header.Children.Add(new TextBlock {
                Text = $"Crash report saved to:\n{crashReportPath}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 11,
                Opacity = 0.7,
            });
        }

        // Dev mode: expandable stack trace panel.
        if (devMode && stackTrace is not null) {
            var traceBox = new TextBox {
                Text = stackTrace,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                MaxHeight = 300,
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            };

            var darkRed = new SolidColorBrush(Color.FromRgb(0x33, 0x23, 0x23));

            var expander = new Expander {
                Header = "Stack Trace",
                Content = traceBox,
                IsExpanded = devMode,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
            };

            var darkRedHover = new SolidColorBrush(Color.FromRgb(0x22, 0x00, 0x00));

            // Style the toggle button (header area) inside the Expander.
            // Target both the ToggleButton and its inner Border for all
            // visual states (normal, hover, pressed).
            var tbSelector = default(Func<Selector?, Selector>);
            tbSelector = x => x.OfType<Expander>().Template().OfType<ToggleButton>();
            var borderSelector = default(Func<Selector?, Selector>);
            borderSelector = x => x.OfType<Expander>().Template().OfType<ToggleButton>().Template().OfType<Border>();

            foreach (var sel in new[] { tbSelector, borderSelector }) {
                expander.Styles.Add(new Style(sel) {
                    Setters = { new Setter(ToggleButton.BackgroundProperty, darkRed) },
                });
                expander.Styles.Add(new Style(x => sel(x).Class(":pointerover")) {
                    Setters = { new Setter(ToggleButton.BackgroundProperty, darkRedHover) },
                });
                expander.Styles.Add(new Style(x => sel(x).Class(":pressed")) {
                    Setters = { new Setter(ToggleButton.BackgroundProperty, darkRedHover) },
                });
            }
            header.Children.Add(expander);
        }

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        Content = root;

        if (theme is not null) {
            Background = theme.TabActiveBackground;
            Foreground = theme.TabForeground;
            RequestedThemeVariant = theme == EditorTheme.Dark
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }
    }
}
