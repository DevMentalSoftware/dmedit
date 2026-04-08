# `BlockLayoutResult`

`src/DMEdit.Rendering/Layout/BlockLayoutResult.cs` (75 lines)

Owns the list of `BlockLayoutLine` and provides block lookup
helpers.

## Likely untested
- **`FindBlockAtY` linear scan** — correct but O(N). For big
  documents this is a per-frame cost. Binary search on
  `block.Y` ascending would be O(log N).
- **`FindBlockAtY(y < 0)`** — returns null because the first
  block has `Y >= 0`. Untested.
- **`FindBlockAtY(y >= totalHeight)`** — returns the last block.
  Untested.
- **`FindBlockByIndex` linear scan** — O(N). Block results
  are ordered by block index, so binary search on
  `BlockIndex` also works. Low priority.

## Architectural concerns
- **`FirstBlockIndex` exists for windowed layout** but
  `BlockLayoutResult` is only produced as whole-document
  layouts today. Dead until partial layout is wired.
- **`Dispose` disposes each block** — good.
