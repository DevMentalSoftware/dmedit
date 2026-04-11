using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Shared factory for creating an Avalonia <see cref="TextLayout"/> with
/// consistent defaults (left-aligned, infinite height, auto-wrap when a
/// finite <paramref name="maxWidth"/> is supplied).
/// </summary>
internal static class TextLayoutHelper {
    internal static TextLayout Create(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth) {

        var wrapping = double.IsFinite(maxWidth) && maxWidth > 0
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;
        var effectiveMaxWidth = double.IsFinite(maxWidth) && maxWidth > 0
            ? maxWidth
            : double.PositiveInfinity;

        return new TextLayout(
            text,
            typeface,
            fontSize,
            foreground,
            textAlignment: TextAlignment.Left,
            textWrapping: wrapping,
            maxWidth: effectiveMaxWidth,
            maxHeight: double.PositiveInfinity);
    }
}
