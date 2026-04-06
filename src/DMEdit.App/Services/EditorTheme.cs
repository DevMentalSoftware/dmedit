using Avalonia.Media;

namespace DMEdit.App.Services;

/// <summary>
/// Centralizes all UI colors for the editor. Two static instances (Light and
/// Dark) provide palettes that controls reference at render time.
/// </summary>
public sealed class EditorTheme {
    // -- Editor surface --
    public IBrush EditorBackground { get; init; } = 
        new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    public IBrush EditorForeground { get; init; } = Brushes.Black;
    public IBrush CaretBrush { get; init; } = Brushes.Black;
    public IBrush SelectionBrush { get; init; } =
        new SolidColorBrush(Color.FromArgb(255, 0x97, 0xc6, 0xea));
    public IBrush BrightSelectionBrush { get; init; } =
        new SolidColorBrush(Color.FromArgb(255, 0x99, 0xd7, 0xFF));

    // -- Gutter --
    public IBrush GutterBackground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
    public IBrush GutterForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));

    // -- Column guide --
    public IPen GuideLinePen { get; init; } = new Pen(
        new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)), 1);

    // -- Whitespace glyphs --
    private static readonly Color WhitespaceGlyphLight = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    public IBrush WhitespaceGlyphBrush { get; init; } = new SolidColorBrush(WhitespaceGlyphLight);
    public IPen WhitespaceGlyphPen { get; init; } = new Pen(new SolidColorBrush(WhitespaceGlyphLight), 1);

    // -- Wrap symbol --
    private static readonly Color AccentBlue = Color.FromRgb(0x00, 0x78, 0xD7);
    public IPen WrapSymbolPen { get; init; } = new Pen(new SolidColorBrush(AccentBlue), 1);

    // -- Tab bar --
    public IBrush TabBarBackground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    public IBrush TabBarBorder { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA));
    public IBrush TabActiveBackground { get; init; } = 
        new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
    public IBrush TabInactiveBackground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    public IBrush TabBorder { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA));
    public IBrush TabForeground { get; init; } = Brushes.Black;
    public IBrush TabCloseForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    public IBrush TabInactiveHoverBg { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    public IBrush TabCloseHoverBg { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    public IBrush TabToolButtonForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    public IBrush TabErrorIconForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    public IBrush ChromeButtonHoverBg { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    public IBrush ChromeButtonForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
    public IBrush ChromeButtonForegroundActive { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
    public IBrush ChromeCloseButtonHoverBg { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    public IBrush ChromeCloseButtonForeground { get; init; } =
        new SolidColorBrush(Colors.White);

    // -- Status bar --
    public IBrush StatusBarBackground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
    public IBrush StatusBarBorder { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
    public IBrush StatusBarForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    public IBrush StatusBarWarning { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xC8, 0xA0, 0x30));

    // -- Scrollbar --
    public IBrush ScrollTrack { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
    public IBrush ScrollArrowBg { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    public IBrush ScrollArrowBgHover { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
    public IBrush ScrollArrowBgPress { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
    public IBrush ScrollArrowGlyph { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    public IBrush ScrollInnerThumbNormal { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
    public IBrush ScrollInnerThumbHover { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
    public IBrush ScrollInnerThumbPress { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    public IBrush ScrollOuterThumbNormal { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xD0));
    public IBrush ScrollOuterThumbHover { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x98, 0xA0, 0xB8));
    public IBrush ScrollOuterThumbPress { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x78, 0x80, 0x9C));

    // -- Menu --
    public IBrush MenuBackground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));

    // -- Settings UI --
    public IBrush SettingsDimForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    public IBrush SettingsWarnForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x00));
    public IBrush SettingsErrorForeground { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    public IBrush SettingsInputBorder { get; init; } =
        new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    public IBrush SettingsAccent { get; init; } =
        new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
    public IBrush SettingsRowSelection { get; init; } =
        new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7));
    public IBrush SettingsButtonActive { get; init; } =
        new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD7));

    // =================================================================
    // Predefined palettes
    // =================================================================

    public static EditorTheme Light { get; } = new();

    private static readonly Color WhitespaceGlyphDark = Color.FromArgb(0xFF, 0xB0, 0xB0, 0xB0);

    public static EditorTheme Dark { get; } = new() {
        // Editor surface
        EditorBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
        EditorForeground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
        CaretBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
        SelectionBrush = new SolidColorBrush(Color.FromArgb(255, 0x26, 0x4F, 0x78)),
        BrightSelectionBrush = new SolidColorBrush(Color.FromArgb(255, 0, 0x78, 0xD4)),

        // Gutter
        GutterBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
        GutterForeground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)),

        // Column guide
        GuideLinePen = new Pen(
            new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1),

        // Whitespace glyphs
        WhitespaceGlyphBrush = new SolidColorBrush(WhitespaceGlyphDark),
        WhitespaceGlyphPen = new Pen(new SolidColorBrush(WhitespaceGlyphDark), 1),

        // Tab bar
        TabBarBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        TabBarBorder = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
        TabActiveBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        TabInactiveBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        TabBorder = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
        TabForeground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
        TabCloseForeground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        TabInactiveHoverBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        TabCloseHoverBg = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        TabToolButtonForeground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        TabErrorIconForeground = new SolidColorBrush(Color.FromRgb(0xF4, 0x4B, 0x3C)),
        ChromeButtonHoverBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        ChromeButtonForeground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        ChromeButtonForegroundActive = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        //ChromeCloseButtonForeground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),

        // Status bar
        StatusBarBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        StatusBarBorder = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        StatusBarForeground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
        StatusBarWarning = new SolidColorBrush(Color.FromRgb(0xD4, 0xA8, 0x30)),

        // Scrollbar
        ScrollTrack = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        ScrollArrowBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
        ScrollArrowBgHover = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
        ScrollArrowBgPress = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
        ScrollArrowGlyph = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
        ScrollInnerThumbNormal = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        ScrollInnerThumbHover = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
        ScrollInnerThumbPress = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
        ScrollOuterThumbNormal = new SolidColorBrush(Color.FromRgb(0x3C, 0x44, 0x5C)),
        ScrollOuterThumbHover = new SolidColorBrush(Color.FromRgb(0x4C, 0x54, 0x6C)),
        ScrollOuterThumbPress = new SolidColorBrush(Color.FromRgb(0x5C, 0x64, 0x7C)),

        // Menu
        MenuBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),

        // Settings UI
        SettingsDimForeground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
        SettingsWarnForeground = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x33)),
        SettingsInputBorder = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        SettingsAccent = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
        SettingsRowSelection = new SolidColorBrush(Color.FromArgb(0x30, 0x33, 0x99, 0xFF)),
        SettingsButtonActive = new SolidColorBrush(Color.FromArgb(0x40, 0x33, 0x99, 0xFF)),
    };
}
