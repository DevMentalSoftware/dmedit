# `BlockStructureChangeKind` (enum)

`src/DMEdit.Core/Blocks/BlockStructureChangeKind.cs`

5-value enum: `Insert`, `Remove`, `Split`, `Merge`, `TypeChange`.

## Likely untested
- Consumer should fire one event per change, with the right kind.
  Tested implicitly via `BlockDocumentTests`; no exhaustive per-kind
  assertion table.

## Architectural concerns
- **`TypeChange` doesn't carry the old/new type.** Observers have
  to read the current block to know what it became, and can't
  recover what it was. Worth extending the event args.
