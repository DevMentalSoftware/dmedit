using System;
using System.Collections.Generic;
using DevMentalMd.App.Services;

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
    ];

    public static readonly IReadOnlyList<SettingDescriptor> All = [
        // -- Display --
        new("ShowLineNumbers", "Show Line Numbers",
            "Display line numbers in a gutter on the left side of the editor.",
            "Display", SettingKind.Bool, true),

        new("ShowStatusBar", "Show Status Bar",
            "Show the permanent status bar (line/column, file info) at the bottom.",
            "Display", SettingKind.Bool, true),

        new("WrapLines", "Wrap Lines",
            "Wrap long lines at the viewport edge or column limit.",
            "Display", SettingKind.Bool, true),

        new("WrapLinesAt", "Wrap Lines At",
            "Maximum columns before a line wraps. Only effective when Wrap Lines is enabled. Set to 0 for viewport-only wrapping.",
            "Display", SettingKind.Int, 100, Min: 0, Max: 10000),

        // -- Theme --
        new("ThemeMode", "Theme",
            "Select the theme for the display.",
            "Display", SettingKind.Enum, ThemeMode.System, EnumType: typeof(ThemeMode)),

        // -- Editor --
        new("CoalesceTimerMs", "Undo Coalesce Timer (ms)",
            "Idle time in milliseconds before consecutive edits are committed as a single undo entry. Minimum 100.",
            "Editor", SettingKind.Int, 1000, Min: 100, Max: 10000),

        // -- Scrollbar --
        new("OuterThumbScrollRateMultiplier", "Outer Thumb Scroll Rate",
            "Multiplier for the outer-thumb fixed scroll rate. 1.0 = baseline. Higher = faster scanning.",
            "Scrollbar", SettingKind.Double, 2.0, Min: 0.1, Max: 20.0),

        // -- Advanced --
        new("RecentFileCount", "Recent File Count",
            "Number of recent files shown in the File menu.",
            "Advanced", SettingKind.Int, 10, Min: 0, Max: 50),

        new("PagedBufferThresholdBytes", "Paged Buffer Threshold (bytes)",
            "Files larger than this use the paged buffer instead of loading entirely into memory.",
            "Advanced", SettingKind.Long, 50L * 1024 * 1024, Min: 1L * 1024 * 1024, Max: 500L * 1024 * 1024),

        new("DevMode", "Developer Mode",
            "Enable developer-mode features (performance stats, sample documents).",
            "Advanced", SettingKind.Bool, false),

        new("ShowStatistics", "Show Statistics",
            "Show developer performance statistics on the Status Bar.",
            "Advanced", SettingKind.Bool, true),

    ];
}
