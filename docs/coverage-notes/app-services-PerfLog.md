# `PerfLog`

`src/DMEdit.App/Services/PerfLog.cs` (38 lines)

Static file logger for perf investigations. Writes to
`%TEMP%/dmedit_perf.log`.

## Likely untested
- Not a production feature; no tests needed.

## Architectural concerns
- **Synchronous file I/O per Write** — OK for a few lines
  per insert, not OK for per-frame logging. Caller is
  responsible.
- **`_initialized` guard is not disposed** — first call
  truncates the file. On multi-process race (two editors
  running), interleaved writes to the same file. Not a
  correctness concern for a diagnostic tool.
- **No log rotation** — file grows without bound during a
  session. Diagnostic tool; acceptable.
