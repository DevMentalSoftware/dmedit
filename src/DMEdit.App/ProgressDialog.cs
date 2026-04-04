using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DMEdit.App.Services;

namespace DMEdit.App;

/// <summary>
/// Modal dialog showing a progress bar with a cancel button.
/// The caller drives progress via <see cref="Update"/> and closes
/// the dialog when the operation finishes.
/// </summary>
public class ProgressDialog : Window {
    private readonly TextBlock _message;
    private readonly ProgressBar _progressBar;
    private readonly CancellationTokenSource _cts = new();
    private long _lastUpdateTick;

    /// <summary>Token that signals when the user clicks Cancel.</summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>True if the user cancelled the operation.</summary>
    public bool WasCancelled => _cts.IsCancellationRequested;

    public ProgressDialog(string title, string initialMessage, EditorTheme? theme = null,
        bool showCancelButton = true) {
        Title = title;
        Width = 400;
        MinWidth = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;

        var panel = new StackPanel { Margin = new Thickness(20) };

        _message = new TextBlock {
            Text = initialMessage,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(_message);

        _progressBar = new ProgressBar {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 16,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(_progressBar);

        if (showCancelButton) {
            var cancelBtn = new Button {
                Content = "Cancel",
                MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            cancelBtn.Click += (_, _) => {
                _cts.Cancel();
                _message.Text = "Cancelling\u2026";
                cancelBtn.IsEnabled = false;
            };
            panel.Children.Add(cancelBtn);
        }

        Content = panel;

        if (theme is not null) {
            Background = theme.TabActiveBackground;
            Foreground = theme.TabForeground;
            RequestedThemeVariant = theme == EditorTheme.Dark
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }
    }

    /// <summary>
    /// Cancel the operation whenever the window closes — whether via the
    /// Cancel button, the taskbar, or the caller calling <see cref="Window.Close"/>.
    /// </summary>
    protected override void OnClosed(EventArgs e) {
        _cts.Cancel();
        base.OnClosed(e);
    }

    /// <summary>Updates the progress bar and message from the UI thread.
    /// Throttled to at most one visual update every 200ms.</summary>
    public void Update(string message, double percent) {
        var now = Environment.TickCount64;
        if (now - _lastUpdateTick < 200 && percent < 100) return;
        _lastUpdateTick = now;
        _message.Text = message;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
    }
}
