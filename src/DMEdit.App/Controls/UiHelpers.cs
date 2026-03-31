using System.Text;
using Avalonia.Controls;
using Avalonia.Media;

namespace DMEdit.App.Controls;

/// <summary>
/// Shared UI helpers for tooltips, unit conversion, and other small
/// presentation concerns.
/// </summary>
static class UiHelpers {
    /// <summary>Converts typographic points to device-independent pixels (96 DPI).</summary>
    public static double ToPixels(this int pts) => pts * 96.0 / 72.0;

    /// <summary>
    /// Sets a file-path tooltip on <paramref name="target"/>.  Long paths are
    /// wrapped at directory separators with continuation lines indented.
    /// </summary>
    public static void SetPathToolTip(Control target, string? path) {
        if (string.IsNullOrEmpty(path)) {
            ToolTip.SetTip(target, null);
            return;
        }

        var sb = new StringBuilder();

        for (int idx = 0; idx < path.Length; ++idx) {
            char ch = path[idx];
            sb.Append(ch);
            if (ch == '\\' || ch == '/') {
                sb.Append('\u200b');
            }
        }

        var tb = new TextBlock {
            FontSize = UiHelpers.ToPixels(9),
            Text = sb.ToString(),
            TextWrapping = TextWrapping.Wrap
        };

        ToolTip.SetTip(target, tb);
    }
}
