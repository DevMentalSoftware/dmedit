# 11 — Search, Memory Safety, and Horizontal Scrolling (2026-03-27)

## Horizontal Scrollbar

When wrapping is disabled and text overflows the viewport, a standard Avalonia
`ScrollBar` (Orientation=Horizontal) now appears below the editor.

- **TextOriginX** (`_gutterWidth - _scrollOffset.X`) — single property used by
  all text-area rendering (text, selection, column selection, carets, whitespace
  glyphs, column guide).
- **Extent width** computed from `doc.Table.MaxLineLength * GetCharWidth()` when
  wrapping is off; stays at viewport width when wrapping is on.
- **Clip rect** prevents horizontally-scrolled text from painting over the gutter.
- **Hit-testing** adds `_scrollOffset.X` to mouse X before layout hit-testing.
- **ScrollCaretIntoView** has a horizontal component: estimates caret X from
  column within line, scrolls to keep caret visible.
- **Wrap toggle** resets `HScrollValue = 0`.
- XAML: `EditorGrid` is now `RowDefinitions="*,Auto" ColumnDefinitions="*,Auto"`.
  `HScrollBar` in row 1, col 0. Wired via `HScrollChanged` event.

## Find Bar Improvements

- **Search type toggles**: replaced ComboBox with two ToggleButtons (`*` for
  Wildcard, `.*` for Regex). Mutual exclusion — only one active at a time,
  both can be off.
- **Match count display**: positioned inside the search textbox with padding
  to account for the toggle buttons.
- **Focus management**: all find bar buttons (Next, Replace, ReplaceAll, menu
  items) return focus to the search textbox after their action, fixing
  Shift+Enter direction change.
- **Search text cleared on document switch**: prevents expensive auto-search
  against a new large document.

## Async Match Counting

`GetMatchInfo` → `GetMatchInfoAsync`: match counting runs on a background
thread with `CancellationToken`. Each new search term cancels the previous
count. Match count capped at 9999 (displays "9999+"). UI never blocks during
counting.

## Memory Safety — GetText Guard

`PieceTable.GetText` throws `ArgumentOutOfRangeException` if `len > MaxGetTextLength`
(5 KB). Forces callers to use `ForEachPiece` for large ranges.

### Callers fixed:
- **GetSelectedText** — `ForEachPiece` + `StringBuilder` fallback for large selections
- **GetColumnSelectedText** — `ForEachPiece` per slice
- **TransformCase** — `ForEachPiece` to build original text
- **SwapLines** — `ForEachPiece` for all text reads
- **SelectWord / ExpandSelection** — bounded 1KB window around caret instead
  of full line (handles single-line JSON files)
- **FindNextSelection / FindPreviousSelection** — `GetSingleLineSelectionTerm`
  rejects multi-line or >1024 char selections
- **ReplaceCurrent** — guards `Selection.Len > MaxSearchTermLength`
- **OpenFindBar** — uses `GetSelectionAsSearchTerm()` (public, no word-select
  side effect) instead of `doc.GetSelectedText()`

## Line-at-a-Time Layout

`TextLayoutEngine.Layout(string)` eliminated. New `LayoutLines(PieceTable, topLine,
bottomLine, ...)` reads one line at a time via `table.GetText(lineStart, contentLen)`.
No multi-line string is ever materialized. `LayoutEmpty()` delegates to `LayoutLines`
with an empty PieceTable. `SplitLogicalLines` removed. 10 new rendering tests cover
LF, CRLF, CR, mixed endings, trailing newline, empty, partial range, and hit-test
round-trip.

## Chunked Search (No GetText in Search Paths)

All search paths use `SearchChunked` / `SearchChunkedBackward` with `ArrayPool<char>`
buffers and `CopyFromTable` (via `ForEachPiece`). No string allocation for plain
text search. Regex still needs `new string(span)` per 64KB chunk (unavoidable).
`FindInDocument` and `FindInDocumentBackward` fully chunked. Old `SearchRange` /
`SearchRangeLast` (which materialized entire ranges) removed.

## ReplaceAll — Bulk PieceTable Operation (Implemented)

### Two-tier design:

**Uniform** (`UniformBulkReplaceEdit`): all matches have the same length and
replacement. Stores `long[] matchPositions` + `int matchLen` + `string replacement`.
50K matches = ~400 KB for the position array.

**Varying** (`VaryingBulkReplaceEdit`): matches have different lengths and/or
replacements. Stores `(long Pos, int Len)[]` + `string[]`. Used by regex replace
and `ConvertIndentation`.

### Algorithm: `PieceTable.BulkReplaceCore`

O(pieces + matches) single pass:
1. Append replacement(s) to `_addBuf`
2. Walk `_pieces` and matches simultaneously, building new piece list:
   - Gap before match: copy original pieces (split at boundary via TakeFirst/SkipFirst)
   - Match region: skip original pieces, insert `Piece(Add, addOfs, repLen)`
   - Tail: copy remaining pieces
3. Replace `_pieces`, call `BuildLineTree()` once

### Undo: O(1) snapshot restore

Before the bulk replace, snapshot the piece list + line tree + add-buffer length.
Undo: `RestorePieces` + `InstallLineTree` + `TrimAddBuffer`. No per-match reversal.

### ConvertIndentation rewritten

Walks document via `ForEachPiece` to collect `(indentStart, indentLen)` regions,
computes per-region replacement, calls `BulkReplaceVarying`. Eliminated the
`StringBuilder` sized to `_table.Length * 2`.

### Async with ProgressDialog

`ReplaceAllAsync` runs match collection on a background thread via `Task.Run`
with `CancellationToken`. Progress reported every 256 matches as a percentage
of document scanned (0-80% for search, 85% for replace phase). `ProgressDialog`
shows a progress bar + cancel button. The bulk replace + `BuildLineTree` run on
the UI thread (must, since PieceTable is not thread-safe).

Status bar (`StatusLeft`) shows "Replaced N occurrences in Xms" after completion,
or "Replace All cancelled" / "No matches found". Stats bar IO row shows
`ReplAll: Xms` via `PerfStats.ReplaceAllTimeMs`.

## SuppressChangedEvents

`Document.SuppressChangedEvents()` returns `IDisposable`. All `Changed?.Invoke`
calls replaced with `RaiseChanged()` which checks `_suppressChanged` counter.
Nested calls supported. Single event fires when outermost scope disposes.

## Whole-Word Search with Wildcards and Regex (2026-04-14)

When MatchWholeWord is enabled, searches should not span multiple words.

### Wildcard mode

`WildcardToRegex` accepts a `wholeWord` parameter. When true, `*` maps to
`\w*` (word characters only) instead of `.*`, and `?` maps to `\w` instead
of `.`. Combined with `\b...\b` wrapping, wildcards are confined to a single
word.

### Regex mode — segment-based matching

Greedy quantifiers like `.*` in a `\b`-wrapped regex can span across words.
Post-filtering matches for whitespace is fragile: greedy matches consume
valid shorter matches at the same position, requiring retry loops and
truncation hacks.

Instead, for regex + WholeWord (`SearchOptions.RegexWholeWord`), the chunk
is split at whitespace boundaries and the compiled regex is applied to each
non-whitespace segment via `Regex.Match(input, start, length)`. The match
is physically constrained to the segment, so greedy quantifiers can't span
words. `\b` assertions still see the full string context (documented .NET
behavior), so word boundaries work correctly.

Four helpers: `MatchInSegments` (forward), `LastMatchInSegments` (backward),
`RegexFirstMatch` / `RegexLastMatch` (non-WholeWord fast path — single
`Match` call per chunk, no segmentation overhead).

### Other search fixes in this session

- **`RegexOptions.Compiled`** — regex patterns are now JIT-compiled to IL,
  significant for large documents searched in 64KB chunks.
- **`RegexOptions.Multiline`** — `^` and `$` match at line boundaries, not
  chunk boundaries. Without this, `^`/`$` are useless (chunks are an
  implementation detail invisible to the user).
- **Keyboard shortcut bug** — `SearchFindNext`/`SearchFindPrevious` command
  wiring called `FindNext()`/`FindPrevious()` with default parameters,
  ignoring MatchCase, WholeWord, and SearchMode from the find bar.
- **Session-restore crash** — `UpdateStatusBar` accessed `PieceTable.Length`
  during streaming load. Fixed by checking `_activeTab.IsLoading` before
  any piece-table access; `LoadCompleted` now calls `UpdateStatusBar`.

## Chunked Regex Limitation: `^`/`$` at Chunk Boundaries

`RegexOptions.Multiline` makes `^`/`$` match at line boundaries within a
chunk, which is correct. However, when a chunk starts mid-line (all chunks
after the first), `^` falsely matches at the chunk start because the regex
engine sees it as start-of-string. Similarly, `$` can falsely match at
a chunk end that falls mid-line.

**User impact:** rare — only affects regex patterns using `^`/`$` where the
match happens to fall near a 64KB chunk boundary that lands mid-line.

**Fix:** align chunk boundaries to line boundaries. After advancing
`start += chunkSize`, scan forward in the document to the next `\n` and
start the next chunk there. Cost: one `ForEachPiece` scan of at most one
line per chunk advance (negligible vs. the regex matching cost). The overlap
region already handles matches spanning chunk boundaries, so line-aligned
chunks don't introduce gaps. `SearchChunkedBackward` needs the same
treatment (align `chunkEnd` to a line boundary when retreating).

## Deferred Items

- **Large selection copy** — silently truncates; needs user notification and
  eventually streaming clipboard support. See `memory/project_deferred_bugs.md`.
- **Pseudo-newlines** — synthetic line breaks at MAX_LINE_LENGTH for single-line
  files. Eliminates per-caller windowing workarounds.
- **Search within selection** — auto-select "Current Selection" scope when find
  bar opens with multi-line selection. Integer range limiting, no materialization.
- **Line-aligned chunks** — fix `^`/`$` false matches at chunk boundaries.
  See "Chunked Regex Limitation" above.
