# 15 — Character-Wrapping Mode

**Date:** 2026-04-03
**Status:** Design

---

## Problem

The pseudo-line system (splitting long lines into fixed-width chunks in the
LineIndexTree) handles large files with long lines, but at significant
complexity cost:

- **Dual-offset system** (BufOffset/DocOffset) threaded through the codebase
- **`SplitLongLine`/`SplitLongLines`** in PieceTable during edits
- **`LineTerminatorType.Pseudo`** with dead-zone logic
- **`_val2`/`_sum2`** secondary prefix sums in LineIndexTree
- **Memory overhead** from millions of tree nodes for single-line files
- **Performance overhead** from `DeriveTerminatorType` → `CharAt` → disk I/O

Since pseudo-lines wrap at exact character positions (no word wrapping), the
LineIndexTree provides no benefit over simple arithmetic:
`row N starts at char N * charsPerRow`.

## Solution: Character-Wrapping Mode

A distinct editor mode where long-line documents display text wrapped at a
fixed character width.  No TextLayout wrapping, no pseudo-lines, no dual
offsets.  Row positions are computed mathematically.

### Trigger

Activates automatically when BOTH conditions are met:
- File size exceeds a configurable threshold (e.g. 10 MB)
- Any line exceeds 500 characters

The threshold is a user setting: integer spinner, size in MB.  The user can
also manually toggle the mode.

### Key properties

- **`charsPerRow`** = `floor(textAreaWidth / charWidth)`.  Rounded down so
  wrapping never changes until the window resizes by at least one full
  character width.
- **Tab characters** render as a single space-width glyph (1 char wide).
  This ensures every character occupies exactly one cell — monospace grid.
- **Line endings** (CR, LF, CRLF) are shown literally as space-width glyphs.
  When ShowWhitespace is enabled, CR and LF render with their standard
  whitespace glyphs.  CRLF occupies two cells.
- **Row N** starts at character offset `N * charsPerRow` within the document.
  No tree lookup needed — pure arithmetic.
- **Total rows** = `ceil(documentLength / charsPerRow)`.

### What the tree stores

The LineIndexTree is **not used** in character-wrapping mode (for scroll/row
calculations).  It may still be built for features that need real-line
awareness (e.g. line ending detection during save), but the layout/scroll
engine bypasses it entirely.

### Gutter

Shows **character offset** or **row number** (1-based) instead of line
numbers.  The offset at the start of the visible row:
`(topRow * charsPerRow) + 1`.  Or simply the row number: `topRow + 1`.

### Status bar

- **Row / Col** instead of Ln / Ch
- **Row count** instead of line count
- **Line ending section** hidden (line endings are rendered inline)
- **Encoding** shown as normal

### WrapLinesAt in character-wrapping mode

Only takes effect when `WrapLines` is true (same gating as normal mode).
When active, it acts as a column limit:
`charsPerRow = min(WrapLinesAt, floor(textAreaWidth / charWidth))`.
When `WrapLines` is false, `charsPerRow = floor(textAreaWidth / charWidth)`
— wraps at window edge only.

### Disabled features

When character-wrapping mode is active:
- Tab-as-indent (Tab key inserts literal tab, no smart indent)
- Indent / Outdent commands (no concept of indentation)
- Column/block selection (already disabled when wrapping is on)
- Line ending conversion (line endings are content, not metadata)

### Search

Find/Replace operates on character offsets.  Search results map directly to
document offsets — no translation needed.  The find bar works as-is.

### Scroll math

- **Total rows** = `ceil(docLength / charsPerRow)` — exact, no estimation
- **Top row** = `floor(scrollY / rowHeight)` — exact
- **Scroll extent** = `totalRows * rowHeight` — exact
- **Caret Y** = `(caretOffset / charsPerRow) * rowHeight` — exact
- **No RenderOffsetY drift** — every row has the same height

All scroll math is O(1).  No tree lookups, no prefix sums, no estimation.

### Layout

For each visible row:
1. `startChar = topRow * charsPerRow`
2. `text = GetText(startChar, min(charsPerRow, docLength - startChar))`
3. `layout = MakeTextLayout(text, NoWrap)` — no Avalonia wrapping
4. Emit `LayoutLine(charStart, charLen, row, 1, layout)`

Each row is exactly `charsPerRow` characters (except the last which may be
shorter).  Each LayoutLine has `HeightInRows = 1`.

### Resize behavior

When the window resizes:
- Compute new `charsPerRow = floor(textAreaWidth / charWidth)`
- If unchanged from previous frame, no relayout needed
- If changed, invalidate layout — all rows recompute from the new width
- Scroll position adjusts: `newScrollY = (caretOffset / newCharsPerRow) * rh`

### Memory

In character-wrapping mode, a 1GB single-line file uses:
- 1 tree node (the single real line)
- ~40 LayoutLine objects (visible rows)
- ~40 TextLayout objects (visible row text)
- Zero pseudo-line overhead

Compare to current pseudo-line system: ~5 million tree nodes with dual
prefix sums.

---

## Implementation plan

### Phase 1 — Core mode flag and layout bypass
- Add `CharWrapMode` bool to EditorControl (or Document)
- When active, `LayoutWindowed` computes rows via arithmetic
- `TextLayoutEngine.LayoutLines` gets a character-wrap path
- Scroll math uses exact row/offset formulas

### Phase 2 — Gutter, status bar, tab rendering
- Gutter shows row numbers or offsets
- Status bar shows Row/Col instead of Ln/Ch
- Tab characters render as single-width spaces

### Phase 3 — Feature gating
- Disable indent, column selection, line ending conversion
- Tab key inserts literal tab
- WrapLinesAt acts as fixed column limit

### Phase 4 — Auto-trigger
- Setting: file size threshold (MB) for automatic activation
- Check at load time: file size > threshold AND longest line > 500
- Manual toggle via View menu

### Phase 5 — Remove pseudo-line system
- Once character-wrapping mode is stable, remove:
  - `SplitLongLine`, `SplitLongLines`
  - `LineTerminatorType.Pseudo`, `VirtualDeadZoneWidth`
  - `_val2`/`_sum2` from LineIndexTree (back to single-value treap)
  - `DocOffset` type alias and all doc-space translation methods
  - `MaxPseudoLine` setting
- This is the same removal we did on AlternateLineBranch, but now safe
  because character-wrapping mode handles the large-file case.

---

## Open questions

1. **Proportional fonts.** Character-wrapping assumes monospace.  If the user
   has a proportional font, characters aren't uniform width.  Options:
   use average char width (approximate), or force monospace in this mode.

2. **Editing in character-wrapping mode.** Inserting/deleting characters
   shifts all subsequent row boundaries.  This is fine — the row math
   recomputes from the new document length.  But the caret position needs
   care: after inserting a char, the caret advances by 1, which may move
   to the next visual row.

3. **Undo in character-wrapping mode.** Undo/redo works on the PieceTable
   as normal.  The visual rows recompute automatically.

4. **Find highlight rendering.** Search matches span character ranges.
   In character-wrapping mode, a match might span row boundaries.
   The highlight needs to wrap across rows — same as any wrapped line today.
