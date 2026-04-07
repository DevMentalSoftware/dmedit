using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using DMEdit.App.Services;

namespace DMEdit.App;

/// <summary>
/// Shows application name, version, copyright, and a link to the GitHub repo.
/// </summary>
public class AboutDialog : Window {
    private const string GitHubUrl = "https://github.com/DevMentalSoftware/dmedit";

    public AboutDialog(EditorTheme theme) {
        Title = "About DMEdit";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel {
            Margin = new Thickness(24),
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Logo
        var logo = LoadLogo();
        if (logo is not null) {
            root.Children.Add(new Image {
                Source = logo,
                Width = 96,
                Height = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        // App name
        root.Children.Add(new TextBlock {
            Text = "DMEdit",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Version
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        root.Children.Add(new TextBlock {
            Text = $"Version {version}",
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Description
        root.Children.Add(new TextBlock {
            Text = "A lightweight text editor built with Avalonia.",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        // Copyright
        root.Children.Add(new TextBlock {
            Text = $"Copyright \u00a9 {DateTime.Now.Year} DevMental Software LLC.\nAll rights reserved.",
            FontSize = 11,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        // GitHub link
        var link = new Button {
            Content = "github.com/DevMentalSoftware/dmedit",
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x90, 0xD0)),
            FontSize = 12,
        };
        link.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
        root.Children.Add(link);

        // OK button
        var ok = new Button {
            Content = "OK",
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);

        Content = root;
        ApplyTheme(theme);
    }

    private static Bitmap? LoadLogo() {
        try {
            var uri = new Uri("avares://dmedit/Resources/dev_mental_head.png");
            var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        } catch {
            return null;
        }
    }

    private void ApplyTheme(EditorTheme theme) {
        Background = theme.TabActiveBackground;
        Foreground = theme.TabForeground;
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
    }
}
