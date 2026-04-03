using System;
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

    public static readonly IReadOnlyList<SettingDescriptor> All = [
        // -- Display --
        new("WrapLinesAt", "Wrap Lines At",
            "When wrapping is enabled we can force wrapping at a particular width when the window is wider than that width.",
            "Display", SettingKind.Int, 100, Min: 0, Max: 10000),

        new("CharWrapFileSizeKB", "Char Wrap File Size (KB)",
            "File size above which character-wrapping mode activates automatically. " +
            "Lower values improve responsiveness on slower machines.",
            "Advanced", SettingKind.Int, 50, Min: 10, Max: 10000, Increment: 10),

        new("CaretWidth", "Caret Width",
            "Width of the text caret in pixels. Range: 1.0 – 2.5.",
            "Display", SettingKind.Double, 1.0, Min: 1.0, Max: 2.5, Increment: 0.5),

        new("BrightSelection", "Bright Selection",
            "Use a brighter, more visible selection highlight. Useful on monitors with limited contrast.",
            "Display", SettingKind.Bool, false),

        new("ThemeMode", "Theme",
            "Select the theme for the display.",
            "Display", SettingKind.Enum, ThemeMode.System, EnumType: typeof(ThemeMode)),

        new("OuterThumbScrollRateMultiplier", "Outer Thumb Scroll Rate",
            "Multiplier for the outer-thumb fixed scroll rate. 1.0 = baseline. Higher = faster scanning.",
            "Scrollbar", SettingKind.Double, 2.0, Min: 0.1, Max: 20.0),

        // -- Editor --
        new("IndentWidth", "Indent Width",
            "Number of columns per indent level. Controls tab display width and the number of spaces inserted when indenting.",
            "Editor", SettingKind.Int, 4, Min: 1, Max: 16),

        new("CoalesceTimerMs", "Undo Coalesce Timer (ms)",
            "Idle time in milliseconds before consecutive edits are committed as a single undo entry. Minimum 100.",
            "Editor", SettingKind.Int, 1000, Min: 100, Max: 10000),

        new("AutoReloadExternalChanges", "Auto-Reload External Changes",
            "When enabled, files modified externally are automatically reloaded if the tab has no unsaved edits.",
            "Editor", SettingKind.Bool, false),

        new("BackupOnSave", "Backup on Save",
            "Keep a .bak copy of the previous version when saving.",
            "Editor", SettingKind.Bool, false),

        new("TailFile", "Tail File",
            "When a file is reloaded and the editor is scrolled to the bottom, automatically scroll to show new content.",
            "Editor", SettingKind.Bool, false),

        new("ExpandSelectionMode", "Expand Selection Mode",
            "Controls how Expand Selection grows the selection. " +
            "'SubwordFirst' starts at camelCase/underscore boundaries; 'Word' starts at whitespace boundaries.",
            "Editor", SettingKind.Enum, ExpandSelectionMode.Word, EnumType: typeof(ExpandSelectionMode)),

        // -- Advanced --
        new("RecentFileCount", "Recent File Count",
            "Number of recent files shown in the File menu.",
            "Advanced", SettingKind.Int, 10, Min: 0, Max: 50),

        new("DevMode", "Developer Mode",
            "Enable developer-mode features (performance stats, detailed errors).",
            "Advanced", SettingKind.Bool, false),

        new("ShowStatistics", "Show Statistics",
            "Show developer performance statistics on the Status Bar.",
            "Advanced", SettingKind.Bool, true, EnabledWhenKey: "DevMode"),

        // -- Commands --
        new("ChordTimeoutMs", "Chord Timeout (ms)",
            "Time in milliseconds before a pending chord prefix is cancelled. " +
            "Press the first key of a chord, then the second key within this timeout.",
            "Commands", SettingKind.Int, 3000, Min: 500, Max: 10000),

    ];
}
