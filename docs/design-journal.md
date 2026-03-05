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

## 2026-02-26 — Block tree implementation + design problems found

### What was implemented

- **FenwickTree** (`Collections/FenwickTree.cs`) — O(log n) point update, prefix sum query,
  and FindByPrefixSum. Used for both character-length and height prefix sums. 16 tests.

- **Block types** (`Blocks/BlockType.cs`, `InlineSpanType.cs`, `InlineSpan.cs`) — Enums for
  block types (Paragraph, H1–H6, CodeBlock, BlockQuote, Lists, HorizontalRule, Image, Table)
  and inline span types (Bold, Italic, InlineCode, Strikethrough, Link). `InlineSpan` is a
  record with Shift/Resize/Overlaps/Contains helpers.

- **Block** (`Blocks/Block.cs`) — Single block with text editing, inline span management,
  split/merge. Span adjustment on text edits (shift, expand, shrink, remove). 40 tests.
  Key design: spans auto-adjust when text is inserted/deleted, and adjacent/overlapping
  spans of the same type merge automatically.

- **BlockDocument** (`Blocks/BlockDocument.cs`) — Ordered block list with parallel Fenwick
  trees for char-length and height prefix sums. Structural operations: InsertBlock,
  RemoveBlock, SplitBlock, MergeBlocks, ChangeBlockType. Lookup: FindBlockByCharOffset
  (O(log n)), FindBlockByScrollPosition (O(log n)), BlockTopY, BlockCharStart. 51 tests.

- **Style system** (`Styles/BlockStyle.cs`, `InlineStyle.cs`, `StyleSheet.cs`) — Per-block-type
  visual properties (font, spacing, colors, max visible lines). Per-inline-type overrides.
  Style-aware height estimation using AvgCharWidth. Font metrics fed back from renderer.
  Default stylesheet with sensible values for all types. 20 tests.

- **BlockLayoutEngine** (`Rendering/Layout/BlockLayoutEngine.cs`) — Lays out a BlockDocument
  range using StyleSheet properties. Each block gets its own Avalonia TextLayout with the
  appropriate font/size/weight. Also feeds actual heights back into the document's Fenwick
  tree and actual font metrics back into the StyleSheet for future estimates.

- **InternalsVisibleTo** added to `DevMentalMd.Core.csproj` for test access to `SplitAt`/`MergeFrom`.
- **Rendering → Core** project reference added for block types and styles.

### Design problems found during implementation

**1. Block separator ambiguity in character offsets**

The char-length Fenwick tree stores each block's text length. A global "document character
offset" is the sum of preceding block lengths. But there is no separator between blocks.
If block 0 has "Hello" (5 chars) and block 1 has "World" (5 chars), characters 0–4 are in
block 0 and characters 5–9 are in block 1. There is no position that means "at the boundary
between blocks."

This works for the `BlockPosition(blockIndex, localOffset)` caret model because the caret
lives inside a specific block. But `FindBlockByCharOffset` is ambiguous at block boundaries:
should offset 5 map to `(0, 5)` or `(1, 0)`? Currently it maps to `(1, 0)` because the
prefix sum for block 0 equals 5.

**Impact**: Low for editing (caret model avoids the problem). Moderate for features that
need a flat character offset (e.g., search-and-replace, text export). May need a separator
concept (+1 per block boundary) if a flat offset API is exposed later.

**2. Structural operations rebuild Fenwick trees in O(n)**

InsertBlock, RemoveBlock, SplitBlock, and MergeBlocks all call `RebuildTrees()` which is
O(n). The FenwickTree supports O(log n) point updates, but not element insertion or removal
— the underlying array would need shifting.

**Impact**: Fine for typical documents (hundreds to low thousands of blocks). Could become
a bottleneck for programmatic transformations that batch many structural changes (e.g.,
importing a 10,000-block markdown file one block at a time). Mitigation: batch structural
changes and rebuild once, or add insert/remove support to FenwickTree via a gap buffer.

**3. Block.Changed uses IndexOf for O(n) lookup**

When a block fires its `Changed` event, `BlockDocument.OnBlockChanged` calls
`_blocks.IndexOf(block)` to find the block's position. This is O(n) per edit.

**Impact**: Noticeable for documents with 10K+ blocks if edits are frequent. Fix: store a
back-pointer from Block to its index (requires updating indices on structural changes), or
use a Dictionary<Block, int>.

**4. Undo/redo not wired to block model**

The existing `EditHistory` (PieceTable-based) doesn't apply to the block model. The design
calls for two layers: text-level undo within a block + tree-level undo for structural changes,
composing into a single user-facing stream. Neither is implemented yet.

**Decision needed**: Should each Block own its own undo stack, or should the document own a
single stack that records all changes? A single stack is simpler for the user (one Ctrl+Z
stream) but requires the document to intercept all block-level edits. A per-block stack is
simpler to implement but creates a confusing UX when undoing across block boundaries.

**Recommendation**: Single document-level undo stack. Each edit (text insert, text delete,
span change, block split, block merge, type change) is a command object pushed onto the stack.
Block text edits are recorded as `(blockIndex, offset, text)` tuples. Structural changes
record enough state to reverse them.

**5. Empty blocks and FindByPrefixSum edge case**

A document with empty blocks (0 chars) creates zero-width entries in the char-length Fenwick
tree. FindByPrefixSum skips zero-width entries because the prefix sum doesn't increase.
This means `FindBlockByCharOffset(0)` returns block 0 even if block 0 is empty and block 1
has content — which is correct. But a sequence of empty blocks makes the mapping meaningless
for intermediate indices.

**Impact**: Low. The caret model doesn't use global char offsets for navigation. The issue
only arises in edge cases like "cursor between two empty blocks." These are rare in practice
and handled by the block-level caret model.

**6. Height estimation is content-length-unaware without font metrics**

The default height estimate (before the renderer feeds back font metrics) uses only block
type, not content length. A 1-character paragraph and a 1000-character paragraph get the
same estimate (24px). This makes the scroll position inaccurate until the renderer has laid
out blocks and updated the Fenwick tree.

**Mitigation**: The `BlockLayoutEngine.LayoutRange()` feeds actual heights back into the
document's Fenwick tree as blocks are rendered. The first time each block is rendered, its
height estimate is corrected. The scrollbar adjusts subtly as the user scrolls through
"uncharted territory." This is the intended design from the height estimation strategy —
it's working correctly, just noting that initial estimates are rough.

**7. Caret model transition is the biggest remaining challenge**

The current EditorControl uses a global `long` character offset for the caret (from the
PieceTable-based Document). The block model uses `BlockPosition(blockIndex, localOffset)`.
Transitioning requires:
- Changing all input handling to scope edits to the focused block
- Enter → SplitBlock, Backspace-at-start → MergeBlocks
- Arrow up/down at block boundary → move to adjacent block
- Click → hit-test to (blockIndex, localOffset) instead of global offset
- Selection model needs to span multiple blocks: (startBlock, startOffset, endBlock, endOffset)
- The existing TextLayoutEngine (global text → layout lines) is replaced by BlockLayoutEngine
  (per-block layouts)

This is a fundamental refactoring that should be done carefully. The existing plain-text
editing must continue to work during the transition (or there should be a clean switchover).

**Recommendation**: The cleanest approach is to build a new `BlockEditorControl` alongside
the existing `EditorControl`, then swap in MainWindow when ready. This avoids a half-working
intermediate state. The new control uses `BlockDocument` + `StyleSheet` + `BlockLayoutEngine`
from the start, with the `BlockPosition` caret model.

### What exists but isn't wired yet

The following are implemented and tested but not yet integrated into the running editor:
- `BlockDocument` + structural operations (Insert/Remove/Split/Merge/ChangeType)
- `StyleSheet` with per-type styles and height estimation
- `BlockLayoutEngine` that produces per-block layouts with proper font/size/weight
- `FenwickTree` for O(log n) scroll-to-block and char-offset-to-block mapping

The next milestone should wire these into a `BlockEditorControl` that replaces the current
plain-text `EditorControl`.

## 2026-02-26 — Block content rules, code blocks, and flat-vs-nested

### Block content rules by type

Not all blocks treat text the same way. The key distinction is how `\n` behaves:

- **Paragraph, Headings (H1–H6)**: Text never contains `\n`. It is a single logical
  line that may become multi-line only through word wrapping. Pressing Enter triggers
  `SplitBlock()`, creating a new block. The Block class should **enforce** this invariant
  for these types (reject `\n` in `InsertText`, strip on `SetText`).

- **CodeBlock**: Text routinely contains `\n`. Each `\n`-delimited segment is an
  independent line. Pressing Enter inserts `\n` into the block's text (does NOT split
  the block). The code block is one structural unit.

- **BlockQuote, ListItems**: Treated as single-line text blocks (like paragraphs) for now.
  See "Flat model with IndentLevel" below for nesting.

### Code block wrapping

Code blocks have a **per-line wrapping** model, distinct from paragraph wrapping:

- Each `\n`-delimited line within the code block is an independent wrap unit.
- Wrapping within a code line is **optional** (user setting, default off for code).
- When enabled, a **wrap indicator glyph** (e.g., `↩` or `⤸`) can be rendered to show
  where wrapping occurred. The glyph sits in the block's left padding / gutter area and
  is allowed to exceed the wrap boundary — it's visual chrome, not content, so it doesn't
  affect text layout or the wrap calculation.
- This means the code block layout path differs from paragraph layout: iterate `\n`-split
  segments, optionally wrap each one individually, stack the visual lines.

### Code blocks as AST (future direction)

A more powerful model stores code blocks not as text but as a parsed AST (tree-sitter
style). Syntax highlighting becomes a rendering of the AST rather than regex-based span
coloring. Benefits:
- Structural editing (fold, refactor, navigate by symbol)
- Accurate highlighting that doesn't break on edge cases
- Jump-to-definition, hover info, etc.

**For now**: store code as text. Apply syntax highlighting as a **separate read-only span
layer** — not `InlineSpan` (which is user-applied formatting), but something like
`SyntaxHighlightSpan` computed on content change, purely for rendering. This design leaves
the door open for AST-backed highlighting later: the text-editing API doesn't change, only
the highlight source switches from regex to tree walks.

### Flat model with IndentLevel (avoiding blocks-within-blocks)

The document model stays **flat** — `BlockDocument` is a list of `Block`, no nesting.
Hierarchical relationships (nested lists, block quotes containing paragraphs) are modeled
with properties on the block rather than tree nesting:

- `Block.IndentLevel` (int, default 0): controls visual indentation and logical grouping.
  A nested list item has `IndentLevel = 1` (or 2, 3…). The renderer increases left padding
  by `indentLevel × indentWidth`.

- Adjacent list items at different indent levels form a visual tree but are structurally
  flat. Tab/Shift+Tab adjusts `IndentLevel`.

- Block quotes are `BlockQuote`-typed blocks that happen to be adjacent. A block quote
  "containing" a code block is just a `BlockQuote` block followed by a `CodeBlock` block,
  both at the same indent level. The renderer draws the quote bar alongside both.

**Edge cases that the flat model handles imperfectly**:
- A list item containing multiple paragraphs: modeled as an `UnorderedListItem` followed
  by a `Paragraph` with a "continuation" flag or matching indent level signaling it belongs
  to the preceding list item.
- A block quote containing mixed content (headings, code): adjacent blocks with a shared
  `isBlockQuoteChild` flag or indent context.

These are minor compromises. True nesting would make the Fenwick trees more complex (tree
of trees), complicate undo/redo, and require a recursive layout engine. The flat model with
IndentLevel keeps everything O(1) per block for editing and O(log n) for lookup, and covers
90%+ of real markdown documents without nesting.

**Reserved for the future**: If the project forks into a full word processor with
multi-column layout or complex nesting, a proper tree model can be introduced then.
The current flat model is the right choice for a markdown-centric WYSIWYG editor.

## 2026-02-26 — WholeBufSentinel bug fix

After inserting a newline into a ProceduralBuffer-backed document and scrolling to the
bottom, lines beyond the edit point were concatenated without separators and text was
truncated mid-word. Root cause: three places assumed a `WholeBufSentinel` piece spans the
entire buffer, but after `Insert()` splits such a piece the right half starts partway in.

Fixes applied in `PieceTable.cs`:
1. `Length` property: `_origBuf.Length` → `_origBuf.Length - p.Start`
2. `GetText()`: same correction — read `bufLen - p.Start` chars, not `bufLen`
3. `VisitPieces()`: clamp to `_origBuf.Length - (p.Start + pieceOfs)` instead of
   blindly reading `remaining` chars

Two regression tests added in `LargeDocumentTests.cs`.

## 2026-02-26 — Persistence architecture and the role of PieceTable

### PieceTable is orphaned by the block model

With the block model, each `Block` owns a small `string _text`. The `PieceTable` was
designed for a single-buffer whole-document world. The block model eliminates that need:
a 100,000-line document is 100,000 blocks, each with a tiny string. The PieceTable, IBuffer,
ProceduralBuffer, LazyFileBuffer, and related infrastructure solve a problem the block model
doesn't have.

### Pristine/dirty blocks (zero-copy loading)

When loading a file, `File.ReadAllText()` produces one `string`. The Markdown parser walks
it and creates blocks, but instead of copying substrings, each block holds a
`ReadOnlyMemory<char>` slice into the original string — zero allocation per block.

A block can be in one of two states:
- **Pristine** — text is a `ReadOnlyMemory<char>` slice of the load buffer. Zero copy.
- **Dirty** — user edited the block. It now owns a mutable `string`.

The first edit to a pristine block "materializes" the slice into a string. The 99% of
blocks the user never touches remain as zero-cost slices. The original string stays alive
via GC references from pristine blocks and is collected when no pristine blocks remain.

This captures the piece table's key insight (original buffer is immutable, edits go elsewhere)
at the block level, without the piece list machinery. A single paragraph is small enough that
once touched, copying to a mutable string is fine.

### File I/O as block streaming

The document should never be materialized as a single string:
- **Load**: `File.ReadAllText()` → one string → parser creates blocks as `ReadOnlyMemory<char>`
  slices → original string is the only allocation
- **Save**: stream block-by-block to a `StreamWriter`. Pristine blocks write their slice
  directly. Dirty blocks write their string. No single allocation of the whole document.
- **`GetText()` returning a full `string` is unnecessary.** Even file export streams
  `ReadOnlySpan<char>` per block.

### Three-layer persistence architecture

| Concern | Mechanism |
|---|---|
| Fast reopen, parsed block cache, editor state | Local persistence store |
| Version history, named checkpoints, collaboration | Git |
| Interchange with other tools | `.md` file |

**Local persistence store**: When opening a `.md` file, the parser creates blocks and
persists the block structure to a local cache (sidecar file or cache directory). A
fingerprint of the `.md` file (size + last-modified, or hash) is stored alongside.

On subsequent opens:
- Fingerprint matches → load from persistence. Blocks are already parsed, spans resolved,
  indent levels set. With memory-mapped access, blocks not on screen don't need to be in
  RAM at all.
- Fingerprint mismatch → re-parse the `.md`, rebuild persistence.

The persistence store is a **cache** — lossy is fine, rebuild from `.md` if corrupted or
missing. It can also store editor state (cursor position, undo history, scroll position,
collapsed sections).

Volante (https://github.com/sergiy/volante) was investigated in prior research as a
high-performance embedded object database for .NET with memory-mapped access. It
outperformed all other persistence mechanisms tested for the goal of avoiding loading
objects into memory. Compatibility with modern .NET needs verification; the concept
applies regardless of the specific library chosen.

**Git for history**: Rather than building custom versioning:
- Auto-commit on save — each Ctrl+S is a git commit (auto-message or user-named)
- History panel — `git log` for the current file with diff preview
- Restore — checkout a previous version of just that file
- Branching — "try a different direction" = git branch

This avoids reinventing diffing, deduplication, and history traversal.

**`.md` as interchange**: The Markdown file is the human-readable, git-friendly,
tool-interoperable format. It's what gets committed, shared, and read by other programs.

### Bundled document format (future)

For sharing documents with embedded resources (images, editor state, history) outside of
git, a zipped format (like `.docx`) could bundle:
- The `.md` file
- Embedded images
- Editor state metadata
- Custom metadata

Low priority — git + `.md` covers most workflows.

## 2026-02-28 — Performance stats panel (Phase 1 complete)

Added a `PerfStats` infrastructure to `EditorControl` for measuring layout and render
performance. `TimingStat` tracks exponential moving average (α=0.1), min, and max.
Stats displayed in a status bar at the bottom of the window (DevMode only), updated
every 5th render frame via `Dispatcher.UIThread.Post` to avoid invalidating visuals
mid-render pass. Also fixed `ClipToBounds` on `EditorControl` — wrapped text was
painting past the control's bounds into the status bar area.

## 2026-02-28 — Status bar design (TODO — deferred)

The status bar infrastructure is in place (Border + TextBlock, docked bottom). Currently
it only shows dev stats. The following items should be added as permanent status bar
segments visible in all modes:

### Left section
- **Caret position**: `Ln 42, Col 17` — line and column of the caret
- **Selection count**: `(42 selected)` — character count when text is selected

### Center section (DevMode only)
- **Perf stats**: Layout/Render timing (EMA + min/max), line counts, scroll %

### Right section
- **Indent style**: `Spaces: 4` or `Tab Size: 4` — clickable to toggle/change
- **Line endings**: `CRLF` or `LF` — clickable to convert
- **Encoding**: `UTF-8`, `ANSI`, etc. — clickable to change
- **Document type**: `Text`, `Markdown`, `XML`, etc. — dropdown, initially just `Text`
- **Zoom**: percentage, TBD

### Design notes
- Inspired by VS Code / Visual Studio status bars
- Each segment should eventually be clickable (opens a picker or toggles the setting)
- Word count may be useful for Markdown mode (writing-focused editors like iA Writer show this)
- File dirty indicator (●) complements the title bar asterisk
- Git branch display deferred — adds dependency, not core to text editing

## 2026-02-28 — Streaming file loader (StreamingFileBuffer)

Replaced `LazyFileBuffer` (mmap, UTF-16 LE only) with `StreamingFileBuffer` — a true
streaming IBuffer that reads files in 1 MB binary chunks on a ThreadPool worker thread.

Key design:
- Pre-allocates `char[]` sized to file byte length (worst-case UTF-8)
- Uses `System.Text.Decoder` for incremental UTF-8 decoding (handles split multi-byte
  sequences at chunk boundaries)
- BOM detection: UTF-8, UTF-16 LE, UTF-16 BE; default UTF-8
- Builds line-start index (`long[]`) incrementally as chunks arrive via `ScanNewlines`
- Thread safety: `volatile _loadedLen` acts as memory barrier; background writes to
  `_data[_loadedLen..]`, UI reads `_data[0.._loadedLen-1]`; `_lineStarts` growth
  protected by `lock`
- Events: `ProgressChanged` (per chunk), `LoadComplete` (when done)
- Integrates with existing `WholeBufSentinel` — PieceTable's `Length` / `VisitPieces`
  call `ResolveLen()` which reads `_loadedLen`, growing seamlessly as chunks arrive

`FileLoader` threshold lowered from 200 MB to 10 MB. Files ≤ 10 MB use
`File.ReadAllText` + `StringBuffer`. Files > 10 MB use `StreamingFileBuffer`.
`LazyFileBuffer.cs` deleted.

Stats bar shows two-part load time for streaming files: "Load: 17.5ms + 553.0ms"
(first renderable chunk + total). Non-streaming files show a single time.

Performance: 185 MB UTF-8 file — first chunk 17.5ms, total 553ms (was a UI freeze).

## 2026-02-28 — Save optimization (two-path FileSaver)

Save was 1034ms for 185 MB (slower than load). Three bottlenecks identified:
1. 64 KB chunk size → 2800 iterations
2. `VisitPieces` WholeBufSentinel path allocated `new char[take]` per chunk
3. `StreamWriter` default buffer is small

Rewrote `FileSaver` with two paths:
- **Fast path** (`SaveFromBuffer`): unedited buffer-backed docs (`IsOriginalContent`),
  reads directly from `IBuffer.CopyTo` in 1 MB chunks, bypasses PieceTable entirely.
  Thread-safe, no PieceTable access. Result: 180ms.
- **General path** (`SaveFromPieceTable`): edited docs, 1 MB chunks with 1 MB
  StreamWriter buffer via `table.ForEachPiece`.

Added `PieceTable.IsOriginalContent` and `PieceTable.OrigBuffer` properties.
Save already uses `Task.Run` (non-blocking UI).

## 2026-02-28 — Fix: UI lockup on Enter key in 185 MB document

After pressing Enter in a 185 MB document, PieceTable splits from 1 piece to 2–3.
The single-piece delegation fast path (`LineCount`, `LineStartOfs`) no longer applies.
`LineCount` saw `Length > EagerIndexThreshold` and returned `-1`. `EnsureLayout()`
interpreted `-1` as "small enough for full layout" and called `GetText()`, materializing
100M+ chars → freeze.

Fix: `PieceTable.LineCount`, `LineStartOfs`, `LineFromOfs`, and `GetLine` now fall back
to the buffer's line index (`_origBuf.LineCount`, `_origBuf.GetLineStart(idx)`) when the
document exceeds `EagerIndexThreshold` and the buffer has a line index. These are
**approximate** (off by ±edit chars) but prevent the freeze by keeping windowed layout
engaged. Added `BinarySearchBufferLines` helper for `LineFromOfs`.

A slight one-time JIT hesitation on the very first edit is expected — .NET compiles the
new buffer-fallback code paths on first execution. Subsequent edits are instant.

## 2026-02-28 — Edit timing stat

Added `TimingStat Edit` to `PerfStatsData` and `_editSw` stopwatch to `EditorControl`.
All edit operations in `OnKeyDown` (Enter, Tab, Backspace, Delete, Undo, Redo) and
`OnTextInput` are instrumented. Stats bar shows "Edit: 0.12ms (0.08–0.45)" after any
edit occurs.

Test count: **216** (195 Core + 21 Rendering).

## 2026-02-28 — Fix: scroll-to-bottom with word wrap

When scrolled to the bottom of a large document with word-wrapping, the last few lines
were clipped below the viewport. Root cause: `LayoutWindowed` maps scroll offset to
`topLine` using a **global** `avgLineHeight`, but lines near the end may wrap more than
average. The actual rendered content is taller than estimated, pushing the last lines
below the viewport.

Fix: after laying out the windowed content, if `bottomLine >= lineCount` (we're at the
end) **and** we're at max scroll (`_scrollOffset.Y >= scrollMax - 1`), check whether the
content bottom extends past the viewport. If so, anchor `_renderOffsetY` to
`_viewport.Height - _layout.TotalHeight` so the last line sits at the viewport bottom.

The `>= scrollMax - 1` guard is critical — without it, the anchor fires continuously
while scrolling up from the bottom (because the fetched lines still include the end),
making scroll-up appear stuck. With the guard, the anchor only engages at actual max
scroll; the first scroll-up click starts moving immediately.

Note: Notepad++ has a much worse version of this same problem with word-wrap enabled —
scrolling to the bottom consistently shows 200–800 lines short of the real end, and the
position is non-deterministic. Our approach is approximate (global-average scroll mapping)
but deterministic, and the bottom-anchor ensures the true end is always reachable.

## 2026-02-28 — Smooth partial-row scrolling with wrapped lines

Windowed layout's formula `_renderOffsetY = topLine * avgLineHeight - scrollOffset` had
discontinuities when `topLine` changed — the render offset jumped by `avgLineHeight` while
scroll only moved by one row height. This caused jitter and whitespace gaps.

Fix: incremental render offset tracking with four cached fields (`_winTopLine`,
`_winScrollOffset`, `_winRenderOffsetY`, `_winFirstLineHeight`). For single-row scrolls
(ds < 2*rh, i.e., arrow button clicks), topLine is constrained to change by ±1 and the
offset is computed using actual cached line heights. Multiple safety clamps prevent top gaps
(`_renderOffsetY > 0`), bottom gaps (content doesn't reach viewport bottom), and resize
instability (incremental state is NOT reset on viewport resize, only on content changes).

## 2026-02-28 — Transparent zip file support

Auto-detect zip files by magic bytes (PK header: `50 4B 03 04`). Single-entry zips are
transparently decompressed and streamed into the existing `StreamingFileBuffer`. Multi-entry
zips are rejected with an informative error.

Key changes:
- `StreamingFileBuffer` gained a second constructor accepting a `Stream` + `IDisposable?`
  owner. Refactored `LoadWorker` and `DetectEncodingAndCreateDecoder` to work with any
  `Stream` (not just `FileStream`). BOM detection handles non-seekable streams by returning
  prefetched bytes.
- `FileLoader` now returns `LoadResult(Document, DisplayName, WasZipped)` instead of raw
  `Document`. Added `IsZipFile()` helper and `LoadZip()` for the zip path. Small zips
  (≤ 10 MB uncompressed) read entirely into memory; larger ones stream via
  `StreamingFileBuffer`. The `ZipArchive` is owned by the buffer and disposed when loading
  completes.
- `MainWindow` updated to use `LoadResult.DisplayName` for the title bar (shows
  "archive.zip → inner.txt" for zips). `SaveAs` suggests the inner entry name for zipped
  sources.
- Save always writes plain text (no re-zipping).

13 new tests in `ZipFileTests.cs`. Test count: **229** (208 Core + 21 Rendering).

## 2026-02-28 — Memory usage in stats bar

Added `MemoryMb` and `PeakMemoryMb` fields to `PerfStatsData`, sampled via
`GC.GetTotalMemory(false)` each render frame. Stats bar now shows
"Mem: 245 MB (max 312 MB)" alongside load/save timing. Peak tracks the session maximum.

## 2026-02-28 — Future features noted in development plan

Two future milestones documented in the plan file:

1. **Open Folder** — folder browsing with metadata database, text/md file searching,
   multi-file zip support, and nested zip/folder handling.

2. **Memory stats** — implemented this session (see above).

## 2026-02-28 — Memory-efficient large file support (PagedFileBuffer)

Three-phase project to achieve sublinear memory scaling for large files.

### Phase 1 — Eliminate `_origBufCache` doubling

`PieceTable.BufFor(BufferKind.Original)` previously materialized the entire `IBuffer`
into a cached string on the first edit. For a 185 MB file that meant +370 MB on the
first keystroke. Removed `_origBufCache`; `VisitPieces`, `CharAt`, and `BuildLineStarts`
now access `_origBuf` directly. Trade-off: more transient allocations per read (small
`char[]` each time) but no persistent doubling. Allocation budget tests raised accordingly.

### Phase 2 — `PagedFileBuffer`

New `IBuffer` implementation (`Buffers/PagedFileBuffer.cs`, ~637 lines) that keeps only
a bounded LRU cache of decoded text pages in memory. The raw file stays on disk, locked
with `FileShare.Read`.

Architecture:
- **Background scan**: reads file in 1 MB chunks, decodes via `Encoding.GetDecoder()`
  (handles multi-byte boundaries), builds `PageInfo[]` (byte↔char offset mapping) and
  a sampled line index (every 1024th line's char offset).
- **LRU cache**: `LinkedList<int>` + `Dictionary<int, LinkedListNode<int>>` for O(1)
  promote/evict. Default `MaxPagesInMemory = 8` ≈ 16 MB decoded cache.
- **On-demand reload**: evicted pages re-read from disk via `FileStream.Seek` + decode
  (~1 ms per page on SSD). `lock(_fs)` ensures seek+read atomicity.
- **BOM detection**: UTF-8, UTF-16 LE, UTF-16 BE, or no-BOM (assumes UTF-8).
- **`IBuffer` additions**: `IsLoaded(offset, len)` and `EnsureLoaded(offset, len)`
  (default `true`/no-op in `IBuffer`) for placeholder rendering support.
- **Thread safety**: `Interlocked.Read`/`Exchange` for `_lineCount` and `_totalChars`
  (C# forbids `volatile long`); `lock(_lock)` for `_pageData`, LRU, and `_lineSamples`.

Memory budget (928 MB file, ~5M lines, MaxPages=8):
  PageInfo[] ≈ 30 KB, decoded cache ≈ 16 MB, line index ≈ 40 KB → **~16 MB total**.
  User-tested: MaxPages=3 → 42 MB, MaxPages=8 → ~60 MB, MaxPages=128 → 500 MB.
  No perceptible scrolling delay at any cache size on SSD.

24 new tests in `PagedFileBufferTests.cs`.

### Phase 3 — FileLoader integration + placeholder rendering

Three-tier file routing in `FileLoader` (configurable via `AppSettings.PagedBufferThresholdBytes`):
  ≤ 10 MB → `File.ReadAllText` (string)
  10 MB–50 MB → `StreamingFileBuffer` (~2× memory)
  > 50 MB → `PagedFileBuffer` (bounded ~16 MB)

Zip streams always use `StreamingFileBuffer` (non-seekable, can't re-read pages).

`EditorControl` checks `IsLoaded()` before fetching text in `LayoutWindowed`. When
pages aren't loaded, `EnsureLoaded()` queues thread-pool loads and grey rounded-rectangle
placeholders render in place of text. `ProgressChanged` fires per page load, triggering
`InvalidateLayout()` which replaces placeholders with real text.

`MainWindow.WireStreamingProgress` now handles `PagedFileBuffer` alongside
`StreamingFileBuffer` for progress bar updates during the initial scan.

Test count: **253** (232 Core + 21 Rendering).

## 2026-02-28 — Undo selection restoration

Previously undo restored a computed caret position (e.g., end of deleted text for an insert
undo). Now undo restores the **exact pre-edit selection** including anchor/active direction.

`EditHistory` was rewritten to store `Entry(IDocumentEdit Edit, Selection SelectionBefore)`
pairs. `Push(edit, table, selBefore)` captures the selection before each edit. Compound
groups capture the first push's selection as `_compoundSelBefore`.

`Document.cs` updated: all `_history.Push()` calls pass `Selection` as third argument.
`Undo()` restores `result.Value.SelectionBefore` instead of computing `CaretAfterUndo`.
`Redo()` still uses `CaretAfterRedo(result.Value.Edit)`. `CaretAfterUndo` method removed.

## 2026-02-28 — I-beam cursor

Set `Cursor = new Cursor(StandardCursorType.Ibeam)` in `EditorControl` constructor so the
mouse cursor shows the text insertion beam when hovering over the editor area.

## 2026-02-28 — Middle-button scroll from editor area

Middle-button drag scrolling moved from scrollbar-only to the entire editor surface. Fixes
a Linux issue where middle-dragging on the scrollbar triggered the window manager's
"move window" gesture.

**EditorControl changes:**
- `ScrollBar` property + `_middleDrag` / `_middleDragStartY` fields
- `OnPointerPressed`: middle-click captures pointer, hides cursor (`StandardCursorType.None`),
  calls `ScrollBar.BeginExternalMiddleDrag()`, marks `e.Handled = true`
- `OnPointerMoved`: middle-drag branch computes deltaY, forwards to scrollbar
- `OnPointerReleased`: restores IBeam cursor, calls `EndExternalMiddleDrag()`, releases
  capture, resets caret blink

**DualZoneScrollBar changes:**
- Three public methods: `BeginExternalMiddleDrag()`, `UpdateExternalMiddleDrag(deltaPixels)`,
  `EndExternalMiddleDrag()` — drive the scrollbar's existing `HandleDragMove` logic from
  external delta-pixel input
- `IsDragging` property (read-only) — true during any drag (thumb, outer, or external middle)
- `InteractionEnded` event — fired at end of `OnPointerReleased`

**MainWindow wiring:**
- `Editor.ScrollBar = ScrollBar` in `WireScrollBar()`
- `ScrollBar.InteractionEnded += () => Editor.ResetCaretBlink()`

## 2026-02-28 — Caret hiding during scroll

The caret is hidden during all forms of scrolling and restored when scrolling stops.

**Approach (after multiple iterations):**
- `ScrollValue` setter: sets `_caretVisible = false` on every scroll change (doesn't
  touch the timer — that caused various re-show bugs)
- Render guard: `!_middleDrag && !scrollDrag` suppresses caret drawing during drags
- `OnCaretTick` / `ResetCaretBlink`: early-return if `_middleDrag` is true
- `InteractionEnded` event on scrollbar → `ResetCaretBlink()` for immediate caret on
  drag/arrow release
- Middle-drag on editor → `ResetCaretBlink()` in `OnPointerReleased`
- Mouse wheel: timer naturally recovers within 530ms (no special handling needed)

**Key lesson:** `DispatcherTimer` can have stale queued ticks that fire after `Stop()`.
During drags the timer toggles `_caretVisible` behind the render guard, so when the drag
ends `_caretVisible` is in a random state. Solution: use the render guard for suppression,
not timer manipulation, and fire `ResetCaretBlink()` on interaction end for deterministic
recovery.

## 2026-02-28 — Selection rounded corners

Selection rectangles now have individually rounded corners (3px radius) at exposed edges.

**Single-row selections** get all 4 corners rounded.

**Multi-row selections** get per-corner rounding based on whether each corner is "exposed"
— i.e., the adjacent rect doesn't cover it. The rule:
- TL: rounded when no rect above, or above.Left > cur.Left (above doesn't cover left edge)
- TR: rounded when no rect above, or above.Right < cur.Right (above doesn't cover right edge)
- BL: rounded when no rect below, or below.Left > cur.Left
- BR: rounded when no rect below, or below.Right < cur.Right

This correctly rounds corners at "steps" where selection lines have different lengths, while
keeping interior corners sharp where adjacent rects meet flush.

Implementation uses `StreamGeometry` with `ArcTo` for per-corner control. A `FillRoundedRect`
helper draws the shape; rects with no rounded corners fall back to `ctx.FillRectangle`.

**Bug fixed:** Initial implementation rounded corners whenever adjacent rects had *different*
edges (using `Math.Abs(diff) > 0.5`). This incorrectly rounded interior corners — e.g.,
line 1's bottom-left was rounded even though line 2 extended further left and fully covered
it. Fixed by checking whether the adjacent rect *fails to cover* the corner (directional
comparison instead of absolute difference).

## 2026-03-02 — Feature backlog

Collected feature ideas for future work, in no particular priority order. Organized by
category for reference. Individual items will get their own dated journal entries when
implementation begins.

### Configuration

- [x] **DevMode via appsettings** — move DevMode toggle from environment variable
  (`DEVMENTALMD_DEV`) to appsettings. Default to enabled for now.

### Editing commands

- [x] **Line Delete** — delete the entire current line. Bind to `Ctrl+Y` by default.
- [x] **SelectWord** — select the word under the caret. Bind to `Ctrl+W` by default.
- [ ] **ExpandWord** — appsetting that makes SelectWord expand incrementally following
  common conventions (camelCase subwords, underscore separators, dash separators)
  instead of selecting the whole word at once.
- [x] **Case commands** — Upper, Lower, Proper case transforms on the selection.
- [x] **Alt+Arrow line move** — `Alt+Up`/`Alt+Down` moves entire selected lines up or
  down, reflowing the document around them. Partial line selections move the whole line.
  Moves by logical lines, not visual rows — so Alt+Up might jump 3 rows if the line
  above wraps to 3 rows.

### Indentation

- [ ] **Smart tab** — appsetting where Tab moves the caret to a predefined indent
  (default 4 spaces) relative to the start of the previous line, not the current row.
  When disabled, Tab indents further right each press. Example: if the previous line
  starts at column 1 and the caret is at column 51, Tab jumps to column 5.

### Display

- [x] **Line numbers** — appsetting to show line numbers in a non-editable gutter on the
  left. Numbers are right-aligned, no leading zeros. Column width auto-expands to fit
  the digit count for the file's line count. Multi-row wrapped lines show the number on
  the top row only. Width changes may reflow wrapped text since horizontal space changes.
- [ ] **Wrap options** — appsettings for: fixed-column wrap with a spinner (default 100),
  optional thin grey vertical line at the wrap column, wrap-to-window toggle, and a
  master enable/disable wrapping checkbox that preserves the other settings.

### Status bar

- [x] **Full status bar** — fixed bar below the stats bar showing: file size, word count,
  line count, row count (hidden when wrapping disabled), current line, current column,
  file encoding, line ending style (clickable to fix), tab style (spaces or tab chars),
  Insert/Overwrite mode, zoom combo. *(Phase 1 done: Ln/Col, selection count, line count;
  encoding/endings/indent hardcoded pending detection infrastructure.)*

### Toolbar

- [ ] **Optional toolbar** — uses font glyphs from various fonts by default, with optional
  SVG icon support. Exposes a subset of frequently toggled settings as toolbar buttons.

### Undo / Redo UI

- [ ] **Undo/Redo toolbar buttons** — disabled when nothing to undo/redo. When multiple
  actions exist, a down-arrow dropdown shows the N most recent actions. Clicking an item
  undoes/redoes up to that point. A count is shown below the dropdown list.

### Tabbed documents

- [ ] **Tabbed document interface** — support multiple open documents with tabs.

### Persistence

- [ ] **Session persistence** — persist editor session to
  `%LOCALAPPDATA%\DevMental\DMEdit\session.db` using VersantDB (requires a separate
  project to build Versant from source). Persist enough to recreate full session state:
  undo/redo history, tabs, caret positions, scroll positions, and all user-visible state.
  Goal: survive process crash or machine reboot with at most a few hundred milliseconds
  of lost changes. No performance impact — persistence runs slightly behind. Closing all
  documents starts a new session. Single-session only for now.

### Docking / panels

- [ ] **Docking pane infrastructure** — support for separate dockable panes (Outline,
  History, Git, etc.).
- [ ] **Outline pane** — for block documents. Shows heading levels 1–N. Mini toolbar for
  level toggles, quick filter, sync-with-editor. Clicking a heading scrolls to that
  location (first line unless already visible). Requires block document model.
- [ ] **History pane** — shows saved versions of the current file (tied to git
  integration). Click an entry to view that version (read-only). Historic versions can be
  opened as editable copies (forked from that point). Variable-height entries with wrapped
  commit messages (option to truncate to one line with tooltip). Option to show diff with
  current/next/previous version. Each entry may list changed files (hideable).

### Git integration

- [ ] **Blame column** — displayed left of line numbers. Shows brief author + date info
  per line, color-coded by age. Clicking opens the History pane scrolled to that change.
  Can collapse to just the color strip (details in tooltip). Uncommitted changes shown as
  if committed at current time. Context menu to jump to previous version. Optional
  narrow color strip in the scrollbar (widens it by a pixel or two) showing blame info
  for the whole document; can optionally show only current edits.

### Settings

- [ ] **Settings page** — a dedicated document type with a long scrollable list of all
  user settings. Search/filter to find settings. Always available as a right-aligned tab
  with a gear icon. All appsettings should be represented here.
- [ ] **Keyboard mapping settings** — assign keyboard shortcuts to any editor command.
  Dropdown to select a base mapping from common editors. Commands divided into categories.
  Non-default bindings (those differing from the selected base) shown at the top. Users
  can save custom mappings that retain the base mapping reference.
- [ ] **Combination keyboard shortcuts** — support both standard shortcuts (all keys held)
  and two-step combo shortcuts (first shortcut opens a context, second completes the
  action). Example: `Ctrl+R` opens a refactor menu, then a letter or Ctrl+letter combo
  selects within it. `Ctrl+R, R` differs from holding `Ctrl` and pressing `R` twice.
  Options: checkbox to simplify by mapping all combos to the same command; option to show
  or hide the combo menus; active shortcut shown in status bar (far left, clickable to
  show menu, optionally translucent).
- [ ] **Export/import settings** — export setting changes to a file for sharing or import
  (JSON or XML format TBD).

## 2026-03-02 — Editing commands, DevMode appsetting, and status bar

Six features implemented in one session. All editing commands live in `Document.cs` with
full undo/redo support. 34 new tests in `DocumentTests.cs`.

### DevMode via appsettings

`DevMode.IsEnabled` is no longer a static readonly init. It is set by an explicit
`DevMode.Init(AppSettings)` call in the `MainWindow` constructor, before any wiring
methods run. `AppSettings.DevModeEnabled` (default `true`) controls the flag in Release
builds. DEBUG builds always return `true`. The `DEVMENTALMD_DEV` env var was removed.

### Line Delete (Ctrl+Y)

`Document.DeleteLine()` finds the caret's line via `Table.LineFromOfs()`, deletes from
line start to next line start (including newline). For the last line, eats the preceding
newline to avoid a trailing blank. Single undo operation. Replaces the `// TODO` in
`EditorControl.OnKeyDown`.

### SelectWord (Ctrl+W)

`Document.SelectWord()` classifies the character under the caret into word chars
(letter/digit/underscore), whitespace, or punctuation, then scans left and right for
the contiguous run. Reads a 1024-char window to avoid materializing large documents.
Handles caret-at-end-of-document.

### Case commands (Ctrl+Shift+U/L/P)

`CaseTransform` enum (Upper, Lower, Proper) in `CaseTransform.cs`.
`Document.TransformCase()` replaces selected text via compound edit (delete+insert).
No-op if selection is empty or already in target case. `ToProperCase()` capitalizes
after whitespace, dash, or underscore.

### Alt+Arrow line move

`Document.MoveLineUp()` / `MoveLineDown()` find the logical line range covered by
the selection, swap with the adjacent line using a compound delete+insert. Handles
the tricky edge case where the last document line has no trailing newline — the newline
transfers between blocks to keep document structure valid. Selection follows the moved
text. Multi-line selections move all covered lines.

`GetSelectedLineRange()` excludes a line if selection ends at column 0 of that line
(matches VS Code behavior).

### Status bar (Phase 1)

The status bar is now always visible (not gated on DevMode). Layout:
- **Left**: `Ln {line}, Col {col}` + `({len} selected)` when selection active
- **Center**: spacer
- **Right**: line count, encoding, line endings, indent style (hardcoded for now)
- **DevMode rows**: existing perf stats (unchanged, only visible in DevMode)

Permanent bar updates every render frame (cheap: two PieceTable lookups). DevMode stats
still throttled to every 5th frame.

### Key bindings added

| Binding | Command |
|---------|---------|
| Ctrl+Y | Delete line |
| Ctrl+W | Select word |
| Ctrl+Shift+U | Uppercase selection |
| Ctrl+Shift+L | Lowercase selection |
| Ctrl+Shift+P | Proper case selection |
| Alt+Up | Move line(s) up |
| Alt+Down | Move line(s) down |

Test count: **287** (266 Core + 21 Rendering).

## 2026-03-03 — View menu settings and debounced persistence

### Debounced settings save

`AppSettings.ScheduleSave()` uses a `System.Threading.Timer` with a 500 ms one-shot
that resets on each call. Rapid changes (e.g., dragging the scroll-rate slider) are
coalesced into a single disk write. All callers now use `ScheduleSave()` instead of
`Save()`.

Fixed a bug where `JsonIgnoreCondition.WhenWritingDefault` silently dropped `false`
values for bool properties whose C# default is `true` (e.g., `ShowLineNumbers = true`).
Switching to `WhenWritingNull` ensures value-type properties are always serialized.

### View menu

Four toggle items with checkbox indicators, each backed by a persisted `AppSettings`
property:

| Menu item      | Setting            | Default | Effect                                    |
|----------------|--------------------|---------|-------------------------------------------|
| Line Numbers   | `ShowLineNumbers`  | `true`  | Gutter on/off                             |
| Status Bar     | `ShowStatusBar`    | `true`  | Permanent Ln/Ch bar on/off                |
| Statistics     | `ShowStatistics`   | `true`  | Dev perf stats bars on/off (DevMode only) |
| Wrap Lines     | `WrapLines`        | `true`  | Word wrap on/off                          |

`UpdateStatusBarVisibility()` centrally manages the `IsVisible` state of the
`PermanentStatusBar`, `StatsBar`, `StatsBarIO`, and the outer `StatusBar` border.
The entire border hides when both the permanent bar and stats bars are off.

### No-wrap layout

When `WrapLines = false`, the layout engine receives `maxWidth = ∞` which activates
`TextWrapping.NoWrap`. `EditorControl` separates `textW` (may be infinity) from
`extentW` (always finite, for scroll extent). The windowed-layout estimation,
`ScrollCaretIntoView`, and `ScrollToTopLine` all guard against the infinity case
by setting `totalVisualRows = lineCount` (one row per logical line, no wrapping).

### WrapLinesAt column limit

`WrapLinesAt` setting (default 100) caps the wrapping width at `N × charWidth` pixels.
Wrapping occurs at the viewport edge or the column limit, whichever is narrower.
Values < 1 are treated as unlimited (viewport-only wrapping).

All `textW` computation is centralised in `EditorControl.GetTextWidth(extentWidth)`
which applies both the `_wrapLines` flag and the `_wrapLinesAt` column cap. This is
called from `EnsureLayout`, `MeasureOverride`, `ScrollCaretIntoView`, and
`ScrollToTopLine`. `MaxColumnsPerRow` also routes through `GetTextWidth` so the
status bar "Ch" field pads correctly.

The setting is wired from `AppSettings.WrapLinesAt` → `Editor.WrapLinesAt` in
`WireViewMenu`. There is no menu UI yet (numeric settings will use the future
settings page); for now it's editable in `settings.json`.

A translucent gray vertical guide line (`GuideLinePen`, `#30000000`, 1px) is drawn
at the column limit when `WrapLinesAt >= 1` and the column falls within the viewport.
`WrapLinesAt <= 0` disables both wrapping-at-column and the guide line (viewport-only
wrapping).

Test count: **287** (266 Core + 21 Rendering).

## 2026-03-03 — Line number gutter

Implemented the optional line number display from the backlog. Line numbers appear in a
non-editable gutter on the left side of the editor.

### Behaviour

- **Right-aligned**, no leading zeros, 1-based numbering.
- Gutter width auto-expands to fit the digit count of the document's total line count
  (minimum 2 digits). When the digit count changes (e.g., 999 → 1000 lines during a
  streaming load), the text area reflows automatically.
- Wrapped lines show the number on the **first visual row only** — subsequent wrapped
  rows within the same logical line have no number.
- Gutter background is light gray (#F0F0F0), number text is muted (#A0A0A0).
- Works correctly with windowed layout for large documents.

### Settings

- `ShowLineNumbers` in `AppSettings` (default: `true`). Persisted to
  `%APPDATA%/DevMentalMD/settings.json`.
- **View → Line Numbers** menu item toggles the setting with a checkbox indicator.

### Implementation

- `EditorControl.ShowLineNumbers` property — toggles gutter visibility and triggers
  re-layout.
- `UpdateGutterWidth()` computes gutter pixel width from line count digits × char width
  plus left/right padding (4px + 12px).
- `EnsureLayout()` and `MeasureOverride()` subtract `_gutterWidth` from `maxWidth` so
  the text layout engine wraps to the correct available width.
- `Render()` calls `DrawGutter()` which paints the gutter background and per-line
  numbers using right-aligned `TextLayout` objects.
- All coordinate translation (selection rects, caret, pointer hit-testing) offsets X by
  `_gutterWidth`.
- `ScrollCaretIntoView()` and `ScrollToTopLine()` chars-per-row estimation accounts for
  gutter width.

Test count: **287** (266 Core + 21 Rendering).

## 2026-03-04 — Tab bar polish, dirty tracking, window state

### Tab bar fixes
- **Stable tab widths**: All tabs are measured with the bold typeface for layout so
  switching the active tab no longer causes horizontal shifts.
- **Drag-to-reorder**: Pointer capture (`e.Pointer.Capture`) keeps the drag alive when
  the mouse leaves the tab bar vertically. Hysteresis via neighbor-center-crossing
  (must cross the center of the adjacent tab) prevents 1px oscillation. Hover state
  is cleared on drag start and suppressed while any mouse button is down.
- **CloseOtherTabs**: Now always calls `UpdateTabBar()` even when the kept tab is
  already the active tab (previously `SwitchToTab` short-circuited).
- **DirtyDotGlyph** changed from `\uF136` to `\uECCC`.

### Undo-to-clean dirty tracking
- `EditHistory` gained `_savePointDepth`, `MarkSavePoint()`, and `IsAtSavePoint`.
- `Document` exposes `MarkSavePoint()` / `IsAtSavePoint` (delegates to history).
- `MainWindow.OnSave` / `SaveAsAsync` call `MarkSavePoint()` after writing.
- `OnTabDocumentChanged` checks `Document.IsAtSavePoint` — if undo returns the
  document to its saved state, `IsDirty` is cleared and the dirty dot disappears.

### Window state persistence
Three settings in `AppSettings`: unmaximized size (`WindowWidth`/`WindowHeight`),
unmaximized position (`WindowLeft`/`WindowTop`), and `WindowMaximized`. Size is
applied in the constructor before layout; position and maximized state are applied
in the `Opened` handler. `TrackWindowState()` only records size/position when
`WindowState == Normal` so maximized bounds never overwrite the normal geometry.

### Deferred: Windows 11 Mica transparency

Research completed — not yet implemented. Summary of approach:

**Requirements**: `TransparencyLevelHint = [WindowTransparencyLevel.Mica]` and
`Background = Brushes.Transparent` on the Window. `ExtendClientAreaToDecorationsHint`
(already enabled) is required.

**EditorTheme changes**: Tab bar, menu bar, and status bar backgrounds would need
semi-transparent brush variants (e.g. `Color.FromArgb(0x80, 0x20, 0x20, 0x20)` for
dark tab bar). The editor surface stays fully opaque for readability.

**Graceful degradation**: Check `ActualTransparencyLevel` at runtime — if the
platform doesn't support Mica (older Windows, Linux), fall back to opaque brushes.

**Inactive window**: Mica continues rendering when unfocused (unlike native Win11).
Handle `Activated`/`Deactivated` to swap between transparent and semi-opaque
backgrounds.

**Limitations**: No `MicaAlt` in Avalonia (only `Mica`). To approximate MicaAlt's
darker tint in the title bar area, layer a semi-transparent dark brush in
`TabBarControl.Render()`. `TransparencyBackgroundFallback` doesn't respond to
dynamic resource changes — workaround is resetting `TransparencyLevelHint` in code
during theme switches.

**Scope**: Mainly `EditorTheme` brush variants + a branch in `ApplyTheme()` + the
two Window properties. Could add a `UseMica` toggle in `AppSettings` and View menu.

Test count: **287** (266 Core + 21 Rendering).
