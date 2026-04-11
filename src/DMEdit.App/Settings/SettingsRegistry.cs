using System.Collections.Generic;
using DMEdit.App.Services;
using DMEdit.Core.Documents;

namespace DMEdit.App.Settings;

/// <summary>
/// Single source of truth for all user-visible settings. Each entry maps
/// an <see cref="AppSettings"/> property to its display metadata.
/// </summary>
public static class SettingsRegistry {
    public static readonly IReadOnlyList<string> Categories = [
        "Display",
        "Editor",
        "Scrollbar",
        "Advanced",
        "Commands",
    ];

    public static readonly IReadOnlyList<ISettingDescriptor> All = [
        // -- Display --
        new SettingDescriptor<bool>("UseWrapColumn", "Use Wrap Column",
            "When enabled, lines wrap at the Wrap Column limit instead of the viewport edge.",
            "Display", true),

        new SettingDescriptor<int>("WrapLinesAt", "Wrap Column",
            "Column at which lines wrap when Use Wrap Column is enabled.",
            "Display", 100, Min: 1, Max: 10000, EnabledWhenKey: "UseWrapColumn"),

        new SettingDescriptor<bool>("ShowWrapSymbol", "Show Wrap Symbol",
            "Show a wrap indicator glyph at the wrap column for lines that word-wrap to the next row.",
            "Display", true),

        new SettingDescriptor<bool>("HangingIndent", "Hanging Indent",
            "Indent wrapped continuation rows by half of one indent level so wrapped text " +
            "is visually offset from the first row.  Currently applies only to monospace fonts.",
            "Display", true, EnabledWhenKey: "UseFastTextLayout"),

        new SettingDescriptor<bool>("UseFastTextLayout", "Fast Text Layout",
            "Render monospace lines through the GlyphRun fast path.  Much faster and enables " +
            "hanging indent, but disables font ligatures (e.g. => rendered as a single glyph).  " +
            "Turn off if you prefer ligatures over speed and hanging indent.",
            "Display", true),

        new SettingDescriptor<int>("CharWrapFileSizeKB", "Char Wrap File Size (KB)",
            "Files larger than this (in KB) that also contain a line longer than " +
            "CharWrapLineLength (2000) switch automatically into character-wrapping " +
            "mode.  The size gate avoids penalizing small files with a long line, because " +
            "those are still cheap to measure.",
            "Advanced", 50, Min: 10, Max: 10000, Increment: 10),

        new SettingDescriptor<bool>("DistributeColumnPaste", "Distribute Multi-Line Paste in Column Mode",
            "When enabled, pasting a multi-line clipboard into a column-mode " +
            "selection distributes one line per cursor when the line counts match " +
            "(extra lines are dropped).  When disabled, the entire clipboard is " +
            "broadcast at every cursor — matches VS Code, Sublime, Rider.",
            "Advanced", true),

        new SettingDescriptor<double>("CaretWidth", "Caret Width",
            "Width of the text caret in pixels. Range: 1.0 – 2.5.",
            "Display", 1.0, Min: 1.0, Max: 2.5, Increment: 0.5),

        new SettingDescriptor<bool>("BrightSelection", "Bright Selection",
            "Use a brighter, more visible selection highlight. Useful on monitors with limited contrast.",
            "Display", false),

        new SettingDescriptor<ThemeMode>("ThemeMode", "Theme",
            "Select the theme for the display.",
            "Display", ThemeMode.System),

        new SettingDescriptor<double>("OuterThumbScrollRateMultiplier", "Outer Thumb Scroll Rate",
            "Multiplier for the outer-thumb fixed scroll rate. 1.0 = baseline. Higher = faster scanning.",
            "Scrollbar", 2.0, Min: 0.1, Max: 20.0),

        // -- Editor --
        new SettingDescriptor<int>("IndentWidth", "Indent Width",
            "Number of columns per indent level. Controls tab display width and the number of spaces inserted when indenting.",
            "Editor", 4, Min: 1, Max: 16),

        new SettingDescriptor<int>("CoalesceTimerMs", "Undo Coalesce Timer (ms)",
            "Idle time in milliseconds before consecutive edits are committed as a single undo entry. Minimum 100.",
            "Editor", 1000, Min: 100, Max: 10000),

        new SettingDescriptor<bool>("AutoReloadExternalChanges", "Auto-Reload External Changes",
            "When enabled, files modified externally are automatically reloaded if the tab has no unsaved edits.",
            "Editor", false),

        new SettingDescriptor<bool>("BackupOnSave", "Backup on Save",
            "Keep a .bak copy of the previous version when saving.",
            "Editor", false),

        new SettingDescriptor<bool>("TailFile", "Tail File",
            "When a file is reloaded and the editor is scrolled to the bottom, automatically scroll to show new content.",
            "Editor", false),

        new SettingDescriptor<ExpandSelectionMode>("ExpandSelectionMode", "Expand Selection Mode",
            "Controls how Expand Selection grows the selection. " +
            "'SubwordFirst' starts at camelCase/underscore boundaries; 'Word' starts at whitespace boundaries.",
            "Editor", ExpandSelectionMode.Word),

        // -- Advanced --
        new SettingDescriptor<int>("RecentFileCount", "Recent File Count",
            "Number of recent files shown in the File menu.",
            "Advanced", 10, Min: 0, Max: 50),

        new SettingDescriptor<bool>("AutoUpdate", "Auto-Update",
            "Download updates silently on startup and show a restart indicator " +
            "in the status bar. When off, updates are still detected but require " +
            "a manual download from Settings.",
            "Advanced", true),

        new SettingDescriptor<bool>("DevMode", "Developer Mode",
            "Enable developer-mode features (performance stats, detailed errors).",
            "Advanced", false, Hidden: true),

        new SettingDescriptor<bool>("ShowStatistics", "Show Statistics",
            "Show developer performance statistics on the Status Bar.",
            "Advanced", true, EnabledWhenKey: "DevMode", Hidden: true),

        // -- Commands --
        new SettingDescriptor<int>("ChordTimeoutMs", "Chord Timeout (ms)",
            "Time in milliseconds before a pending chord prefix is cancelled. " +
            "Press the first key of a chord, then the second key within this timeout.",
            "Commands", 3000, Min: 500, Max: 10000),

    ];
}
