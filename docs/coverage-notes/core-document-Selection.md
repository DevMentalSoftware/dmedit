# `Selection`

`src/DMEdit.Core/Document/Selection.cs`
Tests: no dedicated file; exercised indirectly through `DocumentTests`.

27-line `readonly record struct` with `Anchor`, `Active`, and derived
`Start`/`End`/`Len`/`IsEmpty`/`Caret`. Factory `Collapsed(ofs)` and
mutators `ExtendTo`, `CollapseToStart`, `CollapseToEnd`.

## Likely untested

- **`Selection(Active < Anchor)` reversed selection** — `Start`/`End`
  handle it via `Math.Min`/`Math.Max`. Tests never explicitly build a
  reversed selection and check the invariants.
- **`CollapseToStart`/`CollapseToEnd`** — no dedicated unit test.
- **`ExtendTo` on a collapsed selection** — should move the active
  end, leaving the (equal) anchor unchanged.
- **Negative offsets** — `new Selection(-1, -1)` is currently legal,
  no invariant enforced. If the editor ever produces one accidentally,
  subsequent piece table operations will throw. Debatable whether to
  add validation; current practice is "caller clamps before
  constructing."

## Architectural concerns

- **`Caret => Active`** is a second name for the same field. Useful
  for expressing intent at call sites, but makes skim-reading less
  clear. Fine — keep.
- **No explicit invariant that offsets are non-negative.** See above.

## Simplification opportunities

- None; this is correctly minimal.
