using Avalonia.Media;

namespace DevMentalMd.App;

/// <summary>
/// Centralizes icon-font glyph constants and the shared font family
/// so every usage site references one place. Uses the MIT-licensed
/// Fluent UI System Icons font bundled as an Avalonia resource for
/// cross-platform support (Windows + Linux).
/// https://github.com/microsoft/fluentui-system-icons
/// </summary>
static class IconGlyphs {
    // Embedded cross-platform font (MIT licensed).
    public const string FontFamilyName =
        "avares://dmedit/Resources/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";
    public static readonly FontFamily Family = new(FontFamilyName);
    public static readonly Typeface Face = new(FontFamilyName);

    // Glyphs (Fluent UI System Icons — Regular, 20px variants)
    public const string CheckMark    = "\uF293";  // ✓  ic_fluent_checkmark_12_regular
    public const string ChevronRight = "\uF2B0";  // >  ic_fluent_chevron_right_20_regular
    public const string ChevronDown  = "\uF2A3";  // v  ic_fluent_chevron_down_20_regular
    public const string Settings     = "\uF6A9";  // ⚙  ic_fluent_settings_20_regular
    public const string Add          = "\uF109";  // +  ic_fluent_add_20_regular
    public const string Close        = "\uF369";  // ×  ic_fluent_dismiss_20_regular
    public const string Dirty        = "\uF660";  // ●  ic_fluent_record_16_regular
}
