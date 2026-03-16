using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.App.Services;

public enum ThemeMode {
    System,
    Light,
    Dark,
}

/// <summary>
/// Application settings persisted to <c>%APPDATA%/DevMentalMD/settings.json</c>.
/// Add new properties as the app evolves — unknown keys in the JSON are silently
/// ignored, and missing keys fall back to defaults.
/// </summary>
public sealed class AppSettings {
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevMentalMD", "settings.json");

    // WhenWritingNull (not WhenWritingDefault) so that value-type properties
    // at their CLR default (e.g. bool false) are always serialized.
    // WhenWritingDefault would silently drop false / 0 / 0.0, and on reload
    // the property initializer (e.g. true) would kick in — losing the change.
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -----------------------------------------------------------------
    // Developer mode
    // -----------------------------------------------------------------

    /// <summary>
    /// Enables developer-mode features (perf stats, sample documents) in Release builds.
    /// Always enabled in DEBUG builds regardless of this setting.
    /// </summary>
    public bool DevMode { get; set; } = true;

    // -----------------------------------------------------------------
    // Scrollbar
    // -----------------------------------------------------------------

    /// <summary>
    /// Multiplier applied to the outer-thumb fixed scroll rate.
    /// 1.0 = baseline (~100-line-doc feel). Higher = faster scanning.
    /// Default 2.0 — empirically feels good for quickly moving through
    /// large documents while still being controllable.
    /// </summary>
    public double OuterThumbScrollRateMultiplier { get; set; } = 2.0;

    // -----------------------------------------------------------------
    // Recent files
    // -----------------------------------------------------------------

    /// <summary>
    /// Number of recent files to display in the File menu.
    /// </summary>
    public int RecentFileCount { get; set; } = 10;

    // -----------------------------------------------------------------
    // Display
    // -----------------------------------------------------------------

    /// <summary>
    /// Show line numbers in a gutter on the left side of the editor.
    /// </summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>
    /// Show the permanent status bar (line/column, file info) at the bottom.
    /// </summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// Show developer performance statistics bars (layout/render timing, memory).
    /// Only visible when <see cref="DevMode"/> is also enabled.
    /// </summary>
    public bool ShowStatistics { get; set; } = true;

    /// <summary>
    /// Show visible glyphs for whitespace characters (spaces, tabs, NBSP).
    /// </summary>
    public bool ShowWhitespace { get; set; }

    /// <summary>
    /// Wrap long lines at the viewport edge. When false, lines extend beyond
    /// the visible area (horizontal scrolling planned for a future release).
    /// </summary>
    public bool WrapLines { get; set; } = true;

    /// <summary>
    /// Maximum number of columns before a line wraps. Wrapping occurs at the
    /// viewport edge or this column limit, whichever is narrower. Only has
    /// effect when <see cref="WrapLines"/> is true. Default: 100.
    /// </summary>
    public int WrapLinesAt { get; set; } = 100;

    /// <summary>
    /// Number of spaces per indent level. Also controls the visual width of
    /// tab characters. Default: 4.
    /// </summary>
    public int IndentWidth { get; set; } = 4;

    /// <summary>
    /// Default indentation style for new/untitled documents. Files opened from
    /// disk use the detected style instead. Default: Spaces.
    /// </summary>
    public IndentStyle DefaultIndentStyle { get; set; } = IndentStyle.Spaces;

    /// <summary>
    /// When true, files modified on disk are automatically reloaded if the
    /// tab has no unsaved edits. Dirty tabs still show the conflict icon.
    /// Default: false.
    /// </summary>
    public bool AutoReloadExternalChanges { get; set; }

    // -----------------------------------------------------------------
    // Theme
    // -----------------------------------------------------------------

    /// <summary>
    /// Controls the color theme. System (default) follows the OS setting.
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    // -----------------------------------------------------------------
    // Window state
    // -----------------------------------------------------------------

    /// <summary>Unmaximized window width (DIPs). Null = use XAML default.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Unmaximized window height (DIPs). Null = use XAML default.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Unmaximized window left position (screen pixels). Null = OS default.</summary>
    public int? WindowLeft { get; set; }

    /// <summary>Unmaximized window top position (screen pixels). Null = OS default.</summary>
    public int? WindowTop { get; set; }

    /// <summary>Whether the window was maximized when last closed.</summary>
    public bool WindowMaximized { get; set; }

    // -----------------------------------------------------------------
    // Undo coalescing
    // -----------------------------------------------------------------

    /// <summary>
    /// Idle time (in milliseconds) before consecutive edits are committed as
    /// a single undo entry. Continuous typing resets the timer on every
    /// keystroke, so only actual pauses trigger a flush. Default: 1000.
    /// </summary>
    public int CoalesceTimerMs { get; set; } = 1000;

    /// <summary>
    /// Controls the hierarchy of levels used by Expand Selection.
    /// <see cref="ExpandSelectionMode.SubwordFirst"/> starts with camelCase/underscore
    /// boundaries; <see cref="ExpandSelectionMode.Word"/> starts with whitespace boundaries.
    /// </summary>
    public ExpandSelectionMode ExpandSelectionMode { get; set; } = ExpandSelectionMode.SubwordFirst;

    // -----------------------------------------------------------------
    // Settings UI state
    // -----------------------------------------------------------------

    /// <summary>
    /// Remembers which category was selected in the Settings sidebar
    /// (e.g. "All Settings", "Keyboard"). Null = "All Settings" (default).
    /// </summary>
    public string? LastSettingsPage { get; set; }

    // -----------------------------------------------------------------
    // Find bar
    // -----------------------------------------------------------------

    /// <summary>
    /// Remembered width of the find bar in DIPs. Null = use default MinWidth.
    /// </summary>
    public double? FindBarWidth { get; set; }

    /// <summary>
    /// Most-recently-used search terms (newest first). Max 20 entries.
    /// </summary>
    public List<string>? RecentFindTerms { get; set; }

    /// <summary>
    /// Most-recently-used replace terms (newest first). Max 20 entries.
    /// </summary>
    public List<string>? RecentReplaceTerms { get; set; }

    private const int MaxRecentTerms = 20;

    /// <summary>
    /// Pushes a term to the front of the recent find terms list, deduplicating
    /// and truncating to <see cref="MaxRecentTerms"/> entries.
    /// </summary>
    public void PushRecentFindTerm(string term) {
        if (string.IsNullOrEmpty(term)) return;
        RecentFindTerms ??= new List<string>();
        RecentFindTerms.Remove(term);
        RecentFindTerms.Insert(0, term);
        if (RecentFindTerms.Count > MaxRecentTerms)
            RecentFindTerms.RemoveRange(MaxRecentTerms, RecentFindTerms.Count - MaxRecentTerms);
    }

    /// <summary>
    /// Pushes a term to the front of the recent replace terms list, deduplicating
    /// and truncating to <see cref="MaxRecentTerms"/> entries.
    /// </summary>
    public void PushRecentReplaceTerm(string term) {
        if (string.IsNullOrEmpty(term)) return;
        RecentReplaceTerms ??= new List<string>();
        RecentReplaceTerms.Remove(term);
        RecentReplaceTerms.Insert(0, term);
        if (RecentReplaceTerms.Count > MaxRecentTerms)
            RecentReplaceTerms.RemoveRange(MaxRecentTerms, RecentReplaceTerms.Count - MaxRecentTerms);
    }

    // -----------------------------------------------------------------
    // File dialogs
    // -----------------------------------------------------------------

    /// <summary>
    /// Last directory used in an Open or Save file dialog. Null = OS default.
    /// Updated whenever a file is opened or saved, so the next dialog starts
    /// in the most recently used location.
    /// </summary>
    public string? LastFileDialogDir { get; set; }

    // -----------------------------------------------------------------
    // Keyboard shortcuts
    // -----------------------------------------------------------------

    /// <summary>
    /// Active key mapping profile identifier (e.g. "VSCode", "Emacs").
    /// Null means "Default". Stored as null for backward compatibility
    /// so existing settings files that lack this property use the Default profile.
    /// </summary>
    public string? ActiveProfile { get; set; }

    /// <summary>
    /// User overrides for primary keyboard shortcuts. Maps command ID (e.g.
    /// <c>"Edit.Undo"</c>) to gesture string (e.g. <c>"Ctrl+Y"</c>).
    /// Empty string means the command is explicitly unbound.
    /// Null means no overrides — all defaults from the active profile apply.
    /// </summary>
    public Dictionary<string, string>? KeyBindingOverrides { get; set; }

    /// <summary>
    /// User overrides for secondary keyboard shortcuts. Same format as
    /// <see cref="KeyBindingOverrides"/> but for the Gesture2 slot.
    /// </summary>
    public Dictionary<string, string>? KeyBinding2Overrides { get; set; }

    /// <summary>
    /// Timeout in milliseconds before a pending chord prefix is cancelled.
    /// Default: 3000 (3 seconds).
    /// </summary>
    public int ChordTimeoutMs { get; set; } = 3000;

    // -----------------------------------------------------------------
    // Clipboard ring
    // -----------------------------------------------------------------

    /// <summary>
    /// Maximum number of entries kept in the clipboard ring. Hidden setting
    /// (not shown in the Settings UI). Default: 10.
    /// </summary>
    public int ClipboardRingSize { get; set; } = 10;

    // -----------------------------------------------------------------
    // Printing
    // -----------------------------------------------------------------

    /// <summary>Last-used printer name. Null = system default.</summary>
    public string? PrinterName { get; set; }

    /// <summary>Last-used paper size name (matched against available sizes on load).</summary>
    public string? PaperSizeName { get; set; }

    /// <summary>Last-used page orientation. Default: Portrait.</summary>
    public string? PageOrientation { get; set; }

    /// <summary>Last-used top margin in inches.</summary>
    public double? MarginTopInches { get; set; }

    /// <summary>Last-used right margin in inches.</summary>
    public double? MarginRightInches { get; set; }

    /// <summary>Last-used bottom margin in inches.</summary>
    public double? MarginBottomInches { get; set; }

    /// <summary>Last-used left margin in inches.</summary>
    public double? MarginLeftInches { get; set; }

    // -----------------------------------------------------------------
    // Load / Save
    // -----------------------------------------------------------------

    /// <summary>
    /// Loads settings from disk. Returns defaults on any failure.
    /// </summary>
    public static AppSettings Load() {
        try {
            if (File.Exists(StorePath)) {
                var json = File.ReadAllText(StorePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (settings is not null) {
                    return settings;
                }
            }
        } catch {
            // Corrupted or unreadable — use defaults.
        }
        return new AppSettings();
    }

    /// <summary>
    /// Persists settings to disk. Failures are silently swallowed (best-effort).
    /// </summary>
    public void Save() {
        try {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOpts));
        } catch {
            // Best-effort — non-fatal.
        }
    }

    // -----------------------------------------------------------------
    // Debounced save
    // -----------------------------------------------------------------

    private Timer? _saveTimer;

    /// <summary>
    /// Schedules a debounced save — the file is written 500 ms after the last
    /// call. Rapid successive changes are coalesced into a single write.
    /// </summary>
    public void ScheduleSave() {
        if (_saveTimer != null) {
            _saveTimer.Change(500, Timeout.Infinite);
        } else {
            _saveTimer = new Timer(_ => Save(), null, 500, Timeout.Infinite);
        }
    }
}
