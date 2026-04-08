# `INativeClipboardService`

`src/DMEdit.Core/Clipboard/INativeClipboardService.cs` (44 lines)

Platform clipboard interface for zero-alloc copy/paste. Default
implementations on the interface stubs return `-1`/`throw`.

## Likely untested

- **The default `PasteToStream` throws `NotSupportedException`**
  — any implementor that forgets to override it will crash at
  runtime. Worth either making the method abstract or providing
  a real default that goes through `Paste(table)` + re-encode.
- **`GetClipboardCharCount` default returns `-1`** (unknown). A
  caller that treats `-1` as "no text" will silently skip large
  clipboards. Worth a comment making the semantics explicit.
- **`Paste(table)` → `Paste(table, null, default)`** — the
  overload calls itself through the full-signature method. No
  test verifies the default-parameter path.

## Architectural concerns
- **Default method implementations on an interface** are subtle
  — an implementor can skip overriding and get silently broken
  behavior. Consider an abstract base class instead.
- **Windows-only real implementation** (`NativeClipboardService`
  in `DMEdit.Windows`). Linux uses `LinuxClipboardService` which
  is a separate class (not via this interface). Two parallel
  abstractions. See the Linux notes.
- **No "has text" probe other than `GetClipboardCharCount`.**
  UI code that just wants to know "is paste enabled" has to
  either count or call Paste speculatively. Worth a
  `bool HasText { get; }`.
