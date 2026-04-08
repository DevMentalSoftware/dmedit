# `DMEditableCombo`

`src/DMEdit.App/Controls/DMEditableCombo.cs` (290 lines)

Editable combo box: DMInputBox + clear button + dropdown +
history popup with auto-suggest.

## Likely untested

- **Entire control** — no dedicated test file.
- **Popup open/close on dropdown click** — untested.
- **Auto-suggest filtering** as user types — untested.
- **HighlightItem star prefix** (⭐) — display-only marker.
  Selecting the item should strip the star. Not asserted.
- **Keyboard nav** in the popup list (Up/Down/Enter/Escape).
- **Click outside popup to close**.
- **ItemsSource null** handling.
- **Watermark display** when Text is empty.

## Architectural concerns

- **Same pattern as DMTextBox** — named template parts.
  Template drift breaks silently.
- **Star character embedded as a display hack** for
  HighlightItem — the select handler strips it. Subtle
  round-trip that's easy to break.
- **Mutable `ItemsSource`** — if the caller mutates the list
  after assignment, does the popup reflect changes? No
  observer wired.
