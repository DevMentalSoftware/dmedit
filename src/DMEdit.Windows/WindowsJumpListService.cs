using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Shell;
using DMEdit.Core.JumpList;

namespace DMEdit.Windows;

/// <summary>
/// Manages the Windows taskbar jump list using WPF's
/// <see cref="System.Windows.Shell.JumpList"/> API.
/// Each recent file appears as a <see cref="JumpTask"/> that launches
/// DMEdit with the file path as an argument.
/// All calls are best-effort — failures are silently ignored.
/// </summary>
public class WindowsJumpListService : IJumpListService {
    [DllImport("shell32.dll")]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>
    /// Sets the process-wide AppUserModelID so Windows can associate our
    /// taskbar button with our jump list.  Must be called before any
    /// window is shown.  Safe to call multiple times.
    /// </summary>
    public static void SetAppUserModelId() {
        try {
            SetCurrentProcessExplicitAppUserModelID("DMEdit");
        } catch {
            // Best-effort — failure here just means jump lists won't work.
        }
    }

    public void UpdateRecentFiles(IReadOnlyList<string> paths, string appExePath) {
        if (paths.Count == 0) {
            Clear();
            return;
        }

        try {
            var jumpList = new JumpList();
            foreach (var path in paths) {
                jumpList.JumpItems.Add(new JumpTask {
                    ApplicationPath = appExePath,
                    Arguments = $"\"{path}\"",
                    Title = Path.GetFileName(path),
                    Description = path,
                    IconResourcePath = appExePath,
                    IconResourceIndex = 0,
                    CustomCategory = "Recent"
                });
            }
            jumpList.Apply();
        } catch {
            // Best-effort — jump list failures are not fatal.
        }
    }

    public void Clear() {
        try {
            var jumpList = new JumpList();
            jumpList.Apply();
        } catch {
            // Best-effort.
        }
    }
}
