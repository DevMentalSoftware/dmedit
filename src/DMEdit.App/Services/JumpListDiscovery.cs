using System;
using System.Diagnostics;
using DMEdit.Core.JumpList;

namespace DMEdit.App.Services;

/// <summary>
/// Provides the Windows jump list service, or null on non-Windows platforms
/// or when the Windows Desktop runtime is not installed.
/// </summary>
public static class JumpListDiscovery {
    private static readonly Lazy<IJumpListService?> _instance = new(Discover);

    public static IJumpListService? Service => _instance.Value;

    private static IJumpListService? Discover() {
#if WINDOWS
        if (!WpfResolver.IsAvailable) {
            return null;
        }
        try {
            DMEdit.Windows.WindowsJumpListService.SetAppUserModelId();
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
