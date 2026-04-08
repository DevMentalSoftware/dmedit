# `BlockDocument`

`src/DMEdit.Core/Blocks/BlockDocument.cs` (380 lines)
Tests: `BlockDocumentTests.cs`.

Ordered block list with parallel Fenwick trees for char-length
and pixel-height prefix sums. **Not wired into production** (see
journal).

## Likely untested

- **Tree rebuild cost on structural changes.** Insert/Remove do
  incremental updates where possible; a full Rebuild is O(N).
  No regression test pins that rebuild only happens on
  explicitly-marked operations.
- **`HeightEstimator` delegate when null** — falls back to a
  rough built-in. Both paths should have tests.
- **`FromText` with `"\r\n"` endings** — strips trailing `\r`
  (line 57). Tested?
- **`FromText` with empty string** — produces a single empty
  Paragraph block. Tested by `FromText_*` I assume.
- **Structure-changed event firing and payload** for each of the
  five `BlockStructureChangeKind` values. Especially `Split` and
  `Merge` indices per the XML docs (which say Split reports the
  original block index and Merge reports the survivor).
- **`this[int]` with a negative or out-of-range index** — throws
  via `List<T>`'s own bounds check. Not asserted.
- **Char tree and height tree consistency** — the parallel trees
  can drift if only one is updated on a structural change. No
  debug-mode invariant check.

## Architectural concerns

- **`BlockDocument` is parallel to `Document`** (the PieceTable
  wrapper). They solve overlapping problems with different data
  models. Long-term, one of these has to go or they need a
  clear coexistence story. The journal calls this out.
- **`FenwickTree` is rebuilt on structural changes** (line 45).
  For a big import this is O(n log n). The blocks/Fenwick
  relationship should use the incremental `Update` path where
  possible.
- **`Block.Changed` subscription** is wired per block at
  construction and detached on removal. A test that inserts and
  then removes a block and asserts no leaked handler wouldn't
  hurt (event-handler leaks are a known DMEdit issue — see
  journal "closure leaks").
- **`HeightEstimator` is a mutable property**, not passed at
  construction. Changing it does not re-run existing estimates,
  so the height tree is out of sync until another operation
  triggers a rebuild. Should either rebuild on set or be
  immutable.

## Simplification opportunities

- **`BlockDocument` and the editor model should converge** —
  don't add features to this class until its role is
  decided.
