using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DMEdit.App.Services;

/// <summary>
/// Registers an <see cref="AppDomain.AssemblyResolve"/> handler that locates
/// WPF framework assemblies (PresentationFramework, PresentationCore, etc.)
/// from the installed Windows Desktop runtime.
///
/// The app publishes framework-dependent for WPF — the base .NET runtime and
/// Avalonia are self-contained, but WPF assemblies are not bundled.  This
/// resolver finds them from the installed Desktop runtime at startup.
///
/// Call <see cref="Register"/> once in <c>Program.Main</c>, before any code
/// that touches WPF types (printing, jump lists).
/// </summary>
public static class WpfResolver {

    private static bool _registered;

    /// <summary>
    /// True when the resolver found the Windows Desktop runtime and WPF
    /// assemblies can be loaded.  Check this before attempting to use
    /// WPF-dependent services.
    /// </summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>
    /// Human-readable reason if the resolver could not find the Desktop
    /// runtime.  Null when <see cref="IsAvailable"/> is true.
    /// </summary>
    public static string? UnavailableReason { get; private set; }

    /// <summary>
    /// Finds the Windows Desktop runtime and registers an assembly resolver.
    /// Safe to call multiple times — only the first call has an effect.
    /// No-op on non-Windows.
    /// </summary>
    public static void Register() {
        if (_registered) {
            return;
        }
        _registered = true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            UnavailableReason = "Not running on Windows.";
            return;
        }

        var wpfDir = FindWpfRuntimeDir();
        if (wpfDir is null) {
            UnavailableReason = "Windows Desktop runtime not found. "
                + "Install the .NET Desktop Runtime to enable printing and jump lists.";
            Trace.WriteLine($"[WpfResolver] {UnavailableReason}");
            return;
        }

        IsAvailable = true;
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
            var name = new AssemblyName(args.Name).Name;
            if (name is null) {
                return null;
            }
            var candidate = Path.Combine(wpfDir, name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFile(candidate) : null;
        };
        Trace.WriteLine($"[WpfResolver] Registered — {wpfDir}");
    }

    /// <summary>
    /// Locates the Microsoft.WindowsDesktop.App shared framework directory
    /// by navigating from the current .NET runtime directory to its sibling.
    /// </summary>
    private static string? FindWpfRuntimeDir() {
        var coreDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sharedDir = Path.GetDirectoryName(Path.GetDirectoryName(coreDir));
        if (sharedDir is null) {
            return null;
        }

        var wpfBase = Path.Combine(sharedDir, "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(wpfBase)) {
            return null;
        }

        // Prefer the exact version matching the running runtime.
        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}.{Environment.Version.Build}";
        var exact = Path.Combine(wpfBase, version);
        if (Directory.Exists(exact)) {
            return exact;
        }

        // Fall back to the highest installed version with the same major.
        var majorPrefix = $"{Environment.Version.Major}.";
        return Directory.GetDirectories(wpfBase)
            .Where(d => Path.GetFileName(d)!.StartsWith(majorPrefix))
            .OrderDescending()
            .FirstOrDefault();
    }
}
