using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;

namespace DMEdit.App.Services;

/// <summary>
/// JSON payload for submitting feedback to the Azure Function backend.
/// Field names use camelCase to match the API contract.
/// </summary>
public sealed record FeedbackPayload {
    [JsonPropertyName("type")]
    public string Type { get; init; } = "feedback";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = "";

    [JsonPropertyName("os")]
    public string Os { get; init; } = "";

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; init; } = "";

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = "";

    [JsonPropertyName("locale")]
    public string Locale { get; init; } = "";

    [JsonPropertyName("screenResolution")]
    public string ScreenResolution { get; init; } = "";

    [JsonPropertyName("scaleFactor")]
    public string ScaleFactor { get; init; } = "";

    [JsonPropertyName("memoryUsage")]
    public string MemoryUsage { get; init; } = "";

    [JsonPropertyName("uptime")]
    public string Uptime { get; init; } = "";

    [JsonPropertyName("crashReport")]
    public string? CrashReport { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Returns a compact multi-line markdown summary suitable for embedding
    /// in a GitHub issue body.
    /// </summary>
    public string ToMarkdownFooter() =>
        $"""
        | Field | Value |
        |-------|-------|
        | Version | {AppVersion} |
        | OS | {Os} {OsVersion} |
        | Runtime | {Runtime} |
        | Locale | {Locale} |
        | Screen | {ScreenResolution} @ {ScaleFactor}x |
        | Memory | {MemoryUsage} |
        | Uptime | {Uptime} |
        """;
}

/// <summary>
/// Collects system metadata for feedback submissions.
/// </summary>
public static class SystemInfoCollector {
    /// <summary>
    /// Builds a <see cref="FeedbackPayload"/> pre-populated with all available
    /// system metadata. Caller fills in <c>Type</c>, <c>Message</c>, and
    /// optionally <c>CrashReport</c>.
    /// </summary>
    public static FeedbackPayload Collect(Window? ownerWindow = null) {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        var (osName, osVer) = ParseOs();
        var runtime = ParseRuntime();
        var (resolution, scale) = GetScreenInfo(ownerWindow);
        var memory = FormatMemory();
        var uptime = FormatUptime();

        return new FeedbackPayload {
            AppVersion = version,
            Os = osName,
            OsVersion = osVer,
            Runtime = runtime,
            Locale = CultureInfo.CurrentCulture.Name,
            ScreenResolution = resolution,
            ScaleFactor = scale,
            MemoryUsage = memory,
            Uptime = uptime,
        };
    }

    private static (string Name, string Version) ParseOs() {
        var desc = RuntimeInformation.OSDescription; // e.g. "Microsoft Windows 10.0.22631"
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            // Extract version number from the end.
            var parts = desc.Split(' ');
            var ver = parts.Length > 0 ? parts[^1] : "";
            return ("Windows", ver);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            var parts = desc.Split(' ');
            var ver = parts.Length > 0 ? parts[^1] : "";
            return ("macOS", ver);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return ("Linux", desc);
        }
        return ("Unknown", desc);
    }

    private static string ParseRuntime() {
        // RuntimeInformation.FrameworkDescription returns e.g. ".NET 10.0.0"
        var desc = RuntimeInformation.FrameworkDescription;
        var prefix = ".NET ";
        return desc.StartsWith(prefix) ? desc[prefix.Length..] : desc;
    }

    private static (string Resolution, string Scale) GetScreenInfo(Window? window) {
        try {
            var screen = window?.Screens.Primary ?? window?.Screens.All.FirstOrDefault();
            if (screen is not null) {
                var bounds = screen.Bounds;
                var resolution = $"{bounds.Width}x{bounds.Height}";
                var scale = screen.Scaling.ToString("F1", CultureInfo.InvariantCulture);
                return (resolution, scale);
            }
        } catch {
            // Screen info unavailable (headless, etc.)
        }
        return ("unknown", "unknown");
    }

    private static string FormatMemory() {
        var bytes = Process.GetCurrentProcess().WorkingSet64;
        var mb = bytes / (1024.0 * 1024.0);
        return $"{mb:F0} MB";
    }

    private static string FormatUptime() {
        try {
            var start = Process.GetCurrentProcess().StartTime;
            var elapsed = DateTime.Now - start;
            return elapsed.ToString(@"hh\:mm\:ss");
        } catch {
            return "unknown";
        }
    }
}
