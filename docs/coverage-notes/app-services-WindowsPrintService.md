# `WindowsPrintService`

`src/DMEdit.App/Services/WindowsPrintService.cs` (114 lines)

Discovery shim that loads the Windows-specific
`DMEdit.Print.Windows.dll` (a separate project) via reflection
and caches the `ISystemPrintService` instance. `IsAvailable`
triggers assembly-resolver registration.

## Likely untested

- **DLL missing** — returns null. Silent fallback to "no
  printers."
- **DLL present but type not found** — null.
- **WPF assembly-resolver registration side effects** —
  fragile; the comment in `NativeClipboardDiscovery` says
  "touch `IsAvailable` before loading the clipboard DLL."
  Couples two unrelated DLLs via a shared resolver.
- **Concurrent first-access race** — `Lazy<T>` handles.
- **Graceful degradation** — printing menu should disable
  if `IsAvailable` is false.

## Architectural concerns

- **Reflection loading is a maintenance burden.** Worth
  documenting why: it's to avoid linking WPF in non-Windows
  builds.
- **Assembly resolver side effect** is load-bearing for the
  clipboard DLL per the comment. That is a code smell;
  explicit dependency would be cleaner.
