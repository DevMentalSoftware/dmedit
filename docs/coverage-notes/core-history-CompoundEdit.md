# `CompoundEdit`

`src/DMEdit.Core/Document/History/CompoundEdit.cs`

29-line group-of-edits. `Apply` forward, `Revert` reverse.

## Likely untested

- **Empty compound.** `EditHistory.EndCompound` short-circuits
  `edits.Count == 0`, so no empty CompoundEdit should ever reach
  the undo stack. But the class has no guard — constructing one
  directly with an empty list is fine. Worth a comment or an
  invariant.
- **Nested compounds** (a CompoundEdit containing another
  CompoundEdit). Works transparently because each child's
  `Apply`/`Revert` is called polymorphically. Not explicitly
  tested; `EditHistory.BeginCompound` has a `_compoundDepth`
  counter specifically to avoid flattening inner groups.
- **Apply / Revert asymmetry.** If one inner edit's `Apply`
  throws, the outer Apply doesn't roll back the already-applied
  edits. `PieceTable` is now in a partial state. No test pins
  this, and no recovery strategy exists.

## Architectural concerns

- **Accepts `IReadOnlyList<IDocumentEdit>` in the constructor**
  — good. But stores the reference rather than copying. If the
  caller mutates the list after construction, the edit sees the
  changes. `EditHistory.EndCompound` doesn't, so it's safe in
  practice.
- **No way to inspect or modify the group post-construction.**
  Good — immutability is desirable.

## Simplification opportunities

- Tiny class, nothing to simplify.
