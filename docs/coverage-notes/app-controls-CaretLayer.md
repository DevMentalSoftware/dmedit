# `CaretLayer`

`src/DMEdit.App/Controls/CaretLayer.cs` (102 lines)

Tiny child control that paints one caret rectangle. Purpose:
`InvalidateVisual` on just this control repaints ~20px instead
of triggering the whole `EditorControl.Render`, which drops
per-blink allocation cost. See journal entry 21.

## Likely untested

- **Blink toggles `CaretVisible`** and the layer repaints
  itself. No test.
- **`OverwriteMode` block-caret rendering** — translucent
  alpha-100 fill. Not asserted.
- **`Brush` is a non-`ISolidColorBrush`** — falls back to
  black (line 94). Edge case.
- **Zero-sized bounds** — `Render` early-returns. Not
  asserted.
- **`CaretWidth` set to negative** — currently no guard.
  Would render nothing. Harmless.

## Architectural concerns

- **Not focusable, not hit-testable** — correct.
- **`Brush` equality check is `ReferenceEquals`** — changing
  to an equal but different brush instance forces a
  repaint. OK because brushes are typically long-lived.
- **`CaretWidth` double comparison with `==`** — Avalonia
  will never emit a NaN here; works.
- **Column-caret pool lives in `EditorControl`**, not here.
  Right separation.
