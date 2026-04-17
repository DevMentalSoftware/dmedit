using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Services;

public enum ThemeMode {
    System,
    Light,
    Dark,
}

/// <summary>
/// Application settings persisted to <c>%APPDATA%/DMEdit/settings.json</c>.
/// Add new properties as the app evolves — unknown keys in the JSON are silently
/// ignored, and missing keys fall back to defaults.
/// </summary>
public sealed class AppSettings {
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DMEdit", "settings.json");

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

    public bool DevMode { get; set; }

    // -----------------------------------------------------------------
    // Scrollbar
    // -----------------------------------------------------------------

    /// <summary>
    /// Multiplier applied to the outer-thumb fixed scroll rate.
    /// 1.0 = baseline (~100-line-doc feel). Higher = faster scanning.
    /// </summary>
    public double OuterThumbScrollRateMultiplier { get; set; } = 3.0;

    // -----------------------------------------------------------------
    // Recent files
    // -----------------------------------------------------------------

    /// <summary>
    /// When true, hide menu items marked as advanced (e.g. Clipboard Ring,
    /// Incremental Search, line manipulation commands). Does NOT affect the
    /// command palette or toolbar — only menus.
    /// </summary>
    public bool HideAdvancedMenus { get; set; } = true;

    /// <summary>
    /// Per-command overrides for menu inclusion. Maps command ID to nullable bool:
    /// true = always show, false = always hide, null = use default (which depends
    /// on <see cref="HideAdvancedMenus"/> for advanced commands).
    /// </summary>
    public Dictionary<string, bool?>? MenuOverrides { get; set; }

    /// <summary>
    /// Per-command overrides for toolbar inclusion. Maps command ID to bool.
    /// When a key is present, it overrides the manifest default.
    /// </summary>
    public Dictionary<string, bool>? ToolbarOverrides { get; set; }

    /// <summary>
    /// Number of recent files to display in the File menu.
    /// </summary>
    public int RecentFileCount { get; set; } = 10;

    // -----------------------------------------------------------------
    // Command palette
    // -----------------------------------------------------------------

    /// <summary>
    /// Whether the command palette groups commands by category.
    /// </summary>
    public bool CommandPaletteGroupByCategory { get; set; }

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
    /// Show a wrap indicator glyph at the wrap column for lines that wrap.
    /// Only visible when <see cref="WrapLines"/> is true.
    /// </summary>
    public bool ShowWrapSymbol { get; set; } = true;

    /// <summary>
    /// Highlight the row containing the caret with a translucent band that
    /// spans the full editor width (gutter + text).  Off by default.
    /// </summary>
    public bool HighlightCurrentLine { get; set; }

    /// <summary>
    /// Indent wrapped continuation rows by half of one indent level so wrapped
    /// text is visually offset from the first row of each logical line.
    /// Only takes effect when wrapping is on, the editor font is monospace,
    /// and the GlyphRun fast path is engaged.  
    /// </summary>
    public bool HangingIndent { get; set; } = true;

    /// <summary>
    /// Route monospace lines through the <c>MonoLineLayout</c> GlyphRun fast
    /// path when possible.  Turning this off forces every line through
    /// Avalonia's <c>TextLayout</c>, which is slower and disables hanging
    /// indent but enables ligatures (e.g. <c>=&gt;</c> rendered as a single
    /// shaped glyph) and full Unicode shaping.  
    /// </summary>
    public bool UseFastTextLayout { get; set; } = true;

    /// <summary>
    /// Use a brighter, more visible selection highlight color instead of the
    /// default subtle tint. Useful on monitors with limited contrast.
    /// </summary>
    public bool BrightSelection { get; set; }

    /// <summary>
    /// Wrap long lines at the viewport edge. When false, lines extend beyond
    /// the visible area (horizontal scrolling planned for a future release).
    /// </summary>
    public bool WrapLines { get; set; } = true;

    /// <summary>
    /// When true, lines wrap at the <see cref="WrapLinesAt"/> column limit
    /// (or the viewport edge, whichever is narrower).  When false, lines
    /// wrap at the viewport edge only.
    /// </summary>
    public bool UseWrapColumn { get; set; } = true;

    /// <summary>
    /// Maximum number of columns before a line wraps. Only has effect when
    /// both <see cref="WrapLines"/> and <see cref="UseWrapColumn"/> are true.
    /// </summary>
    public int WrapLinesAt { get; set; } = 100;

    /// <summary>
    /// Number of spaces per indent level. Also controls the visual width of
    /// tab characters. 
    /// </summary>
    public int IndentWidth { get; set; } = 4;

    /// <summary>
    /// File size threshold (in KB) — together with <see cref="CharWrapLineLength"/>,
    /// gates the automatic switch to character-wrapping mode.  Both conditions
    /// must be met (file ≥ this size AND any line ≥ <see cref="CharWrapLineLength"/>)
    /// before CharWrap activates.  The size gate avoids penalizing small files
    /// that happen to contain a single long line, because those are still cheap to
    /// measure. 
    /// </summary>
    public int CharWrapFileSizeKB { get; set; } = 50;

    /// <summary>
    /// Line length threshold (in characters) — together with <see cref="CharWrapFileSizeKB"/>,
    /// gates the automatic switch to character-wrapping mode.  When any line in
    /// a sufficiently large file reaches this length, the editor switches to
    /// the fixed-width grid renderer because long lines can't be measured
    /// quickly and aren't practical to edit.  Lower values trigger CharWrap
    /// mode more aggressively.
    /// </summary>
    public int CharWrapLineLength { get; set; } = 2000;

    /// <summary>
    /// When pasting into a column-mode multi-cursor selection: if true,
    /// distribute the clipboard's lines across the cursors (line[i] → caret[i])
    /// when the line count matches the cursor count.  When false, broadcast the
    /// entire clipboard at every cursor (matches VS Code, Sublime, Rider).
    /// Distribution drops excess clipboard lines and leaves excess carets
    /// untouched; multi-line broadcasts always exit column mode afterward.
    /// </summary>
    public bool DistributeColumnPaste { get; set; } = true;

    /// <summary>
    /// Default indentation style for new/untitled documents. Files opened from
    /// disk use the detected style instead. 
    /// </summary>
    public IndentStyle DefaultIndentStyle { get; set; } = IndentStyle.Spaces;

    /// <summary>
    /// When true, files modified on disk are automatically reloaded if the
    /// tab has no unsaved edits. Dirty tabs still show the conflict icon.
    /// </summary>
    public bool AutoReloadExternalChanges { get; set; }

    /// <summary>
    /// When true, the previous version of a file is kept as a .bak file
    /// when saving. 
    /// </summary>
    public bool BackupOnSave { get; set; }

    /// <summary>
    /// When true and a file is reloaded from disk while the editor is
    /// scrolled to the bottom, the scroll position is moved to the new
    /// end of the document so new content is visible. Only applies when
    /// the tab has no unsaved edits. 
    /// </summary>
    public bool TailFile { get; set; }

    /// <summary>
    /// Minimum time in milliseconds between auto-reload completions.
    /// Prevents runaway reloads when an external process writes to a
    /// file faster than we can read it. Hidden setting (not in the
    /// Settings UI). 
    /// </summary>
    public int TailReloadCooldownMs { get; set; } = 500;

    // -----------------------------------------------------------------
    // Editor font
    // -----------------------------------------------------------------

    /// <summary>
    /// Width of the text caret in device-independent pixels.
    /// Allowed range: 1.0 – 2.5 in 0.5 increments. 
    /// </summary>
    public double CaretWidth { get; set; } = 1.0;

    /// <summary>
    /// Editor font family name. Null means use the built-in default
    /// (Cascadia Code with Consolas / Courier New fallbacks).
    /// </summary>
    public string? EditorFontFamily { get; set; }

    /// <summary>
    /// Editor font size in typographic points. 
    /// </summary>
    public int EditorFontSize { get; set; } = 11;

    /// <summary>
    /// Zoom percentage for the editor. 100 = no zoom. Range: 10–800.
    /// Persisted across restarts. Independent of font size.
    /// </summary>
    public int ZoomPercent { get; set; } = 100;

    /// <summary>
    /// Custom sample text for the font preview. Null uses the built-in default.
    /// </summary>
    public string? FontPreviewText { get; set; }

    // -----------------------------------------------------------------
    // Theme
    // -----------------------------------------------------------------

    /// <summary>
    /// Controls the color theme. 
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
    /// keystroke, so only actual pauses trigger a flush. 
    /// </summary>
    public int CoalesceTimerMs { get; set; } = 1000;

    /// <summary>
    /// Controls the hierarchy of levels used by Expand Selection.
    /// <see cref="ExpandSelectionMode.SubwordFirst"/> starts with camelCase/underscore
    /// boundaries; <see cref="ExpandSelectionMode.Word"/> starts with whitespace boundaries.
    /// </summary>
    public ExpandSelectionMode ExpandSelectionMode { get; set; } = ExpandSelectionMode.Word;

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
    /// Maximum assumed regex match length for chunked search overlap.
    /// Increase if Replace All misses very long regex matches near chunk
    /// boundaries. 
    /// </summary>
    public int MaxRegexMatchLength { get; set; } = 1024;

    /// <summary>Last search mode: Normal, Wildcard, or Regex.</summary>
    public SearchMode FindSearchMode { get; set; } = SearchMode.Normal;

    /// <summary>Last state of the Match Case toggle.</summary>
    public bool FindMatchCase { get; set; }

    /// <summary>Last state of the Whole Word toggle.</summary>
    public bool FindWholeWord { get; set; }

    /// <summary>Last state of the Preserve Case toggle.</summary>
    public bool FindPreserveCase { get; set; }

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

    /// <summary>
    /// Linux-only: which file picker backend to use for Open/Save/Export
    /// dialogs.  <see cref="LinuxFilePickerMode.Auto"/> probes
    /// xdg-desktop-portal at startup and falls back to zenity when it looks
    /// broken.  Ignored on Windows and macOS.
    /// </summary>
    public LinuxFilePickerMode LinuxFilePicker { get; set; } = LinuxFilePickerMode.Auto;

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
    /// </summary>
    public int ChordTimeoutMs { get; set; } = 3000;

    // -----------------------------------------------------------------
    // Updates
    // -----------------------------------------------------------------

    /// <summary>
    /// When true, updates are downloaded silently on startup and a status
    /// bar indicator appears when ready. When false, the app still checks
    /// for updates but only shows a button in Settings to download manually.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    // -----------------------------------------------------------------
    // Clipboard ring
    // -----------------------------------------------------------------

    /// <summary>
    /// Maximum number of entries kept in the clipboard ring. Hidden setting
    /// (not shown in the Settings UI). 
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

    /// <summary>
    /// Hidden diagnostic toggle for the WPF print path.  When true (default),
    /// monospace rows are drawn via the <c>GlyphRun</c> fast path which
    /// dramatically outperforms <c>FormattedText</c>.  Turn off to fall back
    /// to the legacy <c>FormattedText</c> drawing path — useful only for
    /// diagnosing visual differences or ruling out a GlyphRun regression.
    /// Not exposed in the Settings UI; edit settings.json directly.
    /// </summary>
    public bool UseGlyphRunPrinting { get; set; } = true;

    // -----------------------------------------------------------------
    // Load / Save
    // -----------------------------------------------------------------

    /// <summary>
    /// True only for instances created by <see cref="Load"/> — i.e. the
    /// singleton backed by the on-disk file.  Instances created via
    /// <c>new AppSettings()</c> (including from tests) are transient and
    /// must never write to <see cref="StorePath"/>.
    /// </summary>
    private bool _persistent;

    /// <summary>
    /// Loads settings from disk. Returns defaults on any failure.
    /// The returned instance is marked <see cref="_persistent"/> so
    /// <see cref="Save"/> and <see cref="ScheduleSave"/> will write
    /// back to disk.  Instances created with <c>new AppSettings()</c>
    /// are <em>not</em> persistent and their Save/ScheduleSave are no-ops.
    /// </summary>
    public static AppSettings Load() {
        AppSettings settings;
        try {
            if (File.Exists(StorePath)) {
                var json = File.ReadAllText(StorePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            } else {
                settings = new AppSettings();
            }
        } catch (Exception ex) {
            // Corrupted or unreadable — use defaults.
            System.Diagnostics.Debug.WriteLine($"AppSettings.Load failed: {ex.Message}");
            settings = new AppSettings();
        }

        settings._persistent = true;

        // DevMode is only allowed in Debug builds or when the DMEDIT_DEVMODE
        // env var is "true".  Otherwise the persisted value is forced off so
        // release builds shipped to end users can't accidentally expose the
        // Dev menu, sample documents, stats bar, etc.
        if (!DevModeAllowed) {
            settings.DevMode = false;
        }

        return settings;
    }

    /// <summary>
    /// Whether DevMode is permitted to be enabled in this process.  True for
    /// Debug builds unconditionally, and for Release builds only when the
    /// <c>DMEDIT_DEVMODE</c> environment variable is set to <c>"true"</c>.
    /// The actual <see cref="DevMode"/> boolean is a separate, user-toggleable
    /// setting that only takes effect when this gate is open.
    /// </summary>
    public static bool DevModeAllowed {
        get {
#if DEBUG
            return true;
#else
            return string.Equals(
                Environment.GetEnvironmentVariable("DMEDIT_DEVMODE"),
                "true", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }

    /// <summary>
    /// Persists settings to disk. Only writes if this instance was created
    /// by <see cref="Load"/> (i.e. is the disk-backed singleton).
    /// Instances created via <c>new AppSettings()</c> silently skip I/O
    /// so tests and transient objects never clobber the user's file.
    /// Failures are silently swallowed (best-effort).
    /// </summary>
    public void Save() {
        if (!_persistent) return;
        try {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOpts));
        } catch (Exception ex) {
            // Best-effort — non-fatal.
            System.Diagnostics.Debug.WriteLine($"AppSettings.Save failed: {ex.Message}");
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
