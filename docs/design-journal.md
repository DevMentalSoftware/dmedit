# DevMentalMD Design Journal

Chronological record of design decisions, user requirements, and architectural direction.
Append-only — new entries go at the bottom. Each entry is dated and summarized so that
a fresh Claude session can recover full context by reading this file.

---

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
`%APPDATA%\DevMentalMD\recentfiles.json`. Developer mode (`#if DEBUG` or env var
`DEVMENTALMD_DEV=1`) injects three `ProceduralBuffer`-based sample documents
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

## 2026-02-26 — Variable line heights (design discussion)

### Sources of variable height

With styled markdown rendering, line height is no longer constant. Contributing factors:

1. **Heading styles** — H1–H6 use different font sizes and therefore different line heights.
   Block-level margins above/below headings add further variation.
2. **Word wrapping** — a single logical line may span multiple visual lines depending on
   content length and wrap width. This is the most volatile factor because it depends on
   `content × wrapWidth`, not just content alone.
3. **Block-level spacing** — different vertical margins for different block types (paragraphs,
   headings, code blocks, list items). Even without word-wrap, total height ≠ `lineCount × constant`.
4. **Code blocks** — different font (monospace) and size, plus padding and background.
5. **Lists with nesting** — tighter line spacing, indentation reducing effective wrap width.
6. **Images** — fixed rendered height (possibly scaled to fit width), no scrollbar needed.
7. **Tables** — potentially complex row heights; may warrant their own inner scrollbar (see below).

### Current assumption and its limits

The windowed layout and scrollbar currently assume uniform line height:
```
extent = lineCount × lineHeight
topLine = scrollOffset / lineHeight
```
With variable heights, scroll-position-to-content mapping requires cumulative heights
(prefix sums), which requires knowing every block's height — defeating windowed layout
if computed eagerly.

### Height estimation strategy

Two-tier approach:

1. **Fast O(1) estimate per block** (no TextLayout) — use character count, font metrics
   from the style sheet, and wrap width:
   - Monospace: `ceil(charCount × charWidth / wrapWidth) × lineHeight` (exact)
   - Proportional: same formula with `avgCharWidth` (estimate, usually ±1 visual line)
   - Add block-type-specific margins from the style sheet

2. **Actual TextLayout only for visible blocks** — during rendering, compute true height.
   If it differs from the estimate, patch the height index. The scrollbar thumb adjusts
   subtly as the user scrolls through new territory.

### Height index: Fenwick tree

A flat prefix-sum array requires O(n) updates when one block's height changes.
A **Fenwick tree (Binary Indexed Tree)** gives O(log n) point update, O(log n) prefix
query. Two parallel trees:

- **CharLengthTree** — prefix sums of each block's character length.
  Binary search → "which block contains character offset P?" in O(log² n).
- **HeightTree** — prefix sums of each block's estimated height.
  Binary search → "which block is at scroll position Y?" in O(log² n).

On edit at character position P:
1. Search CharLengthTree → find block B. O(log² n)
2. Update B's char length. O(log n)
3. Re-estimate B's height (O(1)). O(log n) HeightTree update.
4. Done. Nothing above B is touched. Nothing below B is recomputed.

### Incremental update rule (hard design rule)

**An edit must only invalidate the block it touches, never anything above it.**
Blocks above the edit point are guaranteed unchanged. Blocks below have the same
individual heights — only their cumulative positions shift, which the Fenwick tree
handles implicitly.

### Nested scrollbars for tall blocks

Code blocks and tables that exceed a configured `maxVisibleLines` render with their
own inner scrollbar. The block's contribution to the document-level HeightIndex is
the capped height, not the full content height.

Scrolling convention: normal wheel scrolls the document; **Ctrl+wheel scrolls the
inner block** when the cursor is over a capped block. A subtle visual indicator
(thin scrollbar or gradient fade) signals that the block is scrollable.

This avoids a single 500-line code block distorting the document-level scroll geometry.

### Wrap modes

The wrap boundary is a **pixel width**, not a character column. Avalonia's TextLayout
handles word-first line-breaking with character-level fallback for long words (URLs,
paths). It never splits mid-glyph.

Three modes (user setting):

1. **Fixed width** (default) — wrap at a configured pixel width (e.g., 720px), centered
   in viewport. Heights are content-stable and survive window resize. The style config
   expresses this as a character count (e.g., 80); it is converted to pixels using the
   body font's character width.
2. **Window width** — wrap at viewport edge. Heights recompute on resize.
3. **No wrap** — horizontal scroll instead. Heights are minimal (1 visual line per
   logical line, ignoring content length).

For large documents, auto-downgrade from window-width to fixed-width (or no-wrap) is
a safety valve to avoid expensive full-reflow on resize.

Fixed-width wrapping is strongly preferred because the height cache is stable across
resize events — only content edits invalidate it.

## 2026-02-26 — WYSIWYG block tree model (key architectural decision)

### Core principle: Markdown is an import/export format, not an editing format

The editor is **WYSIWYG** — users manipulate a structured block tree, like a word
processor. They never type raw markdown syntax. This is the single most important
architectural decision so far; it touches everything.

- Users don't type `#` to create a heading. They use a formatting command
  (toolbar button, keyboard shortcut, context menu) to change a block's type.
- Users don't type `` ``` `` to create a code block. They create a code block
  (empty or from a selection) and type *into* it.
- Users don't type `**` for bold. They select text and apply bold.
- Block structure is always valid by construction. Malformed/ambiguous markdown
  states cannot occur.

Markdown is the **file format** for Open / Save / Save As — essentially an
import/export codec. The user never sees or edits raw markdown.

### What this simplifies

- **No incremental markdown parsing** during editing. The tree IS the document.
- **No structural edit hazards.** Users can't accidentally break block structure
  by typing characters. Block-type changes are explicit, atomic operations.
- **Height invalidation is trivial.** An edit within block B can only affect B's
  height. Mark B dirty, re-estimate, one Fenwick update. No re-parsing.
- **No markdown ambiguity.** `*foo*` as italic vs. list item is never a question —
  the tree knows the block type and span type.

### Document model: two-level tree

```
BlockDocument
  └── Block[]
        ├── Type: H1 | H2 | … | H6 | Paragraph | CodeBlock | ListItem |
        │         Table | BlockQuote | HorizontalRule | Image | …
        ├── Content: text with inline spans
        │     └── Span[] — bold, italic, code, link, image-ref
        │           Each span: { type, startOffset, length }
        └── Children? — for nesting (list items containing sub-blocks,
                        blockquote containing paragraphs, etc.)
```

Two levels:
1. **Block level** — heading, paragraph, code block, list item, table cell,
   blockquote, horizontal rule, image.
2. **Inline level** — bold, italic, inline code, link, image reference.
   Nested spans within a block's text content. Structurally guaranteed
   valid (applied via commands, not raw typing).

### Editing API

Editing operations are scoped to a single block:
- `block.InsertText(localOffset, text)` — insert characters within a block
- `block.DeleteText(localOffset, length)` — delete characters within a block
- `block.ApplySpan(type, start, length)` — add inline formatting
- `block.RemoveSpan(type, start, length)` — remove inline formatting

Structural operations change the tree:
- `doc.ChangeBlockType(blockIndex, newType)` — e.g., paragraph → H2
- `doc.InsertBlock(index, type)` — add a new empty block
- `doc.DeleteBlock(index)` — remove a block
- `doc.SplitBlock(blockIndex, offset)` — Enter key in a paragraph
- `doc.MergeBlocks(blockIndex)` — Backspace at start of a block
- `doc.WrapInCodeBlock(startBlock, endBlock)` — wrap selection
- `doc.IndentListItem(blockIndex)` / `doc.DedentListItem(blockIndex)`

### Per-block text storage

For small blocks (headings, short paragraphs): plain `string` is fine.
For large blocks (long code blocks, massive paragraphs): a `PieceTable` per
block provides efficient insert/delete and undo history.

The threshold can be dynamic — start with a string, promote to PieceTable if
the block's content exceeds a size threshold during editing.

### Undo/redo

Two layers:
- **Text-level undo** within a block (character insert/delete, span changes) —
  could use PieceTable's existing edit history, or a separate command stack.
- **Tree-level undo** for structural changes (block type change, block
  insert/delete, split/merge) — a separate command stack on the document.

Both layers compose into a single user-facing undo stream.

### Internal persistent format

Since markdown is import/export, the working storage format can be more efficient:

```json
{ "version": 1,
  "blocks": [
    { "type": "h1", "text": "Title", "spans": [] },
    { "type": "p", "text": "Some text with bold",
      "spans": [{ "type": "bold", "start": 15, "len": 4 }] },
    { "type": "code", "language": "csharp", "text": "var x = 1;" }
  ]
}
```

Advantages over markdown-as-working-format:
- No parse ambiguity — block types and spans are explicit.
- Fast to load — no markdown parser needed for internal files.
- Supports efficient diffing for version checkpoints.
- Could be stored compressed for large documents.
- Round-trip fidelity — no information loss through markdown serialization.

### Style system (JSON-based, user-editable later)

A `styles.json` file maps block types to rendering properties:
- Per-type: `fontSize`, `fontWeight`, `fontFamily`, `lineHeight`, `marginTop`,
  `marginBottom`, `padding`, `maxVisibleLines` (for capped blocks), etc.
- The renderer and height estimator both read from this same style definition.
- Background rendering step caches computed font metrics (line heights, char widths)
  per style, so height estimation is O(1) per block without touching the layout engine.
- First pass: JSON file in app data. Later: user-facing style editor UI.

### What happens to existing code

The current PieceTable / IBuffer / ProceduralBuffer infrastructure works for the
plain-text milestone. The block tree is a new layer built alongside. Transition plan:

1. **Current state** — plain-text editing, custom scrollbar, windowed layout. All working.
2. **Next** — introduce block model + style system. Render blocks with per-type styling.
   Editing stays within blocks (no inline spans yet).
3. **Then** — inline formatting spans, toolbar/shortcuts for bold/italic/code/link.
4. **Then** — markdown import (parse .md into block tree) and export (serialize block
   tree to .md). The current PieceTable becomes the import buffer.
5. **Then** — internal format for native save/load (faster, lossless).

### Caret and focus model

Since editing is scoped to a block, the caret lives within a specific block. Navigation:
- Arrow keys within a block: normal text movement.
- Arrow up/down at block boundary: move caret to adjacent block.
- Tab / Shift+Tab in list items: indent/dedent.
- Enter in a paragraph: split block at caret.
- Backspace at start of block: merge with previous block (if types are compatible).
- Click: place caret in the clicked block at the clicked character position.

The EditorControl needs to know which block is focused and where the caret is within
that block, rather than tracking a single global character offset.
