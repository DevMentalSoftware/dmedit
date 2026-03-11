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

- [x] **Settings page** — a dedicated document type with a long scrollable list of all
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

---
