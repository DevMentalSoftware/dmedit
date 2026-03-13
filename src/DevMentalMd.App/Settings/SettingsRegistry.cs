using System;
using System.Collections.Generic;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.App.Settings;

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
        "Keyboard",
    ];

    public static readonly IReadOnlyList<SettingDescriptor> All = [
        // -- Display --
        new("WrapLinesAt", "Wrap Lines At",
            "When wrapping is enabled we can force wrapping at a particular width when the window is wider than that width.",
            "Display", SettingKind.Int, 100, Min: 0, Max: 10000),

        new("ThemeMode", "Theme",
            "Select the theme for the display.",
            "Display", SettingKind.Enum, ThemeMode.System, EnumType: typeof(ThemeMode)),

        new("OuterThumbScrollRateMultiplier", "Outer Thumb Scroll Rate",
            "Multiplier for the outer-thumb fixed scroll rate. 1.0 = baseline. Higher = faster scanning.",
            "Scrollbar", SettingKind.Double, 2.0, Min: 0.1, Max: 20.0),

        // -- Editor --
        new("CoalesceTimerMs", "Undo Coalesce Timer (ms)",
            "Idle time in milliseconds before consecutive edits are committed as a single undo entry. Minimum 100.",
            "Editor", SettingKind.Int, 1000, Min: 100, Max: 10000),

        new("ExpandSelectionMode", "Expand Selection Mode",
            "Controls how Expand Selection grows the selection. " +
            "'SubwordFirst' starts at camelCase/underscore boundaries; 'Word' starts at whitespace boundaries.",
            "Editor", SettingKind.Enum, ExpandSelectionMode.SubwordFirst, EnumType: typeof(ExpandSelectionMode)),

        // -- Advanced --
        new("RecentFileCount", "Recent File Count",
            "Number of recent files shown in the File menu.",
            "Advanced", SettingKind.Int, 10, Min: 0, Max: 50),

        new("DevMode", "Developer Mode",
            "Enable developer-mode features (performance stats, sample documents).",
            "Advanced", SettingKind.Bool, false),

        new("ShowStatistics", "Show Statistics",
            "Show developer performance statistics on the Status Bar.",
            "Advanced", SettingKind.Bool, true),

        // -- Keyboard --
        new("ChordTimeoutMs", "Chord Timeout (ms)",
            "Time in milliseconds before a pending chord prefix is cancelled. " +
            "Press the first key of a chord, then the second key within this timeout.",
            "Keyboard", SettingKind.Int, 3000, Min: 500, Max: 10000),

    ];
}
