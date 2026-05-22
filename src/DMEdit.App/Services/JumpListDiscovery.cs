using System;
using System.Diagnostics;
using DMEdit.Core.JumpList;

namespace DMEdit.App.Services;

/// <summary>
/// Provides the Windows jump list service, or null on non-Windows platforms.
/// WPF is available via the Windows Desktop runtime (declared in runtimeconfig.json).
/// </summary>
public static class JumpListDiscovery {
    private static readonly Lazy<IJumpListService?> _instance = new(Discover);

    public static IJumpListService? Service => _instance.Value;

    private static IJumpListService? Discover() {
#if WINDOWS
        try {
            DMEdit.Windows.WindowsJumpListService.SetAppUserModelId();
            // Stamp our AUMID onto any existing Start Menu / pinned-taskbar
            // shortcuts. Without this, Windows can't match the running
            // process to the pinned button and opens a second taskbar
            // group. Idempotent — no-ops once the property is in place.
            var exe = Environment.ProcessPath;
            if (exe is not null)
                DMEdit.Windows.WindowsJumpListService.EnsureKnownShortcutsHaveAumid(exe);
            return new DMEdit.Windows.WindowsJumpListService();
        } catch (Exception ex) {
            Debug.WriteLine($"JumpList: Failed to load: {ex}");
            return null;
        }
#else
        return null;
#endif
    }
}
