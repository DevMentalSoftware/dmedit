# `ColumnSelection`

`src/DMEdit.Core/Document/ColumnSelection.cs`
Tests: `tests/DMEdit.Core.Tests/ColumnSelectionTests.cs` (exists);
additional coverage in `DocumentTests` (mid-pair snap helpers).

330-line `readonly record struct` representing a rectangular multi-cursor
selection, plus a bag of static tab-aware column/char-index math
helpers. More logic than a record-struct normally carries; the static
helpers could live on a dedicated utility class.

## Likely untested

### Record-struct API (mutators)
- **`ShiftColumns(delta)` with negative delta that clamps both Anchor
  and Active independently** — e.g. anchor=2, active=5, delta=−4 →
  anchor=0 (clamped), active=1. Tests may cover this; worth verifying.
- **`ShiftLines`** with delta that clamps only one end.
- **`MoveColumnsTo`** — no obvious targeted test.
- **`CollapseToLeft`/`CollapseToRight`** for an already-collapsed
  selection.
- **`ExtendLine` with an existing AnchorLine == ActiveLine** (growing
  a previously-single-line column selection).

### Tab-aware helpers (bulk of the file)
- **`ColToCharIdx` fast-path / slow-path split.** The fast path
  (tab-free line) and slow path (tab-bearing tail after the first
  tab) are both reached by varying test inputs, but no test explicitly
  pins the behavior at the transition: a line with a single tab at a
  known offset, and queries for columns just before, at, and just
  after the tab.
- **`ColToCharIdx` with `targetCol > end-of-line column`** — returns
  `lineLen`. Not explicitly asserted.
- **`ColToCharIdx` with a line that starts with a tab** — `firstTabIdx
  == 0`, so the "tab-free prefix up to the first tab" branch (line 137)
  never fires — the function should fall through to the per-char loop
  immediately. Boundary condition.
- **`OfsToCol` with `ofs > lineEnd`** (line 196-198 virtual-space
  case) — untested per the code comment "shouldn't normally happen."
- **`PaddingNeeded`** on a line with mixed tabs and spaces.
- **`EndOfLineCol`** on an empty line.
- **`FindWordBoundaryCol`** direction −1 from column 0 (should stay
  at 0). Direction +1 past end of line.
- **`NextCharCol`** at the very start of a line going left (should
  return 0 and not cross the line boundary).
- **`Materialize` with a mix of short lines and long lines** — short
  lines should produce collapsed selections at the end of content.
  Source comment at line 59 says so; the tests may or may not cover
  it.
- **`Materialize` with `top > bot`** (an inverted or out-of-range
  rectangle after clamping) — returns `[]` (line 64). Not tested.
- **`MaterializeCarets` similarly.**
- **`FindFirstTab`** with a zero-length range.

### Surrogate-pair snapping
- `ColumnSelection_OfsToCol_MidPair_SnapsBackward` and
  `ColumnSelection_ColToCharIdx_MidPairCol_SnapsForward` exist in
  `DocumentTests.cs:463,470`. Good. Check whether they also cover
  the `EndOfLineCol` / `NextCharCol` / `FindWordBoundaryCol` paths —
  probably not.

## Architectural concerns

- **The record struct carries a lot of static methods.** 8 mutators
  (reasonable for a record) + `ColToCharIdx`, `OfsToCol`, `PaddingNeeded`,
  `FindFirstTab`, `EndOfLineCol`, `FindWordBoundaryCol`, `NextCharCol`,
  `LineContentEnd`, `Materialize`, `MaterializeCarets`,
  `SnapCharIdxToBoundary`. Several of those (`LineContentEnd`,
  `OfsToCol`, `PaddingNeeded`) are generally useful and have nothing
  to do with column selection specifically — they happen to be here
  because column selection was the first feature to need them.
  **Suggestion:** move `OfsToCol`, `ColToCharIdx`, `PaddingNeeded`,
  `FindFirstTab`, `EndOfLineCol`, `LineContentEnd`,
  `FindWordBoundaryCol`, and `NextCharCol` into a new static
  `TabColumnMath` (or similar) helper. The `Materialize` /
  `MaterializeCarets` / `Extend*` / `Shift*` methods stay on the
  record.
- **`LineContentEnd` is `internal` static** — the only internal in
  the file. Every other method is public. If the helper class above
  is created it can be public.
- **`OfsToCol` on line 180 snaps backward, then decodes line from
  ofs, then re-scans the line.** For random-access `OfsToCol` the
  snap is redundant with what `LineFromOfs` already does. Minor.

## Simplification opportunities

- **`ColToCharIdx`'s fast path** — lines 123-132 are correct and
  well-commented; after the static-helper move, keep as is.
- **`NextCharCol` could be ~10 lines shorter** by factoring the
  end-of-line clamp.
- **`SnapCharIdxToBoundary`** could live in `CodepointBoundary` as a
  friendlier overload taking `lineStart + charIdx`; the current
  wrapper is fine.

## Bugs / hazards

- **`ColToCharIdx` slow-path indexing subtlety.** The loop at line 145
  does `for (var i = firstTabIdx; i < lineLen; i++)` and reads
  `table.CharAt(lineStart + i)`, advancing `col` per character. When
  `i` lands on the low half of a surrogate pair, the tab check is
  false (surrogate half isn't `\t`) and `col` gets incremented twice
  for a single rune. This is the same bug the `SnapCharIdxToBoundary`
  helper at the end was added to mitigate, but the mitigation only
  runs when the loop returns. If the caller happens to ask for a
  column that lands exactly between the two halves of a wide
  character, the per-char loop will hit `col == targetCol` on the
  high surrogate and return a position that then gets snapped
  forward. Works, but the slow path's `col++` is still
  code-unit-oriented, which is a drift from the rest of the file
  which is code-point-oriented. Worth a test: tab + emoji + text,
  query the column past the emoji's high surrogate.
