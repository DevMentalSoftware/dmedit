using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DMEdit.App.Services;

/// <summary>
/// Fallback file-open / file-save dialogs for Linux using <c>zenity</c>
/// (GTK) or <c>kdialog</c> (Qt/KDE).  The choice is made by
/// <see cref="LinuxFilePickerResolver"/> based on user preference and tool
/// availability, and passed in as a <see cref="FilePickerChoice"/>.
/// </summary>
public static class LinuxFileDialog {
    /// <summary>
    /// Shows an open-file dialog using the requested backend.
    /// Returns the selected path, or <c>null</c> if the user cancelled or
    /// the backend failed.  <paramref name="isDark"/> sets theme env vars
    /// on the spawned process so the dialog tracks dmedit's current theme.
    /// </summary>
    public static Task<string?> OpenAsync(FilePickerChoice choice, string title,
        string? startDir = null, bool isDark = false) {
        return choice == FilePickerChoice.KDialog
            ? RunKDialogOpenAsync(title, startDir, isDark)
            : RunZenityOpenAsync(title, startDir, isDark);
    }

    /// <summary>
    /// Shows a save-file dialog using the requested backend.
    /// Returns the selected path, or <c>null</c> if the user cancelled or
    /// the backend failed.
    /// </summary>
    public static Task<string?> SaveAsync(FilePickerChoice choice, string title,
        string suggestedName, string? startDir = null, bool isDark = false) {
        return choice == FilePickerChoice.KDialog
            ? RunKDialogSaveAsync(title, suggestedName, startDir, isDark)
            : RunZenitySaveAsync(title, suggestedName, startDir, isDark);
    }

    // -------------------------------------------------------------------------
    // Zenity
    // -------------------------------------------------------------------------

    private static Task<string?> RunZenityOpenAsync(string title, string? startDir, bool isDark) {
        var args = "--file-selection " +
            $"--title={Quote(title)}";
        if (startDir is not null && Directory.Exists(startDir)) {
            // Trailing slash tells zenity to treat it as a directory, not a filename.
            args += $" --filename={Quote(startDir.TrimEnd('/') + "/")}";
        }
        return RunProcessAsync("zenity", args, ApplyGtkTheme, isDark);
    }

    private static Task<string?> RunZenitySaveAsync(string title, string suggestedName,
        string? startDir, bool isDark) {
        string filenameArg;
        if (startDir is not null && Directory.Exists(startDir)) {
            // Combine directory + suggested name so zenity opens in the right folder.
            filenameArg = Quote(Path.Combine(startDir, suggestedName));
        } else {
            filenameArg = Quote(suggestedName);
        }
        var args = "--file-selection --save --confirm-overwrite " +
            $"--title={Quote(title)} " +
            $"--filename={filenameArg}";
        return RunProcessAsync("zenity", args, ApplyGtkTheme, isDark);
    }

    // -------------------------------------------------------------------------
    // kdialog (Qt)
    // -------------------------------------------------------------------------

    private static Task<string?> RunKDialogOpenAsync(string title, string? startDir, bool isDark) {
        // kdialog --getopenfilename [startDir] [filter] --title <title>
        var args = "--getopenfilename " +
            Quote(startDir is not null && Directory.Exists(startDir) ? startDir : "~") +
            $" --title={Quote(title)}";
        return RunProcessAsync("kdialog", args, ApplyQtTheme, isDark);
    }

    private static Task<string?> RunKDialogSaveAsync(string title, string suggestedName,
        string? startDir, bool isDark) {
        string startArg;
        if (startDir is not null && Directory.Exists(startDir)) {
            startArg = Path.Combine(startDir, suggestedName);
        } else {
            startArg = suggestedName;
        }
        var args = "--getsavefilename " +
            Quote(startArg) +
            $" --title={Quote(title)}";
        return RunProcessAsync("kdialog", args, ApplyQtTheme, isDark);
    }

    // -------------------------------------------------------------------------
    // Shared process runner + theme env
    // -------------------------------------------------------------------------

    private delegate void EnvApplier(ProcessStartInfo psi, bool isDark);

    private static async Task<string?> RunProcessAsync(string tool, string args,
        EnvApplier applyEnv, bool isDark) {
        try {
            var psi = new ProcessStartInfo(tool, args) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            applyEnv(psi, isDark);
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

    private static void ApplyGtkTheme(ProcessStartInfo psi, bool isDark) {
        // Adwaita ships with every GTK install; :dark is the standard
        // suffix for its dark variant.  Explicitly set the light form
        // too so a light-mode dmedit doesn't inherit a dark system theme.
        psi.Environment["GTK_THEME"] = isDark ? "Adwaita:dark" : "Adwaita";
    }

    private static void ApplyQtTheme(ProcessStartInfo psi, bool isDark) {
        // Qt doesn't have a universal "dark Adwaita" style the way GTK
        // does, so theming kdialog reliably needs a Qt color-scheme file
        // and Breeze/Kvantum bits that aren't guaranteed to exist on
        // non-KDE systems.  For now we only hint — users on KDE already
        // get their system theme; users elsewhere get whatever default
        // Qt picks.  A future improvement could write a tiny palette .ini
        // and point QT_STYLE_OVERRIDE at it.
        _ = isDark;
        _ = psi;
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
