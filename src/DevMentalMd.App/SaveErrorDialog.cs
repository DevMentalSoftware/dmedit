using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

public enum SaveErrorChoice {
    SaveAs,
    OK,
}

/// <summary>
/// Dialog shown when a save operation fails due to an IO or permission error
/// (file locked, read-only, disk full, etc.). Offers [Save As...] to try a
/// different location, or [OK] to dismiss. The tab is kept open either way.
/// </summary>
public class SaveErrorDialog : Window {
    public SaveErrorChoice Result { get; private set; } = SaveErrorChoice.OK;

    public SaveErrorDialog(string? filePath, string errorMessage, EditorTheme? theme = null) {
        Title = "Could Not Save";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock {
            Text = "Could not save the file.",
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
            Text = errorMessage,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = Brushes.OrangeRed,
        });

        var saveAsBtn = new Button { Content = "Save As...", MinWidth = 90 };
        saveAsBtn.Click += (_, _) => {
            Result = SaveErrorChoice.SaveAs;
            Close();
        };

        var okBtn = new Button { Content = "OK", MinWidth = 90 };
        okBtn.Click += (_, _) => {
            Result = SaveErrorChoice.OK;
            Close();
        };

        panel.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { saveAsBtn, okBtn },
        });

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
