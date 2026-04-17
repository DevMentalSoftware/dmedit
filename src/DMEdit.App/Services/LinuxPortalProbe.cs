using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DMEdit.App.Services;

/// <summary>
/// Runtime health check for the Linux xdg-desktop-portal stack.
/// Avalonia's Linux file picker routes through
/// <c>org.freedesktop.portal.FileChooser</c> on DBus.  If the portal is
/// installed but the session bus name has no owner, or the backend has
/// crashed, Avalonia's <c>OpenFilePickerAsync</c> silently returns zero
/// files — indistinguishable from a user cancel.
/// This probe detects that case so we can fall back to zenity.
/// </summary>
public static class LinuxPortalProbe {
    private static readonly Lazy<ProbeResult> Cached = new(RunProbe);

    public static ProbeResult Result => Cached.Value;

    public readonly record struct ProbeResult(bool IsHealthy, string Diagnostic);

    private static ProbeResult RunProbe() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return new ProbeResult(true, "Not Linux; portal probe skipped.");
        }

        // gdbus is part of glib-bin and is typically present wherever portals
        // are set up.  busctl (systemd) is a reasonable secondary option.
        if (TryGdbus(out var gdbusResult)) return gdbusResult;
        if (TryBusctl(out var busctlResult)) return busctlResult;

        return new ProbeResult(
            IsHealthy: false,
            Diagnostic: "Neither gdbus nor busctl is available; cannot verify portal health.");
    }

    private static bool TryGdbus(out ProbeResult result) {
        var psi = new ProcessStartInfo("gdbus",
            "call --session " +
            "--dest org.freedesktop.DBus " +
            "--object-path /org/freedesktop/DBus " +
            "--method org.freedesktop.DBus.GetNameOwner " +
            "org.freedesktop.portal.Desktop") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return TryRunProbe(psi, "gdbus", out result);
    }

    private static bool TryBusctl(out ProbeResult result) {
        var psi = new ProcessStartInfo("busctl",
            "--user get-property " +
            "org.freedesktop.portal.Desktop " +
            "/org/freedesktop/portal/desktop " +
            "org.freedesktop.DBus.Peer") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return TryRunProbe(psi, "busctl", out result);
    }

    private static bool TryRunProbe(ProcessStartInfo psi, string tool, out ProbeResult result) {
        try {
            using var proc = Process.Start(psi);
            if (proc is null) {
                result = default;
                return false;
            }
            if (!proc.WaitForExit(2000)) {
                try { proc.Kill(); } catch { }
                result = new ProbeResult(false, $"{tool} probe timed out after 2s.");
                return true;
            }
            if (proc.ExitCode == 0) {
                result = new ProbeResult(true, $"Portal reachable via {tool}.");
                return true;
            }
            var err = proc.StandardError.ReadToEnd().Trim();
            if (err.Length > 200) err = err[..200] + "...";
            result = new ProbeResult(
                IsHealthy: false,
                Diagnostic: $"{tool} exit {proc.ExitCode}: {(err.Length == 0 ? "(no stderr)" : err)}");
            return true;
        } catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            // Tool not installed — caller falls through to the next tool.
            result = default;
            return false;
        } catch (Exception ex) {
            result = new ProbeResult(false, $"{tool} probe threw: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }
}
