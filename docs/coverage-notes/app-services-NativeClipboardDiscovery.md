# `NativeClipboardDiscovery`

`src/DMEdit.App/Services/NativeClipboardDiscovery.cs` (63 lines)

Static discovery for `INativeClipboardService` on the current
platform. Windows: reflection-loads `DMEdit.Windows.dll`. Linux:
uses `LinuxClipboardService`.

## Likely untested

- **Windows DLL missing** — returns null, app falls back to
  Avalonia clipboard. Untested.
- **Windows DLL present but missing type** — null return.
- **Windows DLL reflection exception** — caught, null return.
- **Linux detection** — `LinuxClipboardService.TryCreate` may
  return null if no tool is available.
- **Unsupported platform (macOS)** — returns null.
- **`Lazy<T>` concurrency** — standard, not asserted.

## Architectural concerns

- **Reflection-based loading** with `Assembly.LoadFrom` —
  picks up the wrong version if a DLL is in a parent dir.
  Mitigated by `AppContext.BaseDirectory`.
- **`WindowsPrintService.IsAvailable` touch at line 35**
  — side effect to register an assembly resolver. Fragile
  coupling. A dedicated `WindowsAssemblyResolver` call
  would be clearer.
- **Silent failures logged to `Debug.WriteLine`** — hidden
  in release builds. A startup log file would help.
