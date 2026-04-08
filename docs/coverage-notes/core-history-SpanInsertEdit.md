# `SpanInsertEdit`

`src/DMEdit.Core/Document/History/SpanInsertEdit.cs`

32-line insert edit. Stores `(ofs, addBufStart, len, bufIdx)`;
`Apply` creates a piece, `Revert` calls `table.Delete(ofs, len)`.

## Likely untested

- **`bufIdx = -1` default vs explicit `bufIdx`.** Default uses
  `table.AddBufferIndex` at apply time. An explicit `bufIdx` is
  used by session restore. If a Session-restored edit references
  a buffer index that was swapped/disposed, `InsertFromBuffer`
  will throw. No test pins that error path.
- **Redo semantics after `TrimAddBuffer`.** If a bulk-replace undo
  trimmed the add buffer below this edit's `addBufStart + len`,
  redoing this insert would read garbage. The current design
  seems to assume bulk-replace only touches "bulk" replacement
  spans at the tail, but the add buffer is a single shared
  stream. Worth a test or an assertion.
- **`Revert` after any other edit that changed the length at `ofs`**
  — undo-redo ordering is what guarantees correctness. If someone
  calls `Revert` outside the EditHistory flow, behavior is
  undefined. Interface doesn't say so.

## Architectural concerns

- **Primary-constructor-as-fields** (`(long ofs, long addBufStart,
  int len, int bufIdx = -1)`) is neat but means the private state
  is the parameters. Slightly unusual for a class that also exposes
  them as properties; the properties are "view of the parameter."
  Fine.
- **`bufIdx < 0 ? table.AddBufferIndex : bufIdx`** computes the
  buffer at apply time. If the table's add-buffer index changes
  between pushes (e.g. session load installs a new add buffer),
  the resolution drifts. `PieceTable.SetAddBuffer` sets
  `_buffers[_addBufIdx] = buffer` without changing `_addBufIdx`,
  so the index is stable. Worth a comment noting the invariant.
