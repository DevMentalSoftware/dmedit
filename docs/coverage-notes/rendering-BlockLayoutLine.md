# `BlockLayoutLine`

`src/DMEdit.Rendering/Layout/BlockLayoutLine.cs` (92 lines)

Disposable wrapper around a per-block `TextLayout` with style
chrome (margin/padding). Counterpart to `LayoutLine` for the
block model.

## Likely untested
- **`TextLength` vs layout text length** — when the block is
  empty, layout text is `" "` (per BlockLayoutEngine) but
  `TextLength == 0`. Hit-test clamps to `TextLength`. Not
  asserted at this level.
- **`Dispose` second call** — `_disposed` guard.
- **`ContentY` / `ContentX` arithmetic** — trivial.

## Architectural concerns
- **`ContentHeight => Layout.Height`** — computed property that
  calls into Avalonia. Not cached. Probably called once per
  render; fine.
- **No way to distinguish "block with empty text" from "block
  that failed layout"** — both have `TextLength == 0`.
