# `BlockPosition`

`src/DMEdit.Core/Blocks/BlockPosition.cs` (15 lines)
Tests: indirectly via `BlockDocumentTests`.

Tiny record struct `(BlockIndex, LocalOffset)` with two factories.

## Likely untested
- **`StartOf` / `EndOf` factories** — trivial but no direct asserts.

## Architectural concerns
- **No validation.** Negative indices / offsets are legal.

## Simplification opportunities
- None; correctly minimal.
