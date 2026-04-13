using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DMEdit.Core.Clipboard;

namespace DMEdit.App.Services;

/// <summary>
/// Discovers the platform-specific <see cref="INativeClipboardService"/>.
/// On Windows: uses DMEdit.Windows.NativeClipboardService.
/// On Linux: uses process-based <see cref="LinuxClipboardService"/>.
/// </summary>
public static class NativeClipboardDiscovery {
    private static readonly Lazy<INativeClipboardService?> Instance = new(Discover);

    public static INativeClipboardService? Service => Instance.Value;

    private static INativeClipboardService? Discover() {
#if WINDOWS
        try {
            return new DMEdit.Windows.NativeClipboardService();
        } catch (Exception ex) {
            Debug.WriteLine($"NativeClipboard: Failed to load: {ex}");
            return null;
        }
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return LinuxClipboardService.TryCreate();
        }
        return null;
#endif
    }
}
