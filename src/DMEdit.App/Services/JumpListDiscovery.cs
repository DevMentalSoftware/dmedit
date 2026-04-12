using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DMEdit.Core.JumpList;

namespace DMEdit.App.Services;

/// <summary>
/// Discovers the Windows jump list service from DMEdit.Windows.dll.
/// Returns null on non-Windows platforms or if the DLL is not present.
/// </summary>
public static class JumpListDiscovery {
    private static readonly Lazy<IJumpListService?> _instance = new(Discover);

    public static IJumpListService? Service => _instance.Value;

    private static IJumpListService? Discover() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return null;
        }

        try {
            // Ensure the WPF assembly resolver is registered first.
            _ = WindowsPrintService.IsAvailable;

            var dir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(dir, "DMEdit.Windows.dll");
            if (!File.Exists(dllPath)) {
                Debug.WriteLine($"JumpList: DLL not found at {dllPath}");
                return null;
            }

            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType("DMEdit.Windows.WindowsJumpListService");
            if (type == null) {
                Debug.WriteLine("JumpList: WindowsJumpListService type not found.");
                return null;
            }

            var instance = Activator.CreateInstance(type) as IJumpListService;
            if (instance != null) {
                Debug.WriteLine("JumpList: Windows jump list service loaded.");
            }
            return instance;
        } catch (Exception ex) {
            Debug.WriteLine($"JumpList: Failed to load: {ex}");
            return null;
        }
    }
}
