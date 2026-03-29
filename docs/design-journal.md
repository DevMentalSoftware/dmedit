# DevMentalMD Design Journal

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
| [03-performance](design-journal/03-performance.md) | 2026-02-28 | Perf stats, streaming I/O, paged buffer, ZIP support |
| [04-ux](design-journal/04-ux.md) | 2026-02-28 | Undo selection, caret/scroll UX, selection rounded corners |
| [05-features](design-journal/05-features.md) | 2026-03-02 – 2026-03-04 | Feature backlog, editing commands, status bar, line numbers, tab bar |
| [06-settings](design-journal/06-settings.md) | 2026-03-05 | Edit coalescing undo, settings document tab |
| [07-commands](design-journal/07-commands.md) | 2026-03-06 – 2026-03-07 | Command registry, key binding profiles, 21 new commands, command palette |
| [08-status-bar](design-journal/08-status-bar.md) | 2026-03-14 | Interactive status bar buttons, indent detection, GoTo Line, file locking fix, file watching notes |
| [09-storage-and-history](design-journal/09-storage-and-history.md) | 2026-03-22 | Storage-backed edits, file history, checkpoints, projects, git integration roadmap |
| [10-session-and-reliability](design-journal/10-session-and-reliability.md) | 2026-03-24 | Session persist bugs, edit serialization, line tree reliability, memory safety, buffer simplification |
| [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md) | 2026-03-27 | Horizontal scrollbar, find bar improvements, async match counting, GetText guard, line-at-a-time layout, chunked search, ReplaceAll design |

---

## Current State

**Test baseline: 510** (414 Core + 31 Rendering + 65 App, 1 skipped)

### Recently completed

- **Error Handling & UX Hardening** (2026-03-27) — Major overhaul of global exception
  handling and several editor UX fixes:
  - **Fatal error dialog**: `HandleFatalException` rewritten to avoid deadlocks.
    `ShowDialog` cannot receive input when called from a `Post` callback (Win32 modal
    loop issue), so background-thread exceptions use `Show()` + manual modality
    (`mainWindow.IsEnabled = false`, `Topmost = true`, centered via `Opened` handler).
    UI-thread exceptions use normal `ShowDialog`. Re-entrancy guard (`_handlingFatal`)
    prevents duplicate dialogs. Exit button calls `Process.Kill()` for guaranteed
    termination. Debugger-attached shows Continue button. `SaveSession()` removed from
    crash path (risky + slow).
  - **ErrorDialog improvements**: DockPanel layout (buttons anchored bottom-right),
    reduced margins, Expander header styled with dark-red background via
    ToggleButton template styles (normal + pointerover + pressed states).
    `ErrorDialogButton.Exit` and `.Continue` added.
  - **DevMode test commands**: `Dev.ThrowOnUIThread` and `Dev.ThrowOnBackground`
    commands — Help menu items (visible only in DevMode) that throw
    `InvalidOperationException` on the respective thread for testing crash handling.
    Background uses `new Thread` (not `Task.Run`) for immediate exception delivery.
    Added to all 6 key binding profiles as intentionally unbound.
  - **Undo/Redo scroll preservation**: `Document.Undo()`/`Redo()` now return the
    `IDocumentEdit` that was applied. `PerformUndo`/`PerformRedo` skip
    `ScrollCaretIntoView` for bulk replace edits, preserving scroll position.
  - **ProgressDialog cancel fix**: `OnClosed` override cancels the CTS regardless
    of how the dialog closes (button, taskbar, programmatic). Double-close guard
    in the `finally` block.
  - **Settings page command guard**: `DispatchCommand` now whitelists only File,
    Window, Menu, and Nav.FocusEditor commands on the settings page (was only
    blocking `RequiresEditor` commands). Blocks Command Palette, Find, View, Dev, etc.
  - **Command ordering**: Command Palette and Settings commands list preserve
    original definition order (no alphabetical sorting). Categories merged by
    first-seen order using dictionary. Dev commands hidden when DevMode is off.
  - **ScrollCaretIntoView verification pass**: After the initial estimate-based
    scroll, performs an `EnsureLayout()` + `GetCaretBounds()` check. If the caret
    is outside the viewport, adjusts scroll and re-layouts. Helps with wrapped lines
    where the scroll estimate drifts.

- **Bulk PieceTable Operations** (2026-03-27) — `PieceTable.BulkReplace` with two
  tiers: `UniformBulkReplaceEdit` (same-length, same-replacement — stores only
  `long[]` positions + single matchLen + single replacement) and
  `VaryingBulkReplaceEdit` (varying lengths/replacements — for regex, indentation).
  O(pieces + matches) single-pass algorithm with one `BuildLineTree()` call.
  Undo is O(1) via piece-list + line-tree snapshot restore + add-buffer trim.
  `EditorControl.ReplaceAllAsync` runs match collection on a background thread
  with `ProgressDialog` (cancel button, progress bar), then applies the bulk
  replace on the UI thread. Status bar shows replacement count + timing;
  stats bar shows `ReplAll: Xms` in the IO row.
  `Document.ConvertIndentation` rewritten to collect indent regions via
  `ForEachPiece` and feed them into `BulkReplaceVarying` — eliminated the
  `StringBuilder` sized to `_table.Length * 2`. Session serialization added for
  both bulk edit types. 29 new tests (PieceTable-level + Document undo/redo).
  See [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md).

- **Search, Memory Safety & Horizontal Scrolling** (2026-03-27) — horizontal
  scrollbar when wrapping disabled, find bar toggle buttons (Wildcard/Regex),
  async match counting, `GetText` 5KB guard on PieceTable, line-at-a-time layout
  (eliminated `Layout(string)`), chunked search with `ArrayPool` (no string
  allocation), `SuppressChangedEvents`, search term limited to single-line ≤1024
  chars.
  See [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md).

- **Session Persistence & Memory Safety** (2026-03-24) — major reliability pass fixing
  edit serialization (MaterializeText, explicit delete length, recapture in Apply),
  line tree reliability (InstallLineTree during load, CaptureLineInfo boundary fix,
  atomic InsertPiecesAndRestoreLines), memory safety (VisitPieces/ReadPieces 1 MB chunk
  cap, GetText internal), buffer simplification (removed ProceduralBuffer/StringBuffer/
  StreamingFileBuffer/DevSamples from production).
  See [10-session-and-reliability](design-journal/10-session-and-reliability.md).

- **Global Error Handling** (2026-03-23) — replaced SaveErrorDialog and SaveFailedDialog
  with a single general-purpose `ErrorDialog` (resizable, themed, configurable buttons).
  Dev mode adds a collapsed expander showing the full stack trace. Global exception handlers
  installed in Program.cs: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`,
  and `Dispatcher.UIThread.UnhandledException` — all write a crash report to disk and show
  the ErrorDialog. `CrashReport` generalized to accept any operation name (not just "Save")
  with a synchronous overload for global handlers. `SaveAsPdfAsync` wrapped in try/catch
  with ErrorDialog (was previously unprotected — caused silent crash on Linux).

- **Theme Refinements** (2026-03-20) — consistent light/dark theming across all controls.
  Design rules: foreground always black/white (state via background only), border = background
  (invisible border for corner rounding only), hover = background tint, focus = background
  shift. New ButtonTheme and GridSplitterTheme. Updated ComboBox, TextBox, NumericUpDown
  themes with proper ThemeDictionaries. NUD inner TextBox always transparent. Dark
  placeholder foreground `#909090`.

- **Tail File & Auto-Reload Improvements** (2026-03-21) — TailFile boolean setting
  (Editor category) with status bar icon button (Fluent \uF126). When enabled and
  caret is on last line + scrolled to bottom, auto-reload scrolls to show new content.
  Clicking the status bar button moves the caret to engage/disengage tail (Nav.MoveDocEnd
  / Nav.MoveUp). Icon dims when tail is inactive. Auto-reload rearchitected: background
  load awaits full completion before atomic UI-thread swap — eliminates flicker entirely.
  `EditorControl.ReplaceDocument(doc, scrollState)` swaps the document without resetting
  scroll to (0,0) by gating `_scrollOffset = default` behind `_keepScrollOnSwap` flag
  and skipping eager layout disposal. Full editor state (Selection with Anchor+Active,
  ColumnSel, scroll position) captured at swap time, not before the await. Dirty check
  aborts reload if user edited during load. Reload throttle: `ReloadInProgress` guard +
  `TailReloadCooldownMs` hidden setting (default 500ms). `DualZoneScrollBar` refactored
  to read from `IScrollSource` interface (implemented by EditorControl) — eliminated 5
  duplicate state fields, single source of truth. `SyncScrollBarFromEditor` removed.
  Load perf stat now captured for reloads. Re-watch on failed reload so watcher recovers.

- **Editor Font Setting** (2026-03-20) — font picker in Display settings: DMEditableCombo
  with dropdown (no filtering, full list always shown), ToggleButton "F" for fixed-width
  filter, NumericUpDown for size (points), and editable preview paragraph with editor
  colors. Font name validation (red foreground for uninstalled fonts, case normalization
  on lost focus). Default font auto-detected from preference list (Cascadia Code →
  Consolas → DejaVu Sans Mono → Liberation Mono → Courier New). Star glyph marks the
  default in the dropdown (display-only via ItemTemplate). Custom preview text persisted.
  DMEditableCombo enhanced: ShowClearButton, HighlightItem, scroll containment, popup
  close-before-text ordering fix. Settings controls hardened: left-click-only guards on
  checkboxes/combo boxes/command rows, context menu disabled on shortcut key capture
  boxes. GoTo Line and Command Palette dialogs: transparent corners, close buttons.
  Editor focus restored when switching from settings tab.

- **Save crash handling** (2026-03-17) — crash report infrastructure writes diagnostic
  files to the session directory when an unexpected save failure occurs. Error dialog
  offers Save As (to try a different location) or Close Tab. BackupOnSave
  setting added: BackupOnSave keeps a .bak copy. 

- **Column/Block Selection** (2026-03-16) — Alt+drag or Alt+Shift+Up/Down creates a
  rectangular selection spanning multiple lines. Typing, backspace, delete, tab, copy,
  cut, paste all operate at every cursor simultaneously. Column selection is defined in
  logical-line/column space with tab-aware column math. Undo reverts all per-line edits
  as one step. Escape or any non-column navigation command exits column mode. This feature
  is currently disabled when line wrapping is enabled.

- **Interactive Status Bar** (2026-03-14) — four clickable segments (Ln/Ch, Encoding,
  Line Ending, Indent) with hover highlights and flyout menus. Indent detection added
  to buffer scan loops. GoTo Line dialog. Encoding menu scaffold (UI only). File locking
  fix: removed persistent `_fs` from PagedFileBuffer. See
  [08-status-bar](design-journal/08-status-bar.md).
- **Command Palette** (2026-03-07) — F1, modal dialog with text filter, arrow-key nav,
  Enter to execute. Row colors from editor theme. Hidden from: Newline, Tab, Backspace,
  Delete. See [07-commands](design-journal/07-commands.md).
- **21 New Editor Commands** (2026-03-07) — Find stubs, Delete Word Left/Right, Insert
  Line Above/Below, Duplicate Line, Indent, Scroll Line Up/Down, Zoom In/Out/Reset,
  Revert File. Search menu added.
- **Predefined Key Mapping Profiles** (2026-03-06) — 6 profiles: Default, VS Code,
  Visual Studio, JetBrains, Eclipse, Emacs. Switching clears user overrides.
- **Command Registry + Key Binding System** (2026-03-06) — centralized dispatch,
  user-customizable bindings, Keyboard settings section, 55 App tests.

- **Pseudo-Newlines & Streaming Load Safety** (2026-03-28) — Major feature +
  performance/reliability overhaul for large files (single-line and multi-line).

  **Pseudo-newlines:** `MaxPseudoLine = 500` in PieceTable. Lines exceeding
  this are split into pseudo-lines in the line tree — document text is never
  modified. Pseudo-splitting at three levels: `PagedFileBuffer.ScanNewlines`
  (during background scan — primary), `BuildLineTree` (post-processing for
  `PieceTable(string)` constructor), and `SplitLongLine` helper (after edits).
  `GetLine()` fixed to check actual character via `CharAt` instead of assuming
  a newline at every boundary. `MaxGetTextLength` derived from
  `MaxPseudoLine + 2`. `MaxLayoutBytes` derived from `visibleRows * MaxPseudoLine`.
  `MAX_LONGEST_LINE` in PagedFileBuffer replaced with reference to
  `MaxPseudoLine`. `_longestLine` initialized to `MaxPseudoLine` so buffer
  short-circuits are safe from the start. 19 new tests using the constant.

  **Streaming load safety:** Removed O(N) buffer short-circuits from
  `LineCount`/`LineStartOfs`/`LineFromOfs` — all lookups now go through the
  `LineIndexTree` (O(log N) via treap prefix sums). `LayoutLines` receives
  frozen `lineCount` and `docLength` snapshots from `LayoutWindowed` to prevent
  race conditions with the background scan advancing `_totalChars`.
  `LayoutResult.TopLine` added so `DrawGutter` uses it directly instead of
  calling `LineFromOfs` (eliminated the O(N log N) `BinarySearchBufferLines`
  → `GetLineStart` hot path on every render frame). `LayoutLines` skips lines
  with inconsistent offsets (negative, backwards) during streaming.

  **Scroll/interaction lock during load:** `Document.IsLoading` property
  (derived from `Buffer.LengthIsKnown`). `EditorControl.IsLoading` delegates
  to `Document.IsLoading`. During streaming: scroll locked (both `ScrollValue`
  and `IScrollable.Offset` setters reject changes), caret hidden, mouse
  interaction blocked, `RestoreScrollState` deferred to `LoadComplete`.
  First page shows at `topLine = 0` immediately. Once `InstallLineTree` runs,
  scroll restores to saved position with O(log N) lookups.

  **Crash resilience:** `_layoutFailed` flag in EditorControl prevents
  cascading crashes from repeated layout failures. `HandleFatalException`
  re-entrancy guard no longer writes duplicate crash reports.
  `Debugger.Break()` before `GetText` guard throw for easier debugging.
  `FileEncoding.Unknown` added for documents before encoding detection.
  Tab spinner kept alive via `UpdateTabBar()` in `ProgressChanged` handler.

- **Per-line scroll estimation** (2026-03-27) — Replaced global `avgLineHeight`
  (uniform average) with per-line Y estimation using `LineIndexTree` prefix sums.
  Two new O(log N) helpers: `EstimateLineY(lineIndex, table, charsPerRow, rh)` maps
  a logical line to pixel Y via `max(N, ceil(charsBefore / charsPerRow)) * rh`;
  `EstimateTopLine(scrollY, table, lineCount, charsPerRow, rh)` is the inverse.
  `GetCharsPerRow(textWidth)` deduplicates the chars-per-row calculation.
  Updated `LayoutWindowed` (scroll→topLine and RenderOffsetY for large jumps),
  `ScrollCaretIntoView` (caret Y estimation), and `ScrollToTopLine`.
  Incremental small-scroll path unchanged (already uses actual cached line heights).
  When wrapping is off, `charsPerRow = 0` and both helpers degenerate to exact
  `lineIndex * rh` / `scrollY / rh`.

### In progress

- **Search Within Selection** — When OpenFindBar is invoked with a multi-line
  selection, the scope dropdown should auto-select "Current Selection" and all
  Find/Replace/GetMatchInfo operations should be bounded to the selection's
  start/end offsets. No text materialization — just integer range limiting.
  The selection range must be preserved across ReplaceAll edits (adjust offsets
  as replacements shift content). Single-line selections continue to populate
  the search term as today.


### Key deferred items

- Block model / WYSIWYG editor is fully designed and partially implemented but not wired
  into the running editor (see [02-document-model](design-journal/02-document-model.md))
  The Block document will be optional for Markdown files to allow view/edit of Markdown
  with wysiwyg editing.
- Windows 11 Mica transparency researched but not implemented (see
  [05-features](design-journal/05-features.md))
- toolbar, Undo/Redo toolbar buttons not yet implemented
- **Storage-backed large edits** — currently, inserted text lives in an in-memory
  `StringBuilder` (the Add buffer).  For extreme workflows (e.g. pasting 100 × 1 GB XML
  snippets into a single file and then saving a 100 GB result), the Add buffer should
  spill to storage (a temp file or embedded database) above a configurable threshold.
  The PieceTable already treats the Add buffer as opaque via `BufFor()` / `VisitPieces`,
  so the abstraction boundary is in place — the main work is implementing a storage-backed
  `IBuffer` for the Add side and wiring it into `_addBuf` / `_addBufCache`.
- **LineIndexTree (implicit treap)** — replaced FenwickTree with an implicit treap
  (`LineIndexTree`) supporting O(log L) insert/remove of lines.  All edits (including
  Enter/Delete across lines) are O(log L) with no rebuild.  Line lengths built during
  `PagedFileBuffer.ScanWorker` (no post-load rescan).  Undo uses piece-based zero-copy
  re-insertion (`InsertPieces` + `RestoreLines`).  The previously-known 30 GB memory
  explosion on redo of massive deletes was fixed in the 2026-03-24 reliability pass —
  root cause was VisitPieces allocating piece-sized char arrays and line tree / piece
  table inconsistency during restore.
- **Delayed clipboard rendering** — currently `GetSelectedText()` materializes the
  full selection as a `string` for the clipboard.  Windows supports delayed rendering
  via `IDataObject` / `OleSetClipboard`: the text is only materialized when the target
  app actually pastes.  This would make copying 250 MB of selected text instant.
  Caveats: 30-second render timeout, Avalonia's `IClipboard` doesn't expose delayed
  rendering (needs platform-specific interop).  References:
  - [Delayed Clipboard Rendering Explainer (MSEdge)](https://github.com/MicrosoftEdge/MSEdgeExplainers/blob/main/DelayedClipboard/DelayedClipboardRenderingExplainer.md)
  - [WM_RENDERFORMAT message — Win32 API](https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-renderformat)
  - [30-second timeout for delay-rendered clipboard — The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20220609-00/?p=106731)
- **Windows installer (Velopack + GitHub Releases)** — use Velopack to produce a
  self-contained installer with auto-update support. Deploy via GitHub Releases (free,
  no infrastructure). Steps: add `Velopack` NuGet package, wire `UpdateManager` for
  auto-update on startup, `dotnet publish` self-contained, `vpk pack` to build
  installer + delta packages, `vpk upload github` to push release assets. Requires
  GitHub repo to be set up first.
- **Guard against accidental whole-document string materialization** — operations
  that would materialize the entire document as a `string` (or any contiguous buffer)
  should either be prevented with an error message explaining why, or redesigned to
  stream via `ForEachPiece`.  `ConvertIndentation` has been rewritten to use
  `BulkReplaceVarying` (no more full-document StringBuilder).  `ConvertLineEndings`
  is handled at save time in `FileSaver` (already streams correctly).  Remaining
  concern: `ReplaceAll` progress dialog with cancel for the match-collection phase
  on huge documents (bulk replace itself is fast, but the chunked search scan for
  collecting matches could still take a while on very large files).
