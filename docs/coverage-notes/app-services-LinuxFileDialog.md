# `LinuxFileDialog`

`src/DMEdit.App/Services/LinuxFileDialog.cs` (66 lines)

Zenity-based Open/Save dialogs for Linux when Avalonia's
`StorageProvider` is the stub fallback.

## Likely untested

- **`OpenAsync` with null startDir** — no `--filename` arg.
- **`OpenAsync` with non-existent startDir** — passed arg?
  Source skips it (line 20).
- **`Quote` with paths containing spaces** — correct.
- **`Quote` with paths containing embedded quotes** —
  `Replace("\"", "\\\"")` escaping. Not asserted.
- **Zenity not installed** — catches exception, returns null.
- **Zenity non-zero exit** (user cancelled) — returns null.
- **Zenity output trimming** — `.Trim()` at line 59.
- **Async awaiting** — `await proc.StandardOutput.ReadToEndAsync()`
  + `await proc.WaitForExitAsync()`.

## Architectural concerns

- **Hard dependency on zenity** — `kdialog` or GTK-based
  alternatives aren't tried. One fallback path. For KDE
  users, no dialog.
- **No filter support** — zenity can filter by pattern via
  `--file-filter=`. Not used. Non-text files appear.
- **Argument string concatenation** — sharp; quoting bugs
  have real consequences. An array-based `ProcessStartInfo.ArgumentList`
  would be safer.
