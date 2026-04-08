# `VaryingBulkReplaceEdit`

`src/DMEdit.Core/Document/History/VaryingBulkReplaceEdit.cs`
Tests: `BulkReplaceTests.Document_VaryingBulkReplace_*`, also used
by `ConvertIndentation_*` tests.

45-line edit: heterogenous matches + replacements. Snapshot-based
O(1) revert, same shape as `UniformBulkReplaceEdit`.

## Likely untested
- Same gaps as `UniformBulkReplaceEdit.md`: idempotent revert,
  intervening add-buffer growth, serialization round-trip beyond
  the `ConvertIndentation` path.
- **Matches and replacements length mismatch** — no guard.
  `Matches[i]` and `Replacements[i]` are paired by index; if
  `matches.Length != replacements.Length`, the core loop will
  throw `IndexOutOfRangeException` inside PieceTable. Worth a
  ctor precondition.
- **Zero-length match with non-empty replacement** (pure insert)
  and non-zero match with empty replacement (pure delete) are
  both legal. Not explicitly tested at this level.

## Architectural concerns
- See `UniformBulkReplaceEdit.md` re. shared base class.

## Simplification opportunities
- None beyond the dedup with Uniform.
