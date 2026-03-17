using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

public enum SaveFailedChoice {
    SaveAs,
    CloseTab,
}

/// <summary>
/// Error dialog shown when a save operation fails with an unexpected exception.
/// Offers [Save As...] to try a different location, or [Close Tab] to discard.
/// </summary>
public class SaveFailedDialog : Window {
    public SaveFailedChoice Result { get; private set; } = SaveFailedChoice.CloseTab;

    public SaveFailedDialog(string? filePath, string errorMessage, string? crashReportPath, EditorTheme? theme = null) {
        Title = "Save Failed";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock {
            Text = "Save failed. The file may be corrupted.",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock {
            Text = $"File: {filePath ?? "(untitled)"}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock {
            Text = $"Error: {errorMessage}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
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

        var saveAsBtn = new Button { Content = "Save As...", MinWidth = 90 };
        saveAsBtn.Click += (_, _) => {
            Result = SaveFailedChoice.SaveAs;
            Close();
        };

        var closeTabBtn = new Button { Content = "Close Tab", MinWidth = 90 };
        closeTabBtn.Click += (_, _) => {
            Result = SaveFailedChoice.CloseTab;
            Close();
        };

        panel.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { saveAsBtn, closeTabBtn },
        });

        Content = panel;

        if (theme is not null) {
            Background = theme.TabActiveBackground;
        }
    }
}
