using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DMEdit.Core.Clipboard;

namespace DMEdit.App.Services;

/// <summary>
/// Discovers the platform-specific <see cref="INativeClipboardService"/>.
/// On Windows: loads from the optional DMEdit.Print.Windows.dll via reflection.
/// On Linux: uses process-based <see cref="LinuxClipboardService"/>.
/// </summary>
public static class NativeClipboardDiscovery {
    private static readonly Lazy<INativeClipboardService?> Instance = new(Discover);

    public static INativeClipboardService? Service => Instance.Value;

    private static INativeClipboardService? Discover() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return DiscoverWindows();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return LinuxClipboardService.TryCreate();
        }
        return null;
    }

    private static INativeClipboardService? DiscoverWindows() {
        try {
            // Ensure the WPF assembly resolver is registered before loading
            // the Windows DLL (it may need WPF transitive references even
            // though NativeClipboardService itself only uses P/Invoke).
            _ = WindowsPrintService.IsAvailable;

            var dir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(dir, "DMEdit.Windows.dll");
            if (!File.Exists(dllPath)) {
                Debug.WriteLine($"NativeClipboard: DLL not found at {dllPath}");
                return null;
            }

            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType("DMEdit.Windows.NativeClipboardService");
            if (type == null) {
                Debug.WriteLine("NativeClipboard: NativeClipboardService type not found in assembly.");
                return null;
            }

            var instance = Activator.CreateInstance(type) as INativeClipboardService;
            if (instance == null) {
                Debug.WriteLine("NativeClipboard: instance does not implement INativeClipboardService.");
            } else {
                Debug.WriteLine("NativeClipboard: Windows native clipboard service loaded.");
            }
            return instance;
        } catch (Exception ex) {
            Debug.WriteLine($"NativeClipboard: Failed to load: {ex}");
            return null;
        }
    }
}
