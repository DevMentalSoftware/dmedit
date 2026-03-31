using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DMEdit.Core.Printing;

namespace DMEdit.App.Services;

/// <summary>
/// Discovers and wraps the WPF-based <see cref="ISystemPrintService"/> from
/// the optional <c>DMEdit.Print.Windows.dll</c> assembly at runtime.
/// The assembly is only present on Windows builds; on Linux it is absent
/// and <see cref="IsAvailable"/> returns false.
/// </summary>
public static class WindowsPrintService {

    private static readonly Lazy<ISystemPrintService?> Instance = new(Discover);

    /// <summary>
    /// True when the WPF print assembly was found and loaded.
    /// Use this to conditionally show/enable the Print command.
    /// </summary>
    public static bool IsAvailable => Instance.Value is not null;

    /// <summary>
    /// Returns the platform print service, or null if unavailable.
    /// </summary>
    public static ISystemPrintService? Service => Instance.Value;

    private static ISystemPrintService? Discover() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return null;
        }
        try {
            var dir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(dir, "DMEdit.Print.Windows.dll");
            if (!File.Exists(dllPath)) {
                Debug.WriteLine($"Windows print DLL not found at: {dllPath}");
                return null;
            }

            // The app targets net10.0 (not net10.0-windows), so WPF framework
            // assemblies are not on the default probing path. Register a resolver
            // that finds them in the Windows Desktop runtime pack.
            var wpfDir = FindWpfRuntimeDir();
            if (wpfDir is null) {
                Debug.WriteLine("Windows Desktop runtime pack not found.");
                return null;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var name = new AssemblyName(args.Name).Name;
                if (name is null) return null;
                var candidate = Path.Combine(wpfDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFile(candidate) : null;
            };

            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType("DMEdit.Print.Windows.WpfPrintService");
            if (type is null) {
                Debug.WriteLine("WpfPrintService type not found in assembly.");
                return null;
            }

            var instance = Activator.CreateInstance(type) as ISystemPrintService;
            if (instance is null) {
                Debug.WriteLine("WpfPrintService does not implement ISystemPrintService.");
            }
            return instance;
        } catch (Exception ex) {
            Debug.WriteLine($"Failed to load Windows print assembly: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Locates the Microsoft.WindowsDesktop.App runtime directory by navigating
    /// from the current .NET runtime directory to the sibling Windows Desktop pack.
    /// </summary>
    private static string? FindWpfRuntimeDir() {
        var coreDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sharedDir = Path.GetDirectoryName(Path.GetDirectoryName(coreDir));
        if (sharedDir is null) return null;

        var wpfBase = Path.Combine(sharedDir, "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(wpfBase)) return null;

        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}.{Environment.Version.Build}";
        var exact = Path.Combine(wpfBase, version);
        if (Directory.Exists(exact)) return exact;

        var majorPrefix = $"{Environment.Version.Major}.";
        return Directory.GetDirectories(wpfBase)
            .Where(d => Path.GetFileName(d)!.StartsWith(majorPrefix))
            .OrderDescending()
            .FirstOrDefault();
    }
}
