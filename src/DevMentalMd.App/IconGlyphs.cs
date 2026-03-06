using Avalonia.Media;

namespace DevMentalMd.App;

/// <summary>
/// Centralizes icon-font glyph constants and the shared font family
/// so every usage site references one place. When we bundle a
/// cross-platform font (e.g. Fluent UI System Icons) the font
/// family string only needs to change here.
/// </summary>
static class IconGlyphs {
    // Font family — will be updated to an embedded avares:// resource
    // when we bundle a cross-platform icon font for Linux.
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public static readonly FontFamily Family = new(FontFamilyName);
    public static readonly Typeface Face = new(FontFamilyName);

    // Glyphs (Segoe Fluent Icons / MDL2 Assets codepoints)
    public const string CheckMark    = "\uE73E";  // ✓
    public const string ChevronRight = "\uE76C";  // >
    public const string ChevronDown  = "\uE70D";  // v
    public const string Settings     = "\uE713";  // ⚙
    public const string Add          = "\uE710";  // +
    public const string Close  = "\uE624";  // ×
    public const string Dirty     = "\uECCC";  // ●
}
