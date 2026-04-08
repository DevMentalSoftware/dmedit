# `MonoLineLayout`

`src/DMEdit.Rendering/Layout/MonoLineLayout.cs` (262 lines)
Tests: indirectly via `TextLayoutEngineTests` (monospace path).

Monospace fast-path layout for one logical line. Pre-built
`GlyphRun`s per wrapped row, pure-arithmetic hit testing.

## Likely untested

- **`TryBuild` rejection paths:**
  - Line contains a control character other than `\t` (the
    check is `c < 32`, which includes tab). Wait: looking at
    line 83 — the check is `if (c < 32) return null;` which
    *rejects* tabs. Comment at line 79 says "tab in particular
    needs column-aware advance." Correct.
  - Line contains a codepoint the typeface has no glyph for
    (non-Latin / emoji on a Latin-only font). Return null.
    Tested?
- **`TryBuild` with empty text** — single-row layout. Tested.
- **`TryBuild` with text shorter than `firstRowChars`** — single
  row, no wrap. Common case. Tested.
- **`TryBuild` with a hard-break (no space in the row width)** —
  line 130-135 falls back to breaking at the hard limit.
  Tested?
- **`TryBuild` with a line where every row's breakpoint is
  exactly at the end of row width** — edge case of
  `NextRow`'s backward scan.
- **`TryBuild` with `hangingIndentChars >= maxCharsPerRow`** —
  `contRowChars` clamps to 1 (line 91). Not explicitly
  asserted.
- **`Draw` on a disposed layout** — `_rowRuns[i]?` already null
  after dispose. Should no-op. Not pinned.
- **`Draw` on an empty single row** — `_rowRuns[0]` is null,
  `continue`, nothing drawn. Worth a test.
- **`HitTestPoint` with `local.Y < 0`** — `rIdx` clamped to 0.
  Tested?
- **`HitTestPoint` with `local.X` less than the hanging
  indent X offset** — `localX` clamps to 0, returns
  `span.CharStart`. That's "click in the hanging indent area
  of a continuation row → caret at start of continuation."
  Is that right? The user may expect it to go to the visual
  row above. Worth a comment.
- **`HitTestTextRange`** with a range that straddles the
  hanging indent (selection on a wrapped continuation row).
  The rect math uses `span.XOffset` which is correct, but
  there's no test that exercises it.
- **`HitTestTextRange` when `rangeEnd == Text.Length` on a
  wrapped line** — the "inclusive trailing edge of last row"
  branch at line 244. Edge case worth pinning.
- **`RowForChar(Text.Length)`** — walks past all rows and
  returns `Rows.Length - 1`. Expected; not asserted.

## Architectural concerns

- **Per-row GlyphRun allocation in the constructor** (line 57-66)
  — but cached for the layout's lifetime. This fixed a prior
  memory-pulse-during-caret-blink bug. The Key invariant:
  `MonoLineLayout` is not reused across layouts.
- **`NextRow` mirrors `WpfPrintService.PlainTextPaginator.NextRow`**
  — the comment says so. Duplicated logic that could drift.
  Shared helper.
- **Word-break scan is backward from hard limit** — O(rowWidth)
  per row. Fine for today's sizes.
- **`_rowRuns` is nullable-per-entry to handle empty rows** —
  the `null` check at draw time is the per-row cost. Could
  use a no-op GlyphRun sentinel instead; not worth it.
- **`Text` is owned by this instance** (comment at line 22).
  `ReadOnlyMemory` slices for TextLayout would avoid the
  per-line string allocation, but Avalonia's GlyphRun
  constructor may not accept them. Worth checking.

## Bugs / hazards

1. **`HitTestTextRange` has a subtle condition at line 244:**
   `if (hi <= lo && !(r == Rows.Length - 1 && rangeEnd ==
   Text.Length)) continue;` — when the range ends exactly at
   end of text, the last row is always yielded even if empty,
   producing a zero-width rect at end. Without this, caret
   highlight wouldn't extend to EOL on the last row. Correct
   but hard to read.
2. **`HitTestPoint` with `local.Y` past the last row** clamps
   `rIdx` to last row, then uses `local.X - span.XOffset`. On
   a continuation row with hanging indent, clicking past the
   right edge returns `span.CharStart + span.CharLen` which
   could be the whole-line length minus the indent. Correct.
3. **`Dispose` nulls `_rowRuns[i]` individually**. Double-dispose
   is safe. Good.
