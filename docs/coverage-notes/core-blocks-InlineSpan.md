# `InlineSpan`

`src/DMEdit.Core/Blocks/InlineSpan.cs` (45 lines)
Tests: indirectly via `BlockTests`.

Record with `Type`, `Start`, `Length`, optional `Url`; helpers
`End`, `Shift`, `Resize`, `Overlaps`, `Contains`.

## Likely untested
- **`Resize` with a negative delta larger than `Length`** — the
  `Math.Max(0, …)` clamps to 0. Tested indirectly through
  `DeleteText_InsideSpan_ShrinksSpan` but not asserted as a
  unit.
- **`Overlaps` edge cases:** span ends exactly at `start`
  (non-overlap), span starts exactly at `start+length`
  (non-overlap), touching boundaries. Half-open convention.
  Worth pinning.
- **`Contains(0, 0)`** — degenerate; current implementation
  returns true if `Start <= 0 && 0 <= End`. Semantics for
  zero-length range are ambiguous. Worth a comment.
- **`Url` on non-Link span types** — current code allows any
  type to have a `Url`. Should only matter for `Link`.

## Architectural concerns
- **`record` (class-based record), not `readonly record struct`**
  — allocations on every `Shift`/`Resize`. Since `InlineSpan`
  is small and immutable, `readonly record struct` would avoid
  the allocations. Minor.
