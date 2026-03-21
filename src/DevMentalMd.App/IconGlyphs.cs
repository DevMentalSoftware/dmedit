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
    public const string ChevronRight = "\uF2AE";  // >  ic_fluent_chevron_right_20_regular
    public const string ChevronUp    = "\uF2B4";  // ^  ic_fluent_chevron_up_20_regular
    public const string ChevronDown  = "\uF2A1";  // v  ic_fluent_chevron_down_20_regular
    public const string ChevronLeft  = "\uF2A8";  // v  ic_fluent_chevron_left_20_regular
    public const string Settings     = "\uF6A9";  // ⚙  ic_fluent_settings_20_regular
    public const string Add          = "\uF109";  // +  ic_fluent_add_20_regular
    public const string Close        = "\uF367";  // ×  ic_fluent_dismiss_20_regular
    public const string ArrowLeft    = "\uF184";  // ←  ic_fluent_arrow_left_20_regular
    public const string ArrowRight   = "\uF182";  // →  ic_fluent_arrow_right_20_regular
    public const string Dirty        = "\uF660";  // ●  ic_fluent_record_16_regular

    // Reset / undo
    public const string ArrowUndo    = "\uF199";  // ↺  TODO: verify codepoint against bundled font

    // Tail file indicator
    public const string ArrowDown    = "\uF126";  // ↓  ic_fluent_arrow_download_20_regular

    // Conflict / error indicators
    public const string ErrorCircle  = "\uF4A0";  // TODO: placeholder — pick a better error/warning glyph

    // Window chrome glyphs (Linux custom title bar)
    public const string Minimize     = "\uEBD0";  // ─  ic_fluent_subtract_20_regular
    public const string Maximize     = "\uE7EB";  // □  ic_fluent_maximize_20_regular
    public const string Restore      = "\uEB96";  // ⧉  ic_fluent_square_multiple_20_regular
}
