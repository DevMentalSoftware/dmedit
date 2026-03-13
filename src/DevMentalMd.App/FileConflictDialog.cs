using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DevMentalMd.App;

/// <summary>
/// The user's chosen resolution for a file conflict during session restore.
/// </summary>
public enum FileConflictChoice {
    /// <summary>Load the current disk version (discard session edits).</summary>
    LoadDiskVersion,
    /// <summary>Locate the file via a file dialog (for missing files).</summary>
    LocateFile,
    /// <summary>Close the tab (discard everything).</summary>
    Discard,
}

/// <summary>
/// Modal dialog shown when a session-restored tab's base file is missing
/// or has been modified externally since the last session.
/// </summary>
public class FileConflictDialog : Window {
    public FileConflictChoice Choice { get; private set; } = FileConflictChoice.Discard;

    public FileConflictDialog(Services.SessionStore.FileConflict conflict) {
        Title = "Session Recovery";
        Width = 520;
        MinHeight = 180;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var isMissing = conflict.Kind == Services.SessionStore.FileConflictKind.Missing;

        var heading = new TextBlock {
            Text = isMissing ? "File Not Found" : "File Changed Externally",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var message = new TextBlock {
            Text = isMissing
                ? $"The file no longer exists on disk:\n\n{conflict.FilePath}\n\nThe session contains unsaved changes based on this file."
                : $"The file has been modified outside the editor since your last session:\n\n{conflict.FilePath}\n\nThe session contains unsaved changes based on the previous version.",
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
        }

        var discardBtn = new Button { Content = "Discard and Close Tab" };
        discardBtn.Click += (_, _) => {
            Choice = FileConflictChoice.Discard;
            Close();
        };
        buttonPanel.Children.Add(discardBtn);

        Content = new StackPanel {
            Margin = new Thickness(20),
            Children = { heading, message, buttonPanel },
        };
    }
}
