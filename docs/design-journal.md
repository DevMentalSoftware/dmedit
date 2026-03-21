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

---

## Current State

**Test baseline: 449** (366 Core + 21 Rendering + 62 App)

### Recently completed

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

### In progress (uncommitted, 2026-03-20)


#### Design decisions made

- **Foreground is always black (light) or white (dark)** — never changes on hover or
  focus. All state feedback is communicated via background color only.
- **Background and border use the same color** — the border exists only to provide
  rounded corners; it is not a visible dividing line. This gives a cleaner, flatter look.
- **Hover effects** — background tint only (semi-transparent overlay).
- **Focus effects** — background color shift only.

#### Changes made so far (uncommitted)

- **New** `Themes/ButtonTheme.axaml` — flat/transparent Button + ToggleButton theme
- **New** `Themes/GridSplitterTheme.axaml` — light/dark colors + hover/pressed states
- **Updated** `Themes/ComboBoxTheme.axaml` — added ThemeDictionaries (light+dark);
  improved hover: split into `ContentArea` and `DropDownOverlay` borders so each zone
  tints independently
- **Updated** `Themes/TextBoxTheme.axaml` — added ThemeDictionaries with proper
  light+dark values; removed foreground setters from `:pointerover`/`:focus` states
- **Updated** `Themes/NumericUpDownTheme.axaml` — pointer-over updates border only
- **Updated** `App.axaml` — references new theme files; removed redundant inline
  ThemeDictionaries block; dark palette `ChromeMediumLow` tweaked `#2c` → `#2d`
- **Updated** `Settings/SettingsControl.axaml.cs` — panel background uses
  `MenuBackground` instead of `EditorBackground`
- **Updated** `Settings/SettingsControl.axaml` — GridSplitter width 1 → 3px
- **Updated** `Services/EditorTheme.cs` — `MenuBackground` `#F9F9F9` → `#F8F8F8`
- **Updated** `DevMentalMd.App.csproj` — simplified `Avalonia.Diagnostics` reference
  to `Condition="'$(Configuration)' == 'Debug'"`

#### Additional design decisions (2026-03-11)

- **Border = Background at all times** (firm decision) — border is invisible; its only
  job is to provide corner rounding. Both colors are `#E8E8E8` (light) / `#383838` (dark).
- **Hover: background shifts to a darker/lighter opaque color** — `#DEDEDE` (light
  hover) / `#454545` (dark hover). No semi-transparent overlays for TextBox.
- **Focus: background shifts** — `#FFFFFF` (light focused) / `#2C2C2C` (dark focused).
- **NUD inner TextBox always transparent** — all three of Background, BackgroundPointerOver,
  and BackgroundFocused overridden to Transparent in Resources. Outer NUD shell handles
  all visual states.
- **Dark placeholder foreground** changed from `#DDDDDD` to `#909090` to match light
  placeholder color (same relative contrast in both themes).

### Key deferred items

- Block model / WYSIWYG editor is fully designed and partially implemented but not wired
  into the running editor (see [02-document-model](design-journal/02-document-model.md))
  The Block document will be optional for Markdown files to allow view/edit of Markdown
  with wysiwyg editing.
- Windows 11 Mica transparency researched but not implemented (see
  [05-features](design-journal/05-features.md))
- toolbar, Undo/Redo toolbar buttons not yet implemented
- Overwrite mode (Insert/Overwrite toggle) not yet implemented
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
  re-insertion (`InsertPieces` + `RestoreLines`).  **Known bug:** redo of a massive
  delete (2.5M+ lines) causes 30 GB memory explosion and multi-minute freeze — needs
  profiling to identify whether it's in `FreeSubtree`, `Merge`, `MaxLineLength`
  recompute, or a treap corruption.  The issue does not appear in our Edit metric,
  suggesting the cost is in layout/render or tree operations outside the timed window.
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
  stream via `ForEachPiece`.  Currently `ConvertLineEndings` and `ConvertIndentation`
  stream the input but still build the full output as a `StringBuilder` + `InsertEdit`.
  A future improvement could stream the output to a temp file and reference it as
  pieces, avoiding the full-document string entirely.
