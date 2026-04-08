using System.Text;
using Avalonia.Controls;
using Avalonia.Media;

namespace DMEdit.App.Controls;

/// <summary>
/// Shared UI helpers for tooltips, unit conversion, and other small
/// presentation concerns.
/// </summary>
public static class UiHelpers {
    /// <summary>Converts typographic points to device-independent pixels (96 DPI).</summary>
    public static double ToPixels(this int pts) => pts * 96.0 / 72.0;

    /// <summary>
    /// Builds the wrapped tooltip text for a file path, optionally annotated
    /// with the name of an entry inside the file (e.g. a zip archive entry).
    /// Pure: no Avalonia dependency, so it's directly unit-testable.
    /// </summary>
    /// <param name="path">The file path to display.  Returns <c>null</c> when null/empty.</param>
    /// <param name="innerEntryName">Optional inner-entry name; rendered on a
    /// second line as "→ name" so the user can see what's inside an archive
    /// without widening the tab.</param>
    public static string? FormatPathTooltipText(string? path, string? innerEntryName) {
        if (string.IsNullOrEmpty(path)) {
            return null;
        }

        var sb = new StringBuilder();

        for (int idx = 0; idx < path.Length; ++idx) {
            char ch = path[idx];
            sb.Append(ch);
            if (ch == '\\' || ch == '/') {
                sb.Append('\u200b');
            }
        }

        if (!string.IsNullOrEmpty(innerEntryName)) {
            sb.Append('\n').Append('\u2192').Append(' ').Append(innerEntryName);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sets a file-path tooltip on <paramref name="target"/>.  Long paths are
    /// wrapped at directory separators with continuation lines indented.
    /// When <paramref name="innerEntryName"/> is non-null (zip-loaded files),
    /// it's appended on a second line as "→ entry-name" so the user can see
    /// what's inside the archive without widening the tab.
    /// </summary>
    public static void SetPathToolTip(Control target, string? path, string? innerEntryName = null) {
        var text = FormatPathTooltipText(path, innerEntryName);
        if (text == null) {
            ToolTip.SetTip(target, null);
            return;
        }

        var tb = new TextBlock {
            FontSize = UiHelpers.ToPixels(9),
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };

        ToolTip.SetTip(target, tb);
    }
}
