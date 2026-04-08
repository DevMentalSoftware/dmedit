# `BlockLayoutEngine`

`src/DMEdit.Rendering/Layout/BlockLayoutEngine.cs` (227 lines)
Tests: unknown — no dedicated test file I've seen.

Lays out a range of blocks from a `BlockDocument` using styles
from a `StyleSheet`. **Not wired into production**; the Block
model isn't the editor's main path.

## Likely untested

- **Entire class coverage.** The Block model isn't shipped,
  so these tests probably don't exist. Worth checking.
- **`LayoutRange` with `startIndex > doc.BlockCount`** —
  `endIndex = Math.Min` clamps; loop doesn't execute; empty
  result. Not pinned.
- **`LayoutRange` with a block whose text is empty** — uses
  `" "` as layoutText to get a non-zero height (line 70).
  `TextLength` on the block layout line is 0 so hit-tests
  don't clamp to the `" "`. Worth pinning.
- **`HitTest` with an empty `result`** — returns
  `BlockPosition(0, 0)`. Not pinned.
- **`HitTest` with `pt.Y` past the last block** — `FindBlockAtY`
  returns last. Tested?
- **`GetCaretBounds(position.LocalOffset == 0)` shim** at line
  140 — returns a zero-width rect at origin. Same shim as
  `TextLayoutEngine.GetCaretBounds`. Fine.
- **`ParseBrush` on an invalid color string** — catches and
  returns null, caller falls back to DefaultForeground. Not
  asserted.
- **`UpdateFontMetrics` feedback loop** — mutates
  `style.AvgCharWidth` via `StyleSheet.UpdateFontMetrics`.
  Per the `StyleSheet` notes, this is load-bearing and has a
  potential shared-default-style bug. Worth pinning.
- **Monospace `UpdateFontMetrics` path** — builds a one-char
  TextLayout with "M" (line 202-206). Hard-coded brush
  (`Brushes.Black`). If the style has a non-default
  foreground, the measurement is still correct (width is
  measured, color ignored), but allocating a fresh layout
  every block is wasteful.

## Architectural concerns

- **`CreateTextLayout` is a near-duplicate of
  `TextLayoutEngine.MakeTextLayout`.** Dedupe.
- **No sanitize / try-catch for Avalonia crashes.** The same
  `ShapedTextRun.Split` crash that affects the main editor
  could hit the block engine if it ever lays out binary-ish
  content. Port `MakeTextLayoutSafe`/`SanitizeForTextLayout`
  here, or (better) move those to a shared helper class.
- **Hit-test clamps to `TextLength`**, not the layout's text
  length. Correct because the layout uses `" "` for empty
  blocks. Subtle enough to call out.
- **`UpdateFontMetrics` hard-codes `Brushes.Black`** — wouldn't
  matter for measurement, but inconsistent. Use the block's
  foreground.
- **No row-height concept** — each block has its own
  `ContentHeight`. Different from the stream model.
  Consistent with the block semantics (paragraphs can have
  different font sizes).

## Bugs / hazards

1. **Crash-hardening is missing.** If this engine ever handles
   real user files, it needs the same sanitize + fallback as
   `TextLayoutEngine`.
2. **`UpdateFontMetrics` leaks a TextLayout** (line 202-206 via
   `using`). OK — the `using` disposes it.
3. **`ParseBrush` catches all exceptions** (line 223). Fine for
   a malformed color string.
