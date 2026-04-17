using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DMEdit.App.Services;

/// <summary>
/// User preference for which file picker DMEdit uses on Linux.
/// </summary>
public enum LinuxFilePickerMode {
    /// <summary>
    /// Probe xdg-desktop-portal at startup; use the portal picker if healthy,
    /// fall back to zenity otherwise.  Recommended for most users.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always use Avalonia's xdg-desktop-portal picker.  Useful for
    /// debugging the probe or when the probe produces false negatives.
    /// A future <c>Native</c> option could go directly to GTK via P/Invoke,
    /// which is why this value is named for the DBus protocol it uses
    /// rather than "Native".
    /// </summary>
    XdgPortal = 1,

    /// <summary>
    /// Always use zenity (GTK-based).  Escape hatch for users whose portal
    /// is installed and appears healthy but misbehaves in practice.
    /// </summary>
    Zenity = 2,

    /// <summary>
    /// Always use kdialog (Qt-based).  Requires the <c>kdialog</c> package;
    /// if it isn't installed the resolver falls back to the portal picker.
    /// Gives a Qt/KDE-styled dialog — useful on KDE or for users who prefer
    /// the look over zenity.
    /// </summary>
    KDialog = 3,
}

/// <summary>
/// Which picker DMEdit should actually use for the current call.
/// Distinct from <see cref="LinuxFilePickerMode"/>: that is a user preference,
/// this is the resolved decision.  A future <c>Native</c> choice could route
/// directly to GTK via P/Invoke.
/// </summary>
public enum FilePickerChoice {
    XdgPortal,
    Zenity,
    KDialog,
}

/// <summary>
/// Resolves the effective file picker on Linux given the user's preference,
/// the portal probe result, and whether zenity is available.
/// </summary>
public static class LinuxFilePickerResolver {
    private static readonly Lazy<bool> ZenityAvailable = new(() => WhichExists("zenity"));
    private static readonly Lazy<bool> KDialogAvailable = new(() => WhichExists("kdialog"));

    public static bool IsZenityAvailable => ZenityAvailable.Value;
    public static bool IsKDialogAvailable => KDialogAvailable.Value;

    /// <summary>
    /// Resolves which picker to use on Linux.  On non-Linux platforms this
    /// returns <see cref="FilePickerChoice.XdgPortal"/> as a sentinel meaning
    /// "use Avalonia's StorageProvider" — callers should short-circuit
    /// before invoking on non-Linux.
    /// </summary>
    public static FilePickerChoice Resolve(LinuxFilePickerMode mode) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return FilePickerChoice.XdgPortal;
        }

        return mode switch {
            LinuxFilePickerMode.XdgPortal => FilePickerChoice.XdgPortal,
            LinuxFilePickerMode.Zenity =>
                IsZenityAvailable ? FilePickerChoice.Zenity : FilePickerChoice.XdgPortal,
            LinuxFilePickerMode.KDialog =>
                IsKDialogAvailable ? FilePickerChoice.KDialog : FilePickerChoice.XdgPortal,
            _ /* Auto */ =>
                LinuxPortalProbe.Result.IsHealthy || !IsZenityAvailable
                    ? FilePickerChoice.XdgPortal
                    : FilePickerChoice.Zenity,
        };
    }

    /// <summary>
    /// Human-readable explanation of the current resolution, used in the
    /// Settings UI so users understand why a particular picker is in use.
    /// </summary>
    public static string DescribeCurrent(LinuxFilePickerMode mode) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return "Not Linux; platform picker always used.";
        }
        var choice = Resolve(mode);
        var zen = IsZenityAvailable ? "installed" : "not installed";
        var kd = IsKDialogAvailable ? "installed" : "not installed";
        var probe = LinuxPortalProbe.Result;
        var probeText = probe.IsHealthy ? "healthy" : $"unavailable ({probe.Diagnostic})";
        return mode switch {
            LinuxFilePickerMode.XdgPortal =>
                $"Forced: xdg-desktop-portal picker. (Portal probe: {probeText}; zenity {zen}; kdialog {kd}.)",
            LinuxFilePickerMode.Zenity =>
                choice == FilePickerChoice.Zenity
                    ? $"Forced: zenity. (Portal probe: {probeText}.)"
                    : $"Zenity requested but {zen}; falling back to xdg-desktop-portal. (Portal probe: {probeText}.)",
            LinuxFilePickerMode.KDialog =>
                choice == FilePickerChoice.KDialog
                    ? $"Forced: kdialog. (Portal probe: {probeText}.)"
                    : $"KDialog requested but {kd}; falling back to xdg-desktop-portal. (Portal probe: {probeText}.)",
            _ =>
                choice == FilePickerChoice.XdgPortal
                    ? $"Auto: using xdg-desktop-portal picker. (Portal probe: {probeText}; zenity {zen}; kdialog {kd}.)"
                    : $"Auto: using zenity because portal probe failed ({probe.Diagnostic}).",
        };
    }

    private static bool WhichExists(string tool) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = "which",
                Arguments = tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            if (!proc.WaitForExit(2000)) {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        } catch {
            return false;
        }
    }
}
