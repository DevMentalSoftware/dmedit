# `LineTooLongException`

`src/DMEdit.Rendering/Layout/LineTooLongException.cs` (18 lines)

Thrown by `TextLayoutEngine.LayoutLines` when a line exceeds
`PieceTable.MaxGetTextLength`. The editor catches this and
should suggest CharWrap mode.

## Likely untested

- **Thrown and caught by EditorControl** — should suggest
  CharWrap. Per the journal, CharWrap triggering currently
  has a known bug ("CharWrap not triggering on 1MB
  single-line file"). Worth a test that:
  1. Opens a document with a 2 MB single line.
  2. Layout attempts throw `LineTooLongException`.
  3. CharWrap gets enabled.
  4. Subsequent layout succeeds.

## Architectural concerns
- **Carries `LineLength` and `MaxLength`** as explicit properties
  — good for diagnostics.
- **Message is user-facing** ("Consider enabling character-
  wrapping mode for this file"). If the exception ever reaches
  the fatal error dialog, the user sees a useful hint. Good.
