using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DMEdit.App.Services;
using DMEdit.Core.Documents;

namespace DMEdit.App;

/// <summary>
/// Dialog for composing and submitting feedback to GitHub. Supports loading
/// the current document's text, attaching crash reports, and auto-appending
/// system info. Currently submits via a pre-filled GitHub issue URL; a future
/// Azure Function backend can replace the submission mechanism.
/// </summary>
public class SubmitFeedbackDialog : Window {
    private const int MaxBodyChars = 32_768;

    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DMEdit", "session");

    private readonly TextBox _titleBox;
    private readonly TextBox _bodyBox;
    private readonly StackPanel _crashPanel;
    private readonly TextBlock _statusText;
    private readonly List<(CheckBox Check, string Path)> _crashChecks = [];

    /// <summary>
    /// Creates the feedback dialog.
    /// </summary>
    /// <param name="theme">Current editor theme.</param>
    /// <param name="activeDocument">The active tab's PieceTable, or null.</param>
    /// <param name="activeDocName">Display name of the active document.</param>
    public SubmitFeedbackDialog(
        EditorTheme theme, PieceTable? activeDocument = null, string? activeDocName = null) {
        Title = "Submit Feedback";
        Width = 600;
        Height = 520;
        MinWidth = 400;
        MinHeight = 360;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };

        // --- Bottom: buttons ---
        var buttonRow = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _statusText = new TextBlock {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 12, 0),
        };
        buttonRow.Children.Add(_statusText);

        var submitBtn = new Button { Content = "Submit", MinWidth = 100 };
        submitBtn.Click += OnSubmit;
        buttonRow.Children.Add(submitBtn);

        var browserBtn = new Button { Content = "Open in GitHub...", MinWidth = 130 };
        browserBtn.Click += OnSubmitViaBrowser;
        buttonRow.Children.Add(browserBtn);

        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        cancelBtn.Click += (_, _) => Close();
        buttonRow.Children.Add(cancelBtn);

        DockPanel.SetDock(buttonRow, Dock.Bottom);
        root.Children.Add(buttonRow);

        // --- Top: form fields (DockPanel so the body TextBox fills remaining space) ---
        var form = new DockPanel { LastChildFill = true };

        // Title — dock top
        var titleSection = new StackPanel { Spacing = 4 };
        titleSection.Children.Add(new TextBlock { Text = "Title", FontWeight = FontWeight.SemiBold });
        _titleBox = new TextBox { Watermark = "Brief summary of your feedback" };
        titleSection.Children.Add(_titleBox);
        DockPanel.SetDock(titleSection, Dock.Top);
        form.Children.Add(titleSection);

        // Body label + load button — dock top
        var bodyHeader = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        bodyHeader.Children.Add(new TextBlock {
            Text = "Details",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (activeDocument is not null) {
            var loadBtn = new Button {
                Content = $"Load from \"{Truncate(activeDocName ?? "document", 30)}\"",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(6, 2),
            };
            DockPanel.SetDock(loadBtn, Dock.Right);
            loadBtn.Click += (_, _) => LoadFromDocument(activeDocument);
            bodyHeader.Children.Add(loadBtn);
        }
        DockPanel.SetDock(bodyHeader, Dock.Top);
        form.Children.Add(bodyHeader);

        // Crash reports + note — dock bottom so body gets remaining space
        var bottomSection = new StackPanel { Spacing = 4 };
        _crashPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        PopulateCrashReports();
        if (_crashPanel.Children.Count > 0) {
            bottomSection.Children.Add(new TextBlock {
                Text = "Attach crash reports",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            bottomSection.Children.Add(_crashPanel);
        }
        bottomSection.Children.Add(new TextBlock {
            Text = "Submit sends directly. Open in GitHub lets you review/edit first (requires GitHub account).",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        DockPanel.SetDock(bottomSection, Dock.Bottom);
        form.Children.Add(bottomSection);

        // Body text area — fills remaining space
        _bodyBox = new TextBox {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            Watermark = "Describe the issue or suggestion...",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
        };
        form.Children.Add(_bodyBox);

        root.Children.Add(form);

        Content = root;
        ApplyTheme(theme);

        Opened += (_, _) => _titleBox.Focus();
    }

    private void LoadFromDocument(PieceTable table) {
        var len = (int)Math.Min(table.Length, MaxBodyChars);
        var text = table.GetText(0, len);
        _bodyBox.Text = text;
        if (table.Length > MaxBodyChars) {
            _statusText.Text = $"Truncated to {MaxBodyChars / 1024}KB (document is {table.Length / 1024}KB).";
        }
    }

    private void PopulateCrashReports() {
        if (!Directory.Exists(SessionDir)) {
            return;
        }
        var files = Directory.GetFiles(SessionDir, "crash-*.txt")
            .OrderByDescending(f => f)
            .Take(5)
            .ToArray();

        foreach (var file in files) {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);
            var sizeKb = info.Length / 1024.0;
            var check = new CheckBox {
                Content = $"{name} ({sizeKb:F0}KB)",
                FontSize = 11,
                IsChecked = false,
            };
            _crashChecks.Add((check, file));
            _crashPanel.Children.Add(check);
        }
    }

    private (string Title, string Body, string? CrashContent) CollectFields() {
        var body = _bodyBox.Text ?? "";

        string? crashContent = null;
        var checkedFiles = _crashChecks
            .Where(c => c.Check.IsChecked == true)
            .Select(c => c.Path)
            .ToList();

        if (checkedFiles.Count > 0) {
            var sb = new System.Text.StringBuilder();
            foreach (var path in checkedFiles) {
                try {
                    var text = File.ReadAllText(path);
                    if (sb.Length > 0) {
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                    sb.AppendLine(text);
                } catch {
                    // Skip unreadable files.
                }
            }
            crashContent = sb.ToString();
        }

        return (_titleBox.Text?.Trim() ?? "", body, crashContent);
    }

    private bool ValidateTitle() {
        var title = _titleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title)) {
            _statusText.Text = "Please enter a title.";
            _titleBox.Focus();
            return false;
        }
        return true;
    }

    /// <summary>Submit directly via the Azure Function endpoint.</summary>
    private async void OnSubmit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (!ValidateTitle()) {
            return;
        }
        var (title, body, crashContent) = CollectFields();

        // Combine title + body into message for the API payload.
        var message = string.IsNullOrEmpty(body) ? title : $"{title}\n\n{body}";
        var payload = GitHubIssueHelper.BuildPayload(
            "feedback", message, crashContent, Owner as Window);

        _statusText.Text = "Submitting...";
        _statusText.Opacity = 1.0;
        var error = await FeedbackClient.SubmitAsync(payload);

        if (error is null) {
            _statusText.Text = "Submitted successfully.";
            await Task.Delay(800);
            Close();
        } else {
            _statusText.Text = error;
        }
    }

    /// <summary>Open a pre-filled GitHub issue in the browser.</summary>
    private void OnSubmitViaBrowser(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (!ValidateTitle()) {
            return;
        }
        var (title, body, crashContent) = CollectFields();

        var fit = GitHubIssueHelper.OpenFeedbackIssue(title, body, crashContent, Owner as Window);
        if (!fit) {
            _statusText.Text = "Content was truncated to fit URL limits.";
        }
        Close();
    }

    private void ApplyTheme(EditorTheme theme) {
        Background = theme.TabActiveBackground;
        Foreground = theme.TabForeground;
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
