using Avalonia.Media;

namespace DMEdit.App;

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

    public const string H1 = "\uF7EF";
    public const string H2 = "\uF7F0";
    public const string H3 = "\uF7F1";

    // Reset / undo
    public const string ArrowUndo    = "\uF199";  // ↺  

    // Tail file indicator
    public const string ArrowDown    = "\uF126";  // ↓  ic_fluent_arrow_download_20_regular

    public const string Undo = "\uE127"; //F199
    public const string Redo = "\uE0E5";//F16E
    public const string Cut = "\uF33A"; //F33B
    public const string Copy = "\uEEB5"; //
    public const string Paste = "\uE342";
    public const string Delete = "\uE47B"; //EEA5,EEA6  
    public const string Wrap = "\uEF28"; //F80E

    public const string Open = "\uF42E";
    public const string Save = "\uEA43";
    public const string Search = "\uEB22";

    // Placeholder toolbar icons
    public const string ClipboardRing  = "\uEF13";
    public const string SaveAs         = "\uEA49";
    public const string RevertFile     = "\uE47C";
    public const string Print          = "\uE427";
    public const string SaveAsPdf      = "\uE528";
    public const string ReloadFile     = "\uE110";
    public const string Replace        = "\uEAF9";
    public const string GoToLine       = "\uE0B0";
    public const string CommandPalette = "\uEE87";
    public const string LineNumbers    = "\uE887";
    public const string Whitespace     = "\uF6F8";
    public const string ZoomIn         = "\uEE8E";
    public const string ZoomOut        = "\uEE8F";
    public const string ZoomReset      = "\uF860";

    // TabToolbar icons
    public const string SaveAll        = "\uEA4C";  // ic_fluent_save_multiple_20_regular
    public const string StatusBar      = "\uF58B";  // ic_fluent_panel_bottom_20_regular
    public const string History        = "\uE36E";

    // Conflict / error indicators
    public const string ErrorCircle  = "\uF4A0";  

    // Window chrome glyphs (Linux custom title bar)
    public const string Minimize     = "\uEBD0";  // ─  ic_fluent_subtract_20_regular
    public const string Maximize     = "\uE7EB";  // □  ic_fluent_maximize_20_regular
    public const string Restore      = "\uEB96";  // ⧉  ic_fluent_square_multiple_20_regular
}
