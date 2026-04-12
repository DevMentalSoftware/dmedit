# DMEdit Design Journal

Chronological record of design decisions, user requirements, and architectural direction.
**This is the index.** Full entries live in `docs/design-journal/`. When adding a new
entry, write the full content in the relevant detail file and add a one-line summary here
under "Recently completed". Update the "In progress" section after every change, even a
small one — it is the primary way a fresh session recovers context.

---

## Table of Contents

| File | Dates | Topic |
|------|-------|-------|
| [01-foundations](design-journal/01-foundations.md) | 2026-02-26 | Core editor, IBuffer abstraction, windowed layout, dual-zone scrollbar design |
| [02-document-model](design-journal/02-document-model.md) | 2026-02-26 | Variable heights, WYSIWYG block tree, persistence architecture |
| [03-performance](design-journal/03-performance.md) | 2026-02-28, 2026-04-06 | Perf stats, streaming I/O, paged buffer, ZIP support, cold startup optimization |
| [04-ux](design-journal/04-ux.md) | 2026-02-28 | Undo selection, caret/scroll UX, selection rounded corners |
| [05-features](design-journal/05-features.md) | 2026-03-02 – 2026-03-04 | Feature backlog, editing commands, status bar, line numbers, tab bar |
| [06-settings](design-journal/06-settings.md) | 2026-03-05 | Edit coalescing undo, settings document tab |
| [07-commands](design-journal/07-commands.md) | 2026-03-06 – 2026-03-07 | Command registry, key binding profiles, 21 new commands, command palette |
| [08-status-bar](design-journal/08-status-bar.md) | 2026-03-14 | Interactive status bar buttons, indent detection, GoTo Line, file locking fix, file watching notes |
| [09-storage-and-history](design-journal/09-storage-and-history.md) | 2026-03-22 | Storage-backed edits, file history, checkpoints, projects, git integration roadmap |
| [10-session-and-reliability](design-journal/10-session-and-reliability.md) | 2026-03-24 | Session persist bugs, edit serialization, line tree reliability, memory safety, buffer simplification |
| [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md) | 2026-03-27 | Horizontal scrollbar, find bar improvements, async match counting, GetText guard, line-at-a-time layout, chunked search, ReplaceAll design |
| [12-utf8-add-buffer](design-journal/12-utf8-add-buffer.md) | 2026-03-29 | ChunkedUtf8Buffer replaces StringBuilder _addBuf, binary session persistence, paged eviction roadmap |
| [13-custom-textbox](design-journal/13-custom-textbox.md) | 2026-04-01 | DMInputBox: lightweight custom TextBox replacing Avalonia TextPresenter to fix caret bug |
| [14-forced-wrap](design-journal/14-forced-wrap.md) | 2026-04-02 | Attempted forced wrapping for long lines — moved to AlternateLineBranch |
| [15-char-wrap-mode](design-journal/15-char-wrap-mode.md) | 2026-04-03 | Character-wrapping mode: O(1) scroll math, no pseudo-lines needed |
| [16-print-progress](design-journal/16-print-progress.md) | 2026-04-03 | Print progress dialog, monospace pagination, cancellation, ETA display |
| [17-editing-polish](design-journal/17-editing-polish.md) | 2026-04-06 | Auto-indent on Enter, smart deindent Backspace, smart Home, trailing whitespace cleanup |
| [18-wrap-indicators](design-journal/18-wrap-indicators.md) | 2026-04-06 | Wrap symbol glyph at wrap column; hanging indent analysis (deferred) |
| [19-glyphrun-print](design-journal/19-glyphrun-print.md) | 2026-04-06 | GlyphRun fast path for WPF printing (~55–70% faster), word-break wrap restored, print error plumbing overhaul, hanging indent unblocked |
| [20-hanging-indent](design-journal/20-hanging-indent.md) | 2026-04-06 | Hanging indent on wrapped rows, Avalonia monospace GlyphRun fast path, first step toward removing TextLayout from the editor |
| [21-ascii-fast-path](design-journal/21-ascii-fast-path.md) | 2026-04-07 | ChunkedUtf8Buffer per-chunk IsAllAscii flag — column-mode insert ~28× faster, all CharAt-touching code paths benefit |
| [22-textlayout-crash-hardening](design-journal/22-textlayout-crash-hardening.md) | 2026-04-07 | Slow-path TextLayout sanitize + try/catch fallback — fixes real user crash scrolling a binary file (Avalonia split bug) |
| [23-scroll-invariants](design-journal/23-scroll-invariants.md) | 2026-04-11 | Debug invariants for scroll/layout alignment, `GetMonoCharWidth` bug fix, `_lastTextWidth` cache, `ShouldUseSlowPath` unification, `PerfStats.ScrollExactCalls` |
| [24-completed-log](design-journal/24-completed-log.md) | 2026-03-06 – 2026-04-10 | Older completed work moved out of this index to reduce size |

---

## Current State

**Test baseline: 11,108** (1269 Core + 60 Rendering + 9779 App, 1 skipped)

### In progress

- **Test coverage gaps** — Remaining untested branches:
  1. **Overwrite mode** — Insert key toggle, block caret, overwrite-insert logic.
  2. **CharWrap mode** — entire code path for huge single-line files (entry 15).
  3. **Column selection UI integration** — Alt+click, column drag, multi-cursor caret layers.  Math tested in Core, UI untested.
  4. **IsLoading** — streaming load behavior, interaction lock.
  5. **Find horizontal scroll** — `ScrollSelectionIntoView` doesn't handle horizontal scroll.  Known bug, documented in HScrollAndControlCharTests.

### Recently completed

- **Column editing blocked-by-wrap notification** (2026-04-12) — Alt+Click,
  Alt+Drag, and Alt+Shift+Arrow now show "Column editing disabled by
  wrapping." in the status bar (warning color, 3s auto-clear) instead of
  silently doing nothing.  New `StatusMessage` event on `EditorControl`.

- **Session-restore crash fix** (2026-04-12) — `UpdateTailButton` called
  `IsCaretOnLastLine` → `PieceTable.LineCount` → `Length` which iterates
  `_pieces` while the streaming-load worker mutates it on a background
  thread.  Fix: skip tail check when `tab.IsLoading`.

- **Per-line hanging indent** (2026-04-12, GH #13) — Continuation rows
  now indent relative to the line's own leading whitespace, not a fixed
  global offset.  New `MonoRowBreaker.LeadingIndentColumns`.
  `ContRowCharsForLine` derives per-line continuation width.

- **Selection path self-intersection fix** (2026-04-12, GH #14) —
  `FillSelectionPath` split into groups of horizontally-overlapping rects;
  each group gets its own contour.  203 new tests in
  `SelectionPathCornerTests`.

- **Whitespace indicator Y-offset fix** (2026-04-12) — Wrap arrows,
  control-char glyphs, and space/NBSP dots now account for row Y offset
  within multi-row layouts.

- **Settings overwrite protection** (2026-04-12) — `AppSettings._persistent`
  flag prevents test instances from clobbering the user's settings file.

- **Manual website** (2026-04-12) — Static HTML/CSS/JS manual site under
  `site/`.

- **Avalonia 12 upgrade** (2026-04-11) — Avalonia 11.3.12 → 12.0.0,
  SkiaSharp 2.88.9 → 3.119.3, xunit v2 → v3.
  See [23-scroll-invariants](design-journal/23-scroll-invariants.md).

- **Tab mono fast path + control char elimination** (2026-04-11) — Tab
  characters no longer force the TextLayout slow path.  Control chars
  handled as fallback glyphs — `ShouldUseSlowPath` now only checks
  `IsFontMonospace()`.

- **Scroll/layout bug fixes + invariants** (2026-04-11) — Caret layer
  null-layout fix, DMInputBox double-click crash (GH #12), scrollbar
  thumb drift, scroll extent inflation, slow-path row count divergence,
  FindNext/FindPrev scroll-into-view, MoveCaretVertical over-scroll,
  invariant infrastructure.  See [23-scroll-invariants](design-journal/23-scroll-invariants.md).

- **Surrogate-pair safety** (2026-04-11) — `DeleteBackward`/`DeleteForward`
  swallow both halves of a surrogate pair.  `PushInsert` sanitizes lone
  surrogates.  `ChunkedUtf8Buffer.DecodeUpToNChars` emits U+FFFD on
  malformed sequences.

- **Home/End on wrapped continuation rows** (2026-04-11) — Cascading
  Home (row start → line start → smart-home) and End (row end → line end).
  Works on the mono path.

Older completed entries: [24-completed-log](design-journal/24-completed-log.md)

### Key deferred items

- **Column editing while wrapping is enabled** — Currently disabled when
  any wrapping mode is on.  Status bar message added (2026-04-12).
  Key question: logical-column vs visual-cell rectangle semantics.
  VS Code and Sublime use logical columns, clipping at line end.

- **Search Within Selection** — OpenFindBar with multi-line selection
  should auto-scope to "Current Selection".  Integer range limiting;
  preserve range across ReplaceAll edits.

- **Paged add-buffer eviction** (roadmap) — older immutable chunks could
  page to disk like PagedFileBuffer.  No code yet.
  See [12-utf8-add-buffer](design-journal/12-utf8-add-buffer.md).

- **Memory growth while scrolling** — `MonoLineLayout.Draw` allocates
  fresh glyph buffers per row per draw.  Cache needed.

- **"Editor Font" reset button in Settings** — restores both family and
  size.

- **Block model / WYSIWYG editor** — designed, partially implemented,
  not wired in.  See [02-document-model](design-journal/02-document-model.md).

- **Windows 11 Snap Layout** — needs invisible overlay control with
  `MaximizeButton` role over custom button.

- **Windows 11 Mica transparency** — researched, not implemented.
  See [05-features](design-journal/05-features.md).

- **Toolbar Undo/Redo buttons** — not yet implemented.

- **Storage-backed large edits** — add buffer spill to disk.

- **ErrorDialog "Report a Bug" button** — wire `GitHubIssueHelper`.

- **Guard against whole-document materialization** — `ReplaceAll`
  match-collection phase on huge documents.

- **Avalonia upstream issues** — `NameRecord` string interning, AXAML
  resource URI duplication.  One-time startup costs, not blocking.

- **GoToPosition near end of long-wrapped-line docs** — Center policy
  convergence fails.  Skipped in GoTo_CaretOnScreen.

- **PageDown+PageUp round-trip near doc end** — page boundaries shift.
  Skipped in PageDownThenUp_NearOriginal.
