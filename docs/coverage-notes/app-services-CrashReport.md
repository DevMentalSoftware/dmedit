# `CrashReport`

`src/DMEdit.App/Services/CrashReport.cs` (133 lines)

Writes crash report .txt files to `%LOCALAPPDATA%/DMEdit/session/`.
`WriteAsync` (with optional Document context) and synchronous
`Write`.

## Likely untested

- **`WriteAsync` failure path** — catches and returns null.
  Silent.
- **`Write` sync vs `WriteAsync`** — both essentially duplicate
  the header-building code.
- **Inner exception formatting** — branch at lines 62-75.
  Tested?
- **Filename collision** — two crashes in the same millisecond
  would produce the same filename. Low probability; the
  `fff` millisecond suffix may not be enough under rapid
  failure.
- **Very long stack trace** — File I/O will handle it; no
  truncation.
- **Document in an inconsistent state** (e.g. disposed
  PieceTable) — `doc?.Table.LineCount` may throw. Not
  guarded.
- **Temp file or permission errors** — swallowed.

## Architectural concerns

- **Two parallel `Write`/`WriteAsync` methods** with 80%
  duplicated content. Extract a `BuildReport` helper.
- **Lossy silent failures** — a crash report that fails to
  write leaves no trace. Worth writing to a secondary
  location (`%TEMP%`) if the primary fails.
- **Reporting format is free-form text.** A JSON format
  would let tooling consume it later.
- **No cap on number of crash files.** Long-running users
  accumulate infinite reports. Worth a "keep latest N"
  cleanup pass on startup.
