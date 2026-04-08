# `GoToLineWindow`

`src/DMEdit.App/GoToLineWindow.cs` (135 lines)

Modal dialog for "Go to line". Numeric input, validation,
Enter-to-submit.

## Likely untested
- **Out-of-range input** — clamped to 1..LineCount.
- **Non-numeric input** — rejected or parsed partially.
- **Enter in empty field** — no-op or error?
- **Escape cancels.**
- **Negative / zero input.**
- **Line 1 is 1-based** (user-facing) but core is 0-based.
  Off-by-one risk.
