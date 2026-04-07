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

    /// <summary>
    /// If discovery failed, the reason — set once during the Lazy
    /// initialization and safe to read afterwards.  Callers can surface this
    /// in a status-bar message or error dialog when <see cref="IsAvailable"/>
    /// is false.  Null when discovery hasn't run yet or when it succeeded.
    /// </summary>
    public static string? DiscoveryError { get; private set; }

    private static ISystemPrintService? Discover() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return Fail("Not running on Windows.");
        }
        try {
            var dir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(dir, "DMEdit.Windows.dll");
            if (!File.Exists(dllPath)) {
                return Fail($"Windows print DLL not found at: {dllPath}");
            }

            // The app targets net10.0 (not net10.0-windows), so WPF framework
            // assemblies are not on the default probing path. Register a resolver
            // that finds them in the Windows Desktop runtime pack.
            var wpfDir = FindWpfRuntimeDir();
            if (wpfDir is null) {
                return Fail("Windows Desktop runtime pack not found.");
            }
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var name = new AssemblyName(args.Name).Name;
                if (name is null) return null;
                var candidate = Path.Combine(wpfDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFile(candidate) : null;
            };

            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType("DMEdit.Windows.WpfPrintService");
            if (type is null) {
                return Fail("WpfPrintService type not found in assembly.");
            }

            var rawInstance = Activator.CreateInstance(type);
            if (rawInstance is not ISystemPrintService instance) {
                // Most common cause: a stale DMEdit.Windows.dll built against
                // an older ISystemPrintService signature.  Surface a specific
                // hint because silent failure here has burned us before.
                return Fail(
                    $"WpfPrintService (type={rawInstance?.GetType().FullName ?? "null"}) " +
                    "does not implement the current ISystemPrintService interface. " +
                    "This usually means DMEdit.Windows.dll is stale — rebuild the solution.");
            }
            return instance;
        } catch (Exception ex) {
            return Fail($"Failed to load Windows print assembly: {ex}");
        }
    }

    private static ISystemPrintService? Fail(string reason) {
        DiscoveryError = reason;
        Trace.WriteLine($"[WindowsPrintService] {reason}");
        return null;
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
