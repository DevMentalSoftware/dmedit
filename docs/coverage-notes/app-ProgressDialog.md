# `ProgressDialog`

`src/DMEdit.App/ProgressDialog.cs` (98 lines)

Modal progress dialog with phase text, percent bar, ETA,
cancel button. Used by Print, ReplaceAll, large paste.

## Likely untested
- **Cancel button** wires a CancellationTokenSource per
  journal (cancel-on-close).
- **ETA calculation** — per journal entry 16.
- **Phase transitions** (measure → print).
- **Close button during active operation** — cancels.

## Architectural concerns
- **Modal but shown via `Show()` not `ShowDialog()` in
  some paths** — to avoid dispatcher deadlock. Same
  concern as `ErrorDialog`.
