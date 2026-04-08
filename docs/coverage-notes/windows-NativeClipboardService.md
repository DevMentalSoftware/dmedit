# `NativeClipboardService` (Windows)

`src/DMEdit.Windows/NativeClipboardService.cs` (196 lines)
Tests: none direct (Windows-only P/Invoke).

Win32 clipboard access via `OpenClipboard`/`GlobalAlloc`/
`SetClipboardData` (copy) and `GetClipboardData`/`GlobalLock`
(paste). Zero managed-string-allocation by streaming directly
to/from HGLOBAL.

## Likely untested

- **Copy success path.**
- **`OpenClipboard` failure** — returns false.
- **`GlobalAlloc` failure** — returns false, no cleanup
  needed because SetClipboardData never called.
- **`SetClipboardData` failure** — GlobalFree is called to
  prevent leak. Correct.
- **Paste with empty clipboard** — `GetClipboardData` null.
- **Paste with non-text clipboard** — IsClipboardFormatAvailable
  check.
- **Null terminator search** when `GlobalSize` reports a
  buffer larger than the actual string.
- **Cancellation mid-paste** — chunked loop respects token.
- **Unlock on exception** — try/finally wraps `GlobalLock`/
  `GlobalUnlock`. Audit.
- **OpenClipboard contention** — multi-process race.
  Win32's behavior is to return false; no retry.

## Architectural concerns

- **`unsafe` blocks, pointer arithmetic** — standard for
  Win32 interop. Memory safety is on the author.
- **Implements `INativeClipboardService`** — good
  abstraction.
- **No `PasteToStream` override** — per journal, the
  stream-based path is used for large pastes. If not
  overridden here, default throws `NotSupportedException`.
  Check.

## Bugs / hazards

1. **`CloseClipboard` in finally blocks** — any path that
   took ownership via `SetClipboardData` must not
   `GlobalFree` (covered). Paths that didn't take ownership
   must free (also covered). Careful review necessary.
2. **Stream-based paste for very large clipboards** — 1M
   char chunk is fine; if not implemented, large pastes
   allocate unnecessarily.
