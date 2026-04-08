# `TextLayoutEngine`

`src/DMEdit.Rendering/Layout/TextLayoutEngine.cs` (335 lines)
Tests: `tests/DMEdit.Rendering.Tests/TextLayoutEngineTests.cs`
(~47 tests, including the 16 added in entry 22 for crash hardening).

Primary layout dispatcher. Decides per-line between the monospace
GlyphRun fast path and the Avalonia `TextLayout` slow path. Owns
the sanitize + try/catch crash hardening for the slow path.

## Likely untested

- **`LayoutLines(..., useFastTextLayout: false)`** — forces the
  slow path for every line. Should be tested as an explicit
  option (ligature-bearing font case).
- **`LayoutLines` with `maxWidth == double.PositiveInfinity`** —
  `NoWrap` branch on the slow path, `int.MaxValue` charsPerRow
  on the mono path. Not pinned.
- **`LayoutLines` with `topLine == bottomLine`** — zero-length
  range; produces an empty lines list that triggers the fallback
  empty-layout branch (line 140-143). Not pinned.
- **`LayoutLines` with `topLine` or `bottomLine` beyond `lineCount`**
  — loop short-circuits. Not asserted.
- **`LayoutLines` with a line where `lineStart < 0`** — happens
  during streaming load races (line 101 skip). Not pinned.
- **`LayoutLines` with `contentLen > MaxGetTextLength`** — throws
  `LineTooLongException`. Tested? Probably via the CharWrap
  trigger flow, but worth direct coverage.
- **`MakeTextLayoutSafe` fallback branches** — two layers of
  `InvalidOperationException` catch. The first falls back to
  `NoWrap`; the second returns an empty layout. The first
  catch is exercised by the entry-22 regression tests; the
  second (both retries fail) is not directly tested.
- **`SanitizeForTextLayout`:**
  - "Clean input returns the same instance" fast path (line 290).
    Not asserted (would need reflection/`ReferenceEquals`).
  - Sanitizing a tab character (which should NOT be scrubbed —
    `c < 32 && c != '\t'`). Tab preservation not pinned.
  - A string containing only surrogate halves. Edge case.
- **`HitTest` with `pt.Y < 0`** — `FindLineAt` returns
  `lines[0]`, but the negative Y translates to a negative
  `localPt.Y`. `HitTestPoint` clamps. Not asserted.
- **`HitTest` / `GetCaretBounds` on an empty layout** — the
  fallback-empty-layout branch ensures `lines.Count > 0` after
  `LayoutLines`, but consumers could pass a hand-built
  `LayoutResult([], ...)`. The guards at 154 and 171 return
  defaults; not pinned.
- **`GetCaretBounds(posInLine == 0)` on the slow path** shim at
  line 181 that avoids TextLayout's degenerate rect. No test
  asserts the rect dimensions explicitly.
- **Row-height override** path vs computed-from-space-layout
  path. Two separate branches at line 65; only the override
  path is exercised by EditorControl. A direct test with a
  known override would pin alignment.

## Architectural concerns

- **Two independent `MakeTextLayout` helpers** — one in
  `TextLayoutEngine` (line 210) and one in `BlockLayoutEngine`
  (`CreateTextLayout`, line 157). Nearly identical. Dedupe.
- **`SanitizeForTextLayout` lives here** but the same scrubbing
  logic may be needed by the block layout engine (which has no
  equivalent hardening). If a binary file is ever loaded via
  the block model, the same Avalonia split crash is reachable.
  Move sanitize to a shared helper.
- **`MakeTextLayoutSafe` is named like the logical pair of
  `MakeTextLayout`** but it also silently drops content on
  the second fallback. That's a correctness shift from
  "display" to "stop the crash." Worth a clearer name like
  `MakeTextLayoutOrFallback`.
- **`FindLineIndexForOfs` is O(lines)** in the worst case. For
  the visible-window sizes used today (~100 lines) that's
  fine. Could be binary search on `CharStart`. Micro.
- **Stateless engine** — good. No per-instance state means
  concurrent layouts are safe, which the EditorControl may
  eventually leverage.

## Bugs / hazards

1. **`SanitizeForTextLayout` does not scrub lone surrogate halves
   in certain edge positions.** The scan does `char.IsHighSurrogate(c)`
   then looks at `i + 1`; if the scan finds a lone low surrogate
   at `i`, it sets `bad = true`. But the single-pass rewrite in
   the bad-case branch (lines 293-310) duplicates the same logic.
   Both are correct. Worth a property test over random input with
   injected lone surrogates.
2. **`LayoutLines` calls `table.GetText(lineStart, contentLen)`**
   which allocates a string per line. Known hot path; the mono
   fast path still does this allocation before checking whether
   the line qualifies. For a scroll over a monospace file, every
   line allocates a string that may or may not be used. Worth a
   `TryGetLineDirect` that returns a span.
3. **`MakeTextLayoutSafe`'s final empty-layout fallback** at line
   261 uses `""` but keeps the line's original `CharLen` in the
   LayoutLine. Hit-testing on a line where the layout has zero
   content but the CharLen is positive: `HitTestPoint` will
   probably return 0 regardless of click position. Acceptable
   for a fallback from a crash, but UX is bad. Log a warning.
