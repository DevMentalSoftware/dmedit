using System;
using System.Diagnostics;
using DMEdit.Core.Printing;

namespace DMEdit.App.Services;

/// <summary>
/// Provides the WPF-based <see cref="ISystemPrintService"/> on Windows,
/// or null when the Windows Desktop runtime is not installed.
/// </summary>
public static class WindowsPrintService {

    private static readonly Lazy<ISystemPrintService?> Instance = new(Discover);

    /// <summary>
    /// True when the WPF print service is available.
    /// </summary>
    public static bool IsAvailable => Instance.Value is not null;

    /// <summary>
    /// Returns the platform print service, or null if unavailable.
    /// </summary>
    public static ISystemPrintService? Service => Instance.Value;

    /// <summary>
    /// If discovery failed, the reason — set once during the Lazy
    /// initialization and safe to read afterwards.
    /// </summary>
    public static string? DiscoveryError { get; private set; }

    private static ISystemPrintService? Discover() {
#if WINDOWS
        if (!WpfResolver.IsAvailable) {
            DiscoveryError = WpfResolver.UnavailableReason;
            return null;
        }
        try {
            return new DMEdit.Windows.WpfPrintService();
        } catch (Exception ex) {
            DiscoveryError = $"Failed to create WPF print service: {ex.Message}";
            Trace.WriteLine($"[WindowsPrintService] {DiscoveryError}");
            return null;
        }
#else
        DiscoveryError = "Not running on Windows.";
        return null;
#endif
    }
}
