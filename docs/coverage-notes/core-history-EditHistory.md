# `EditHistory`

`src/DMEdit.Core/Document/History/EditHistory.cs`
Tests: `EditHistorySerializationTests.cs` (11 tests), plus
transitive coverage via every Document undo/redo test.

211-line class managing undo/redo stacks, compound grouping, save
points, and serialization. Well-structured but several edges
are sharp.

## Likely untested

- **`CanUndo` when a compound is open with no edits** —
  `_compound?.Count > 0` is false, so `CanUndo` returns whatever
  the undo stack says. Correct.
- **`CanUndo` when a compound is open with edits but the undo
  stack is empty** — `_compound?.Count > 0` is true. Semantically
  the user hasn't "committed" anything, but `CanUndo` reports
  true. Is that right? If the user hits Ctrl+Z mid-compound, the
  compound is abandoned… wait, there's no `Undo` path that
  handles a mid-compound cancel. `Undo` just pops from
  `_undoStack`. So `CanUndo` says yes, but calling `Undo` might
  return null (empty stack). Subtle. Worth a test and potentially
  a bug.
- **`BeginCompound` nesting with no edits inside the inner
  compound** — both levels flush as a single outer compound.
  Tested (`IsAtSavePoint_ReturnsTrue_WhenEmptyCompoundOpened`).
- **`EndCompound` called without matching `Begin`** — `_compound
  == null` early return. No harm.
- **`EndCompound` after two nested `Begin`s but only one `End`**
  — `_compoundDepth` stays at 1 forever (until Dispose-equivalent,
  which doesn't exist). Subsequent pushes will keep aggregating.
  No test pins this.
- **`PushAlreadyApplied` during an open compound** — bypasses the
  compound entirely and pushes directly to `_undoStack`. Is that
  intentional? Source doesn't say. Used only by
  `RecordBackgroundPaste`; may be deliberately outside the
  compound model, but it's a sharp corner.
- **`MarkSavePoint` after `Undo`** — `_undoStack.Count` may equal
  a past depth, producing a "false save point." Works correctly
  because the user literally just saved. Low concern.
- **`IsAtSavePoint` with a mid-compound edit count > 0 but
  undo-stack depth at savepoint** — returns false because
  `_compound.Count > 0`. Correct — there's uncommitted work.
- **`RestoreEntries` that throws inside an `entry.Edit.Apply`
  call** — partial rebuild leaves the table in a corrupt state.
  No recovery. Rare in practice (all edit types' Apply are
  deterministic given a valid table) but worth noting.
- **`GetUndoEntries` / `GetRedoEntries` ordering** — tested
  (`ReturnsBottomToTop`).

## Architectural concerns

- **Save point is an index into the undo stack.** When the user
  undoes past the save point and then edits again, the save point
  no longer matches any reachable state. `IsAtSavePoint` returns
  false as expected. But the classic "saved, edited, undone"
  sequence where the user undoes back to the saved state works
  because the depth matches. Subtle — worth a test for "save,
  edit, undo, should be at save point again."
- **`HistoryEntry` and `UndoRedoResult` are identical record structs**
  with the same fields. The source distinguishes them by intent
  ("result of an op" vs "a stored entry"), but this is twice the
  code. A single `HistoryEntry` would work.
- **`GetUndoEntries` allocates a fresh array each call** — copies
  the stack, reverses, converts. For a large history this is
  wasteful if called frequently. Not currently called frequently
  (only on session save). OK.
- **Compound state (`_compound`, `_compoundSelBefore`,
  `_compoundDepth`)** is three interdependent fields. A single
  `CompoundState?` struct would keep them together.

## Simplification opportunities

- **Unify `HistoryEntry` and `UndoRedoResult`.**
- **Compound state into a struct.**
- **`PushAlreadyApplied` and `Push` share all the post-push
  work.** Could have `Push(..., apply: true)` / `Push(..., apply:
  false)` with a bool. Minor.

## Bugs / hazards

1. **`EndCompound` when called more times than `BeginCompound`**
   drives `_compoundDepth` below zero. Subsequent
   `_compoundDepth++` in `BeginCompound` still resets to 1,
   but during the "over-ended" period `_compound` is null, so
   `EndCompound`'s early return catches it. Works by accident.
   Worth an explicit `if (_compoundDepth <= 0)` guard.
2. **`CanUndo` reporting true for a mid-compound session** —
   potentially lies about what `Undo` will do. See above.
3. **Exceptions inside `entry.Edit.Revert`** during `Undo`: the
   entry is already popped from `_undoStack` but hasn't been
   pushed to `_redoStack`. The entry is lost. Worth a try/catch
   + restore.
