# `SaveChangesDialog`

`src/DMEdit.App/SaveChangesDialog.cs` (324 lines)

Dialog shown when closing a tab or exiting with unsaved
changes. Save / Don't Save / Cancel choices.

## Likely untested
- **Three-way result.**
- **Multi-file variant** — "Save N files?" when closing
  multiple dirty tabs at once.
- **Escape == Cancel.**
- **Enter == Save (default button).**

## Architectural concerns
- **324 lines for a dialog** is a lot. The multi-file
  variant likely drives the size. Consider splitting.
