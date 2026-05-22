$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class AumidReader {
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

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue([In] ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT pv);
        void Commit();
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

    public static string Read(string path) {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
        var key = new PROPERTYKEY {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };
        PROPVARIANT pv;
        store.GetValue(ref key, out pv);
        string val = (pv.vt == 31 || pv.vt == 8) ? Marshal.PtrToStringUni(pv.pwszVal) : null;
        PropVariantClear(ref pv);
        Marshal.ReleaseComObject(store);
        return val;
    }
}
"@

function Dump-Lnk($lnk) {
    Write-Host ""
    Write-Host "FILE: $lnk"
    try {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        Write-Host "  Target:    $($sc.TargetPath)"
        Write-Host "  Arguments: $($sc.Arguments)"
        Write-Host "  WorkDir:   $($sc.WorkingDirectory)"
    } catch {
        Write-Host "  (cannot read shortcut: $_)"
    }
    try {
        $aumid = [AumidReader]::Read($lnk)
        if ([string]::IsNullOrEmpty($aumid)) {
            Write-Host "  AUMID:     (none set on shortcut)"
        } else {
            Write-Host "  AUMID:     '$aumid'"
        }
    } catch {
        Write-Host "  AUMID:     (read failed: $($_.Exception.Message))"
    }
}

$pinDir = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
Write-Host "=== Pinned taskbar shortcuts ==="
Get-ChildItem -LiteralPath $pinDir -Filter '*.lnk' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'dmedit|dev.?mental' } |
    ForEach-Object { Dump-Lnk $_.FullName }

Write-Host ""
Write-Host "=== Start Menu shortcuts (current user) ==="
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
Get-ChildItem -LiteralPath $startMenu -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'dmedit|dev.?mental' } |
    ForEach-Object { Dump-Lnk $_.FullName }

Write-Host ""
Write-Host "=== Running dmedit processes ==="
Get-Process -Name dmedit -ErrorAction SilentlyContinue |
    Select-Object Id, Path | Format-Table -AutoSize
