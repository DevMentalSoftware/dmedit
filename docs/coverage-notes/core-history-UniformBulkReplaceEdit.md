# `UniformBulkReplaceEdit`

`src/DMEdit.Core/Document/History/UniformBulkReplaceEdit.cs`
Tests: `BulkReplaceTests.Document_UniformBulkReplace_Undo` / `_Redo` /
`_MultipleUndoRedo`.

49-line edit: same-length, same-replacement bulk. Undo
reconstructs from saved pieces + saved line tree + truncated add
buffer.

## Likely untested

- **`Apply` when `_matchPositions.Length == 0`** — `table.BulkReplace`
  has an early return. Tested.
- **`Revert` twice in a row** — should idempotently return to the
  pre-replace state. Second Revert with an already-trimmed add
  buffer… actually this is not a real scenario because undo pops
  off the stack. Fine.
- **Revert after an intervening `Push(otherEdit)`** — the redo
  stack is cleared, so the bulk-replace edit is gone. Not a
  real scenario.
- **Revert when the PieceTable's add buffer length has grown
  larger than `_savedAddBufLen + replacement.Length`** — can happen
  if a compound edit added more text after the bulk replace, then
  redoing only the bulk replace but undoing its siblings first…
  unclear if this is reachable. Worth tracing.
- **`MatchCount`/`Replacement`/`MatchLen` public getters** — used
  by `EditSerializer`, not tested at the edit level.

## Architectural concerns

- **Snapshot-based undo is O(N) in saved state size.** A 1 GB
  document with a bulk replace holds a full snapshot of pieces
  and line lengths. Worth documenting the memory cost.
- **`_savedAddBufLen` is the trim point for Revert.** If another
  edit (bulk or otherwise) appends to the add buffer between
  Apply and Revert, trimming back to `_savedAddBufLen` would
  discard the unrelated appends. Because EditHistory's undo
  stack only reverts the most recent edit, the intermediate
  edits must also be undone first — a LIFO discipline is
  required. Not stated anywhere; worth an invariant comment.

## Simplification opportunities

- **`UniformBulkReplaceEdit` and `VaryingBulkReplaceEdit` differ
  only in the `Apply` dispatch and the `_matches` shape.** A
  common base class `BulkReplaceEditBase` with an abstract
  `ApplyReplace` would shrink ~80 lines to ~60, at the cost of
  adding an inheritance layer. Marginal.
