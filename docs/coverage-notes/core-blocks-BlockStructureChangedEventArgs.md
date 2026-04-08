# `BlockStructureChangedEventArgs`

`src/DMEdit.Core/Blocks/BlockStructureChangedEventArgs.cs` (25 lines)

## Likely untested
- **Event firing with Split/Merge** — the XML doc specifies Split
  reports original-block index, Merge reports survivor. Tests
  should pin both semantics.

## Architectural concerns
- **Could be a `readonly record struct`** instead of `sealed class :
  EventArgs`. Event infrastructure accepts it fine, avoids
  per-fire allocation. Worth the tiny refactor.
- **Missing `TypeChange` payload** — see BlockStructureChangeKind notes.
