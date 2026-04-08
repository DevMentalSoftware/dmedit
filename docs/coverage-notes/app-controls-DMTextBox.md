# `DMTextBox`

`src/DMEdit.App/Controls/DMTextBox.cs` (92 lines)

Templated control wrapping a `DMInputBox` with an auto-hiding
clear button. Forwards focus to the inner input box.

## Likely untested
- **Clear button click clears Text and focuses inner box** —
  untested.
- **`ShowClearButton` toggle** — untested.
- **`UpdateClearButtonVisibility`** guard when `_clearButton
  is null` (template not applied yet). Works, not asserted.
- **`OnGotFocus` forwarding** — untested.
- **Two-way binding of `Text`** — implicit through the
  StyledProperty but not asserted.

## Architectural concerns
- **`InnerTextBox` is a public get; private set** — external
  access lets callers directly set `CaretIndex`, `SelectAll()`,
  etc. The comment at line 49 acknowledges this. Convenient
  but leaks the inner control. A `Focus()`, `SelectAll()`,
  `CaretIndex { get; set; }` wrapper would keep it
  encapsulated.
- **Inner control is looked up by name** in `OnApplyTemplate`.
  If the template name changes, `InnerTextBox` is null and
  consumers silently break.
