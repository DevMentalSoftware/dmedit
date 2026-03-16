using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

/// <summary>
/// The user's chosen resolution for a file conflict.
/// </summary>
public enum FileConflictChoice {
    /// <summary>Load the current disk version (discard edits).</summary>
    LoadDiskVersion,
    /// <summary>Locate the file via a file dialog (for missing files).</summary>
    LocateFile,
    /// <summary>Keep the editor's version, ignoring the disk change.</summary>
    KeepMyVersion,
    /// <summary>Close the tab (discard everything).</summary>
    Discard,
}

/// <summary>
/// Modal dialog shown when a tab's base file is missing or has been
/// modified externally. Used both during session restore and at runtime
/// when the file watcher detects changes.
/// </summary>
public class FileConflictDialog : Window {
    public FileConflictChoice Choice { get; private set; } = FileConflictChoice.Discard;

    public FileConflictDialog(Services.FileConflict conflict, EditorTheme? theme = null) {
        var isMissing = conflict.Kind == Services.FileConflictKind.Missing;

        Title = isMissing ? "File Not Found" : "File Changed";
        Width = 520;
        MinHeight = 180;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var heading = new TextBlock {
            Text = isMissing ? "File Not Found" : "File Changed Externally",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var message = new TextBlock {
            Text = isMissing
                ? $"The file no longer exists on disk:\n\n{conflict.FilePath}"
                : $"The file has been modified outside the editor:\n\n{conflict.FilePath}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        if (isMissing) {
            var locateBtn = new Button { Content = "Locate File\u2026" };
            locateBtn.Click += (_, _) => {
                Choice = FileConflictChoice.LocateFile;
                Close();
            };
            buttonPanel.Children.Add(locateBtn);
        } else {
            var loadDiskBtn = new Button { Content = "Load Disk Version" };
            loadDiskBtn.Click += (_, _) => {
                Choice = FileConflictChoice.LoadDiskVersion;
                Close();
            };
            buttonPanel.Children.Add(loadDiskBtn);

            var keepBtn = new Button { Content = "Keep My Version" };
            keepBtn.Click += (_, _) => {
                Choice = FileConflictChoice.KeepMyVersion;
                Close();
            };
            buttonPanel.Children.Add(keepBtn);
        }

        var discardBtn = new Button { Content = "Close Tab" };
        discardBtn.Click += (_, _) => {
            Choice = FileConflictChoice.Discard;
            Close();
        };
        buttonPanel.Children.Add(discardBtn);

        Content = new StackPanel {
            Margin = new Thickness(20),
            Children = { heading, message, buttonPanel },
        };

        if (theme is not null) {
            Background = theme.TabActiveBackground;
        }
    }
}
