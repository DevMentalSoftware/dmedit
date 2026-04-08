# `ExpandSelectionMode` (enum)

`src/DMEdit.Core/Document/ExpandSelectionMode.cs`
Tests: `DocumentTests.ExpandSelection_*` uses both values indirectly.

2-value enum controlling the level hierarchy for
`Document.ExpandSelection`: `Word` (whitespace → line → doc) vs
`SubwordFirst` (subword → whitespace → line → doc).

## Likely untested

- **Nothing at the enum level.** The consumer is
  `Document.ExpandSelection`. The subword path is well-covered; the
  plain "Word" path (skipping the subword level) is at least hit by
  `ExpandSelection_Whitespace_StopsAtPairBoundary` but there's no
  test that explicitly calls `ExpandSelection(ExpandSelectionMode.Word)`
  to verify it really skips the subword level.

## Architectural concerns

- **Name drift.** `Word` really means "whitespace-bounded", not
  "word." Rename to `WhitespaceFirst` for symmetry with `SubwordFirst`.
  UI probably binds to the enum name; would also need a display
  string update.
