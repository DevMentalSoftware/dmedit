# 23 — Scroll/Layout Invariant Infrastructure

**Date:** 2026-04-11

## Context

The scroll/layout/caret test coverage plan (design journal "In progress")
identified ~13 methods that need exhaustive combinatorial testing before
the Avalonia 12 upgrade.  The raw cross-product of test dimensions
(4 wrap modes × 3 font paths × 2 window widths × 6 doc structures × …)
produces 33,000+ behavioral tests and ~5,500 hidden-behavior tests.

Three strategies compress the matrix:

1. **Prove symmetry** — MoveCaretVertical Up/Down is sign-symmetric;
   test one direction exhaustively, smoke-test the other.
2. **Unify code paths** — extract shared decision points so the "font
   path" axis collapses to a single function under test.
3. **Invariants in production code** — make illegal states throw in
   Debug, eliminating entire categories of tests.

This entry covers strategy 2 and 3.  Estimated reduction: ~39,000 → ~12,500 tests.

## Bug found: row count mismatch

The row count alignment invariant fired immediately on the existing
test suite, catching a latent bug that would cause Find scrolling to
land off-viewport in certain wrap+font combinations.

**Root cause (char width divergence):**
`ComputeLineRowCount` and `ComputeRowOfCharInLine` computed
`maxCharsPerRow` differently from the renderer:

- **Renderer** (`MonoLayoutContext`): derives char width from the space
  glyph's advance via `GlyphTypeface.GetGlyphAdvance` — e.g. 11.2 px.
- **Scroll math** (`GetMonoRowWidths` → `GetCharWidth`): measured `"0"`
  via `TextLayout.WidthIncludingTrailingWhitespace` — e.g. 10.0 px.

Different widths → different chars-per-row → different row counts →
scroll targets off by (difference × rowHeight) pixels.

**Root cause (stale Bounds):**
`GetMonoRowWidths` recomputed the text-area width from `Bounds.Width`,
which is 0 during `MeasureOverride` (before Arrange sets it).  The
layout engine receives the correct width as a parameter.

## Fixes

### `GetMonoCharWidth()` — new method

Derives char width from the space glyph's advance (same path as
`MonoLayoutContext`).  Falls back to `GetCharWidth()` when the font
isn't monospace.  File: `EditorControl.Scroll.cs`.

### `_lastTextWidth` — cached text-area width

Set in both `EnsureLayout` and `MeasureOverride` before calling
`LayoutWindowed`.  `GetMonoRowWidths` uses the cached value instead of
recomputing from `Bounds`, ensuring it sees the same width the layout
engine used.  Field: `EditorControl.cs`.

### `ShouldUseSlowPath(text)` — unified decision point

Replaces two duplicated `!IsFontMonospace() || ContainsSlowPathChars(text)`
checks in `ComputeLineRowCount` and `ComputeRowOfCharInLine`.  One
function, one decision, one truth.  File: `EditorControl.Scroll.cs`.

## Invariants added (`#if DEBUG`)

All invariants use `Debug.Assert` — they fire in Debug builds and test
runs (where the test host converts them to `DebugAssertException`) but
are stripped from Release.

| Invariant | Location | What it catches |
|-----------|----------|-----------------|
| Row count alignment | `LayoutWindowed`, after layout built | `ll.HeightInRows != ComputeLineRowCount(lineIdx)` — renderer and scroll math disagree on how many visual rows a line occupies |
| Row-of-char consistency | `ComputeRowOfCharInLine`, at return | Row index >= row count (impossible state, would corrupt scroll targeting) |
| Near-end remap postcondition | `ScrollSelectionIntoView`, after remap | Rows from `targetTopLine` to end-of-doc don't fill the viewport — would leave blank space below last line |
| ScrollExact scroll bounds | `ScrollExact`, after direct `_scrollOffset` write | Negative scroll offset from bypassing the setter's clamp |
| Caret bounds | `ScrollCaretIntoView` + `ScrollSelectionIntoView` entry | Caret or anchor outside `[0, doc.Length]` |

## PerfStats additions

`PerfStats.ScrollExactCalls` — incremented in `ScrollExact`.  Tests
snapshot before/after a user action and assert the delta is 0 (no scroll
needed) or 1 (single scroll).  Multiple ScrollExact calls per action
indicates wasteful layout rebuilds.

Existing counters useful for hidden-behavior tests:
`ScrollCaretCalls`, `ScrollRetries`, `LayoutInvalidations`, `RenderCalls`.

## Impact on test matrix

The invariants make ~10,000 tests redundant:

- **Row count alignment invariant** fires on every Debug layout pass,
  catching font-path divergence without dedicated cross-product tests.
- **Caret bounds invariant** catches out-of-range selections without
  per-method boundary tests.
- **Near-end remap postcondition** catches viewport-fill failures
  without direction × doc-size × scroll-state cross-product.

Combined with symmetry proofs and `extend` flag isolation, the estimated
net new test count drops from ~39,000 to ~12,500.
