# 20 — Hanging Indent

**Date:** 2026-04-06
**Status:** Implemented (monospace only)

---

## Feature

Wrapped continuation rows of a logical line are offset to the right by
half of one indent column, so wrapped text is visually distinct from the
first row.  With the default 4-column indent, continuation rows shift
right by 2 character cells.

Default on.  Exposed as `HangingIndent` in Settings → Display, right below
`ShowWrapSymbol`.  Currently engages only when the editor font is
monospace and the line qualifies for the GlyphRun fast path — see below.

## WPF print side

Straightforward addition to `WpfPrintService.PlainTextPaginator`:

- `PrintJobTicket.IndentWidth` carries the editor's indent column count
  (default 4).
- Paginator computes `_hangingIndentChars = indentWidth / 2` and
  `_hangingIndentPx = _hangingIndentChars * charWidth`.
- Both `ComputePageBreaks` and `GetPage` reduce the effective
  `charsPerRow` by `_hangingIndentChars` for continuation rows so
  word-wrap stays inside the printable area.  `GetPage` adds
  `_hangingIndentPx` to the row's X when drawing.
- Pagination consistency: `GetPage` walks the line from row 0 even when
  resuming mid-line on a new page — the `NextRow` break positions on a
  continuation row depend on where the previous row ended, so we can't
  shortcut.  `wrapIdx` skips drawing the rows that belong to the previous
  page.
- Edge case: if `_hangingIndentChars >= _safeCharCount` (absurdly wide
  indent on very narrow paper) it clamps so at least one column of wrap
  width remains.

Measured throughput after the change: same 130-135 pages/sec as before
the hanging indent — the per-row offset adds ~nothing to the render cost.

## Avalonia editor side — new monospace fast path

This is the larger change: a new GlyphRun-based rendering path lives
alongside the existing `TextLayout`-based path in `DMEdit.Rendering.Layout`.
The editor now renders monospace lines via direct `DrawGlyphRun` calls
rather than `TextLayout.Draw`.  Hanging indent is one of several features
the new path unlocks; it also makes hit-test pure arithmetic and is the
first step toward the long-term goal of removing `TextLayout` from the
editor path entirely (see *Future* below).

### Data model

- **`MonoLayoutContext`** (new) — shared per-`LayoutResult` state:
  resolved `IGlyphTypeface`, `FontSize`, `CharWidth`, `Baseline`,
  `RowHeight`, `HangingIndentChars`/`HangingIndentPx`, plus an
  ASCII-indexed glyph cache (`ushort[128]`) and a `Dictionary<int, ushort>`
  for out-of-ASCII codepoints.  Engages only when
  `Typeface.GlyphTypeface` resolves and `FontMetrics.IsFixedPitch` is true.
- **`MonoLineLayout`** (new) — per-line fast-path data: the line text, an
  array of `RowSpan(CharStart, CharLen, XOffset)` entries (one per
  wrapped visual row), and a reference to the shared context.
  Implements `Draw`, `GetCaretBounds(charInLine)`, `HitTestPoint(local)`,
  `HitTestTextRange(start, len)` — all pure arithmetic on `CharWidth`
  and the row-span list.  No `TextLayout`, no shaping, no per-call
  allocation beyond one `ushort[]` per row drawn.
- **`LayoutLine`** (modified) — now holds either a `TextLayout?` (slow
  path) or a `MonoLineLayout?` (fast path), exactly one of which is
  non-null.  New instance methods `Render`, `HitTestTextPosition`,
  `HitTestTextRange`, `HitTestPoint` branch internally so callers no
  longer touch `line.Layout` directly.

### When the fast path engages

Per line, independently:

1. The engine successfully resolved a `MonoLayoutContext` at the top of
   the layout pass (typeface has a `GlyphTypeface` and `IsFixedPitch`).
2. `MonoLineLayout.TryBuild` confirms the line contains no control
   characters (tabs, CR, LF, etc.) and every character has a glyph in
   the resolved face.

Tab-bearing lines fall back to `TextLayout` for that line.  The rest of
the document can still use the fast path; it's a per-line decision.
This caps the initial scope — tab handling needs column-aware advance
arithmetic that the fast path does not yet do.

### Word-break wrap

`MonoLineLayout.NextRow` mirrors the same helper in `WpfPrintService`:
scan backwards from the hard row limit for a space, break there, drop
the space from the drawn row, skip it for the next row.  No space ⇒
hard mid-token break.  First-row width is `maxCharsPerRow`; continuation
rows shrink by `HangingIndentChars`.

### Rendering

`MonoLineLayout.Draw` walks its rows and builds one `Avalonia.Media.GlyphRun`
per row from the cached glyph indices, drawn at
`(origin.X + span.XOffset, origin.Y + rowIdx*rowHeight + baseline)`.

### Hit-test arithmetic

`RowForChar(charInLine)` → row index (linear scan; usually 1 row).
`GetCaretBounds(charInLine)` → `Rect(span.XOffset + col * charWidth,
rowIdx * rowHeight, 0, rowHeight)`.  `HitTestPoint(local)` computes
`rowIdx = localY / rowHeight`, then
`col = round((localX - span.XOffset) / charWidth)`.
`HitTestTextRange(start, len)` walks rows and yields one `Rect` per
overlapping row.

### `TextLayoutEngine` and `EditorControl` changes

- `TextLayoutEngine.LayoutLines` takes a new `hangingIndentChars`
  parameter, builds the `MonoLayoutContext` once for the window, and
  for each line tries the fast path before falling back to `TextLayout`.
- `TextLayoutEngine.HitTest`/`GetCaretBounds` route through the new
  path-agnostic `LayoutLine` methods.
- `EditorControl` passes `hangingIndentChars = _hangingIndent && _wrapLines
  && !_charWrapMode ? indentWidth / 2 : 0` when calling `LayoutLines`.
- All 5 direct `line.Layout.X` call sites in `EditorControl` (Draw,
  HitTestTextPosition, HitTestTextRange ×2, DrawWhitespace) now call
  `line.Render` / `line.HitTestTextPosition` / `line.HitTestTextRange`.
- The `DrawWrapSymbols` path is unchanged — it only reads
  `line.HeightInRows` and `line.Row` which are available on both paths.

### Baseline sign convention quirk

Avalonia's `FontMetrics.Ascent` XML doc says "distance above the baseline
in design em size" but the stored sign varies across graphics stacks
(positive in OpenType, negative in Y-down graphics conventions).  We use
`Math.Abs` on the raw value to get a positive pixel distance regardless —
sidesteps a runtime-only surprise.

## Remaining limitations (first pass)

- **Tab-bearing lines fall back to `TextLayout`** and therefore get no
  hanging indent.  Adding tab support to the fast path requires
  column-aware advance tracking in `NextRow` and `Draw`: treat a tab as
  advancing to the next tab stop (`(col / indentWidth + 1) * indentWidth`)
  rather than a single cell.
- **Char widths from `CharacterToGlyphMap` advances vs TextLayout's
  `GetCharWidth`.**  Both measure the same thing through different code
  paths; on a truly monospace font they match, but a sub-pixel drift is
  possible and would cause wrap positions to disagree by one column at
  the margin.  Not observed in practice yet.
- **Font fallback for missing glyphs on an otherwise-mono line** still
  forces that line to the slow path.  Fine for code and prose, not so
  fine for documents sprinkled with box-drawing or emoji.
- **Non-monospace fonts** get no hanging indent at all.  See *Future*.

## Future — proportional fonts done right

The eventual direction (not implemented, noted here so the idea isn't
lost):

1. **Elastic tabstops / column alignment.**  Nick Gravgaard's 2006
   proposal — tabs aren't fixed-width, they act as column-alignment
   markers across adjacent lines.  A region of consecutive lines with
   the same number of leading tabs forms a "column", and that column's
   width is the max of each row's cell width in that column.  Handles
   aligned function arguments, aligned assignments, aligned trailing
   comments, and aligned tabular data without manually padding with
   spaces.  Works beautifully with proportional fonts because cell
   widths are per-row and the alignment columns auto-adjust.
2. **Per-character advance on the draw path.**  Instead of our current
   uniform `_glyphAdvance`, read `GetGlyphAdvance(glyph)` for every char
   and accumulate.  Required for proportional, but also lets monospace
   fonts render kerned variants (ligatures) correctly.
3. **Wider spaces.**  Emacs-style "prettify whitespace" where leading
   indent spaces render wider than body spaces to preserve visual indent
   depth in a proportional font.  Useful when mixed with alignment via
   elastic tabstops.
4. **Delete `TextLayout` / `FormattedText` from the editor path
   entirely.**  Once (1)-(3) work for proportional fonts, every code
   path goes through our own layout engine, and Avalonia's text shaping
   becomes just a glyph source (via `HarfBuzz` or `IGlyphTypeface`).
   The hit-test, caret, and selection math all becomes uniform
   arithmetic on glyph advances rather than calls through two different
   framework APIs (`TextLayout.HitTest…` in Avalonia,
   `FormattedText.Build` in WPF print).  Eliminates the long-deferred
   caret X-offset bug in journal 13, removes an entire class of
   font-fallback surprises, and gives the editor full control over
   rendering decisions.

There's a paper the user remembers but can't place that argues
proportional fonts make sense for source code editing — worth tracking
down before attempting the work.  The Emacs thread that did surface:
variable tab widths + wider leading spaces to preserve alignment.

Scope sequence if revisiting:

1. Tab handling on the monospace fast path (column-aware `NextRow`
   and `Draw`).  Bootstraps the column-width accumulator that
   elastic tabstops need anyway.
2. Per-character advance tracking on the draw path.  Cheap change once
   step 1 exists.
3. Elastic tabstops as a layout-level concept, not a per-line one —
   requires a pre-pass that walks adjacent lines with matching leading
   tab counts and computes shared column widths.
4. Tear out `TextLayout` / `FormattedText` path site by site.

## Files touched

**New:**
- `src/DMEdit.Rendering/Layout/MonoLayoutContext.cs`
- `src/DMEdit.Rendering/Layout/MonoLineLayout.cs`

**Modified:**
- `src/DMEdit.Core/Printing/PrintJobTicket.cs` — `IndentWidth`.
- `src/DMEdit.Windows/WpfPrintService.cs` — hanging-indent in
  `ComputePageBreaks` and `GetPage`, paginator constructor takes
  `indentWidth`.
- `src/DMEdit.Rendering/Layout/LayoutLine.cs` — sum-type (TextLayout
  vs MonoLineLayout), new dispatch methods.
- `src/DMEdit.Rendering/Layout/TextLayoutEngine.cs` — builds
  `MonoLayoutContext`, classifies each line, routes through new
  line methods.
- `src/DMEdit.App/Services/AppSettings.cs` — `HangingIndent` bool.
- `src/DMEdit.App/Settings/SettingsRegistry.cs` — `HangingIndent` row in
  Display section.
- `src/DMEdit.App/Controls/EditorControl.cs` — `HangingIndent` property,
  new field, 5 call sites migrated to path-agnostic `LayoutLine` API,
  `LayoutLines` call passes `hangingIndentChars`.
- `src/DMEdit.App/MainWindow.axaml.cs` — `Editor.HangingIndent` assign
  at startup and in the live-setting dispatcher; `ticket.IndentWidth`
  for print.
