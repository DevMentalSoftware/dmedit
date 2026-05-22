using System;
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
    /// <summary>
    /// The AppUserModelID we set on the running process and stamp onto our
    /// taskbar/Start Menu shortcuts. Windows uses this to group taskbar
    /// buttons; if the shortcut's AUMID doesn't match the process's, the
    /// running window won't dock into the pinned button.
    /// </summary>
    public const string AppId = "DMEdit";

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
            SetCurrentProcessExplicitAppUserModelID(AppId);
        } catch {
            // Best-effort — failure here just means jump lists won't work.
        }
    }

    /// <summary>
    /// Stamps the System.AppUserModel.ID property onto every DMEdit .lnk
    /// we can find in the per-user Start Menu and pinned-taskbar folders
    /// whose target points at <paramref name="targetExe"/>. Pinned shortcuts
    /// without this property cause Windows to open a second taskbar button
    /// when the app launches, because the running process's explicit AUMID
    /// no longer matches the shortcut's (empty) AUMID.
    /// Idempotent: only writes when the existing value differs.
    /// </summary>
    public static void EnsureKnownShortcutsHaveAumid(string targetExe) {
        try {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] roots = [
                Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs"),
                Path.Combine(appData, "Microsoft", "Internet Explorer",
                    "Quick Launch", "User Pinned", "TaskBar"),
            ];
            foreach (var root in roots) {
                if (!Directory.Exists(root)) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)) {
                    if (!ShortcutTargets(lnk, targetExe)) continue;
                    if (ReadShortcutAumid(lnk) == AppId) continue;
                    StampShortcutAumid(lnk, AppId);
                }
            }
        } catch {
            // Best-effort — failure here just means the user may need to
            // unpin/re-pin to get a single taskbar button.
        }
    }

    private static bool ShortcutTargets(string lnkPath, string targetExe) {
        try {
            // Late-bind WScript.Shell so this file doesn't need a COM ref.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try {
                dynamic sc = shell.CreateShortcut(lnkPath);
                string? target = sc.TargetPath as string;
                return target is not null
                    && string.Equals(Path.GetFullPath(target),
                        Path.GetFullPath(targetExe),
                        StringComparison.OrdinalIgnoreCase);
            } finally {
                Marshal.FinalReleaseComObject(shell);
            }
        } catch {
            return false;
        }
    }

    private static string? ReadShortcutAumid(string lnkPath) {
        IPropertyStore? store = null;
        try {
            var iid = typeof(IPropertyStore).GUID;
            ShellNative.SHGetPropertyStoreFromParsingName(
                lnkPath, IntPtr.Zero, ShellNative.GPS_DEFAULT, ref iid, out store);
            var key = ShellNative.PKEY_AppUserModel_ID;
            store.GetValue(ref key, out var pv);
            try {
                if (pv.vt == ShellNative.VT_LPWSTR && pv.pwszVal != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pv.pwszVal);
                return null;
            } finally {
                ShellNative.PropVariantClear(ref pv);
            }
        } catch {
            return null;
        } finally {
            if (store is not null) Marshal.FinalReleaseComObject(store);
        }
    }

    private static void StampShortcutAumid(string lnkPath, string aumid) {
        IPropertyStore? store = null;
        var strPtr = IntPtr.Zero;
        try {
            var iid = typeof(IPropertyStore).GUID;
            ShellNative.SHGetPropertyStoreFromParsingName(
                lnkPath, IntPtr.Zero, ShellNative.GPS_READWRITE, ref iid, out store);
            strPtr = Marshal.StringToCoTaskMemUni(aumid);
            var pv = new ShellNative.PROPVARIANT {
                vt = ShellNative.VT_LPWSTR,
                pwszVal = strPtr,
            };
            var key = ShellNative.PKEY_AppUserModel_ID;
            store.SetValue(ref key, ref pv);
            store.Commit();
        } catch {
            // Best-effort.
        } finally {
            if (strPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(strPtr);
            if (store is not null) Marshal.FinalReleaseComObject(store);
        }
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out ShellNative.PROPERTYKEY pkey);
        void GetValue(ref ShellNative.PROPERTYKEY key, out ShellNative.PROPVARIANT pv);
        void SetValue(ref ShellNative.PROPERTYKEY key, ref ShellNative.PROPVARIANT pv);
        void Commit();
    }

    private static class ShellNative {
        public const int GPS_DEFAULT = 0;
        public const int GPS_READWRITE = 2;
        public const ushort VT_LPWSTR = 31;

        public static PROPERTYKEY PKEY_AppUserModel_ID = new() {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5,
        };

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct PROPVARIANT {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr pbc,
            int flags,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);
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
