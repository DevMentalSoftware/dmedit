## 2026-02-26 — Milestone 1 (complete)

Custom text engine on raw `DrawingContext`. Piece-table from scratch. Working plain-text
editor with cursor, insert/delete, keyboard nav, undo/redo, selection, click-drag.
33 Core + 21 Rendering tests. All offsets originally `int`.

## 2026-02-26 — Milestone 2 (complete)

Large-file support via `IBuffer` abstraction. Promoted all offsets `int` -> `long`.
`ProceduralBuffer` (stride-1000 lazy skip-list), `LazyFileBuffer` (mmap UTF-16 LE),
`FileLoader`/`FileSaver`, File menu wiring. 48 Core + 21 Rendering tests.

Key design decisions:
- `WholeBufSentinel = long.MaxValue` for O(1) PieceTable(IBuffer) construction
- `EagerIndexThreshold = 10M chars` — above this, line-start cache is not built
- `LayoutResult.ViewportBase` — `long` field for future windowed rendering
- `EditorControl` translates between document `long` and viewport-relative `int`

## 2026-02-26 — Recent Files + Dev Mode

Added `File -> Open Recent` submenu. Persists up to 10 real file paths in
`%APPDATA%\DMEdit\recentfiles.json`. Developer mode (`#if DEBUG` or env var
`DMEDIT_DEV=1`) injects three `ProceduralBuffer`-based sample documents
(100 / 10K / 1M lines) into the Recent menu for interactive testing.

Files: `Services/RecentFilesStore.cs`, `Services/DevMode.cs`, `Services/DevSamples.cs`.

## 2026-02-26 — Windowed Layout (ILogicalScrollable)

Opening the 1M-line dev sample was slow because `EnsureLayout()` and `MeasureOverride()`
called `doc.Table.GetText()`, materializing the entire document.

Fix: `EditorControl` now implements `ILogicalScrollable`. For documents with
`LineCount > 500`, only the visible window of text is fetched and laid out.
`PieceTable.LineStartOfs` delegates to `IBuffer.GetLineStart` for unedited documents.
`TextLayoutEngine.Layout` accepts a `viewportBase` parameter.

Known limitations (deferred):
- Editing in a large windowed document is unsupported (line-start cache can't be built
  after edits for docs > EagerIndexThreshold)
- Scrollbar position is approximate (based on `lineCount * lineHeight` estimate;
  doesn't account for word-wrap variation)
- No "scroll to caret" — if the user scrolls away from the caret, it disappears
  until they scroll back

## 2026-02-26 — Two-Zone Custom Scrollbar (design spec)

### Problem

In a traditional scrollbar, the thumb position maps proportionally to document position.
For a 1M-line document in a 600px scroll track, 1px of thumb drag ≈ 1,667 lines. This
makes the scrollbar useless for *browsing* — you either sit still or jump thousands of
lines. The only practical scroll interaction becomes the mouse wheel.

### Solution: dual-zone thumb

The scrollbar thumb has two concentric zones:

1. **Inner thumb** (traditional) — position and drag behavior are proportional to the
   full document. Useful for jumping to a rough position ("go to ~40% of the way
   through"). Behaves exactly like a normal scrollbar thumb.

2. **Outer thumb** (extensions above and below the inner thumb) — dragging scrolls at a
   **fixed rate** (lines per pixel of drag), independent of document size. This gives
   the user a "scan through content" gesture that stays controllable at any document
   size:
   - Dragging slowly: content scrolls at a readable pace, a few lines per pixel.
   - Dragging quickly: pages fly by but each is briefly visible — you can still spot
     what you're looking for.
   - The inner thumb barely moves during an outer-thumb drag in a large document,
     because the fixed-rate scroll only covers a small fraction of the total.

### Thumb sizing and zone activation

The outer zones only appear when the document is large enough that the inner thumb
would shrink below a minimum threshold. The transition is:

1. **Small/medium doc**: inner thumb is proportionally sized (normal scrollbar).
   No outer zones at all.
2. **Large doc (inner thumb hits minimum)**: total thumb becomes a fixed pixel
   height, divided into 2–3 zones. The thumb stops shrinking.
3. **At document extremes**: the unused outer zone's space is given to the inner
   thumb (bigger click target). At the top → no outer-top, inner is larger.
   At the bottom → no outer-bottom, inner is larger. In the middle → all three
   zones share the fixed total height.

All zones should be compact — just large enough to click and drag accurately.

### Visual model

```
  Small doc (no zones)     Large doc, middle      Large doc, at top
  ┌───────────┐            ┌───────────┐          ┌───────────┐
  │           │            │           │          │           │
  │           │            │ outer-top │          │           │
  │           │            ├───────────┤          │           │
  │   thumb   │            │   inner   │          │   inner   │
  │           │            ├───────────┤          │  (larger) │
  │           │            │outer-btm  │          ├───────────┤
  │           │            │           │          │outer-btm  │
  └───────────┘            └───────────┘          └───────────┘
```

### Drag behavior

- **Inner thumb drag**: proportional — maps to document position like a normal
  scrollbar thumb.
- **Outer thumb drag (fixed rate)**: target feel should match dragging a normal
  proportional thumb on a small/medium document (~100 lines). Exact rate TBD
  empirically.
- **Only the grabbed half drags**: clicking outer-bottom and dragging down moves
  only the outer-bottom away from the inner thumb. The outer-top stays put
  (and vice versa).
- **Snap back on release**: while dragging, the grabbed outer half visually
  separates from the inner thumb within the scroll track. On mouse-up, it snaps
  back to surround the inner thumb at its (now updated) position.

### Resolved design questions (2026-02-26)

- **Visual styling**: outer zones drawn in a slightly different color — bluish tint
  when the inner thumb is gray. Subtle enough to not distract, clear enough to
  distinguish. Inner = standard gray (`#C0C0C0`), Outer ≈ `#B0B8D0`.
- **Track click**: normal page up/down behavior. "Page" = what fits in the
  viewport window, rounded down so a partial line at the bottom becomes fully
  visible.
- **Keyboard Page Up/Down**: standard page scroll (same as track click).
- **Minimum sizes**: inner thumb = same height as the scroll arrow buttons
  (17px at default width). Each outer zone = half that (≈ 8.5px). Total minimum
  thumb when zones are active = 2 × arrow height (≈ 34px).
- **Fixed scroll rate**: should feel like dragging a proportional thumb on a ~100-line
  document. Computed dynamically from viewport and line height.

### Architecture: replacing ScrollViewer

The built-in Avalonia `ScrollViewer` wrapping `EditorControl` will be replaced
by a `Grid` containing the `EditorControl` (column 0) and a `DualZoneScrollBar`
custom control (column 1, fixed 17px width). The `EditorControl` handles mouse
wheel events directly. The `DualZoneScrollBar` reads scroll state from the
editor and fires `ScrollRequested` when the user drags or clicks. The
`MainWindow` code-behind wires the two together.

### Deferred related items

- **Background progressive line indexing** — needed so that line-count-based scroll
  position estimation works for edited large documents (currently, editing a large doc
  breaks the line-start cache).
- **Scroll-to-caret** — after typing or arrow-keying, the viewport should follow the
  caret.

