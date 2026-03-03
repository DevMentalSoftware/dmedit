using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevMentalMd.App.Services;

/// <summary>
/// Application settings persisted to <c>%APPDATA%/DevMentalMD/settings.json</c>.
/// Add new properties as the app evolves — unknown keys in the JSON are silently
/// ignored, and missing keys fall back to defaults.
/// </summary>
public sealed class AppSettings {
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevMentalMD", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
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

    // -----------------------------------------------------------------
    // Large file support
    // -----------------------------------------------------------------

    /// <summary>
    /// Files larger than this threshold (in bytes) use the paged buffer
    /// instead of loading entirely into memory. Default: 50 MB.
    /// </summary>
    public long PagedBufferThresholdBytes { get; set; } = 50L * 1024 * 1024;

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
}
