using System.IO;
using System.Runtime.InteropServices;
using DMEdit.Core.JumpList;

namespace DMEdit.Windows;

/// <summary>
/// Manages the Windows taskbar jump list using the COM
/// <c>ICustomDestinationList</c> API.  Each recent file appears as a
/// shell link that launches DMEdit with the file path as an argument.
/// All COM calls are best-effort — failures are silently ignored.
/// </summary>
public class WindowsJumpListService : IJumpListService {
    public void UpdateRecentFiles(IReadOnlyList<string> paths, string appExePath) {
        if (paths.Count == 0) {
            Clear();
            return;
        }

        try {
            var destList = (ICustomDestinationList)new CDestinationList();
            destList.BeginList(out _, typeof(IObjectArray).GUID, out _);

            var collection = (IObjectCollection)new CEnumerableObjectCollection();
            foreach (var path in paths) {
                var link = CreateShellLink(path, appExePath);
                if (link != null) {
                    collection.AddObject(link);
                }
            }

            destList.AppendCategory("Recent", (IObjectArray)collection);
            destList.CommitList();
        } catch {
            // Best-effort — COM failures are not fatal.
        }
    }

    public void Clear() {
        try {
            var destList = (ICustomDestinationList)new CDestinationList();
            destList.DeleteList(null!);
        } catch {
            // Best-effort.
        }
    }

    private static IShellLinkW? CreateShellLink(string filePath, string appExePath) {
        try {
            var link = (IShellLinkW)new CShellLink();
            link.SetPath(appExePath);
            link.SetArguments($"\"{filePath}\"");
            link.SetDescription(Path.GetFileName(filePath));
            link.SetIconLocation(appExePath, 0);

            // The shell link must have a Title property for it to appear
            // in a custom category.  Set it via IPropertyStore.
            if (link is IPropertyStore propStore) {
                var titleKey = new PropertyKey(
                    new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2); // System.Title
                var pv = new PropVariant(Path.GetFileName(filePath));
                propStore.SetValue(ref titleKey, ref pv);
                propStore.Commit();
                pv.Dispose();
            }

            return link;
        } catch {
            return null;
        }
    }

    // =====================================================================
    // COM interface declarations
    // =====================================================================

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    private class CDestinationList { }

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    private class CEnumerableObjectCollection { }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        void BeginList(
            out uint pcMinSlots,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IObjectArray ppv);

        void AppendCategory(
            [MarshalAs(UnmanagedType.LPWStr)] string pszCategory,
            [MarshalAs(UnmanagedType.Interface)] IObjectArray poa);

        void AppendKnownCategory(int category);

        void AddUserTasks(
            [MarshalAs(UnmanagedType.Interface)] IObjectArray poa);

        void DeleteList(
            [MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);

        void AbortList();
        void CommitList();
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray {
        // IObjectArray
        new void GetCount(out uint pcObjects);
        new void GetAt(uint uiIndex, [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName,
            int cch);
        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir,
            int cch);
        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs,
            int cch);
        void SetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue([In] ref PropertyKey key, out PropVariant pv);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable {
        private ushort _vt;
        private ushort _wReserved1;
        private ushort _wReserved2;
        private ushort _wReserved3;
        private IntPtr _ptr;

        /// <summary>Creates a VT_LPWSTR PropVariant.</summary>
        public PropVariant(string value) {
            _vt = 31; // VT_LPWSTR
            _ptr = Marshal.StringToCoTaskMemUni(value);
        }

        public void Dispose() {
            if (_ptr != IntPtr.Zero) {
                Marshal.FreeCoTaskMem(_ptr);
                _ptr = IntPtr.Zero;
            }
        }
    }
}
