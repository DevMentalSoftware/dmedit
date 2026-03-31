using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DMEdit.App.Services;

/// <summary>
/// Fallback file-open / file-save dialogs for Linux using <c>zenity</c>.
/// Used when Avalonia's <c>StorageProvider</c> is the stub
/// <c>FallbackStorageProvider</c> (no DBus portal or GTK backend available).
/// </summary>
public static class LinuxFileDialog {
    /// <summary>
    /// Shows a zenity open-file dialog.
    /// Returns the selected path, or <c>null</c> if the user cancelled or zenity is unavailable.
    /// </summary>
    public static async Task<string?> OpenAsync(string title, string? startDir = null) {
        var args = "--file-selection " +
            $"--title={Quote(title)}";
        if (startDir is not null && Directory.Exists(startDir)) {
            // Trailing slash tells zenity to treat it as a directory, not a filename.
            args += $" --filename={Quote(startDir.TrimEnd('/') + "/")}";
        }
        return await RunZenityAsync(args);
    }

    /// <summary>
    /// Shows a zenity save-file dialog with a suggested filename.
    /// Returns the selected path, or <c>null</c> if the user cancelled or zenity is unavailable.
    /// </summary>
    public static async Task<string?> SaveAsync(string title, string suggestedName, string? startDir = null) {
        string filenameArg;
        if (startDir is not null && Directory.Exists(startDir)) {
            // Combine directory + suggested name so zenity opens in the right folder.
            filenameArg = Quote(Path.Combine(startDir, suggestedName));
        } else {
            filenameArg = Quote(suggestedName);
        }
        return await RunZenityAsync(
            "--file-selection --save --confirm-overwrite " +
            $"--title={Quote(title)} " +
            $"--filename={filenameArg}");
    }

    private static async Task<string?> RunZenityAsync(string args) {
        try {
            var psi = new ProcessStartInfo("zenity", args) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc is null) {
                return null;
            }
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output.Trim() : null;
        } catch {
            return null;
        }
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
