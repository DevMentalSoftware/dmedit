## 2026-03-05 — Edit Coalescing Undo

Replaced the simple key-hold compound undo system (`_pressedKeys` / `_repeatCompoundKey` /
KeyUp-based) with a full edit coalescing system inspired by VS Code's undo behaviour.

### How it works

- **Coalesce key**: each edit type gets a string key (`"char"`, `"backspace"`, `"delete"`,
  `"tab"`, `"delete-line"`, `"move-line-up"`, `"move-line-down"`). Consecutive edits with
  the same key are grouped into a single compound undo entry.
- **Idle timer** (1 second): restarts on every edit. When it fires, the compound is committed.
  This ensures pauses during typing create separate undo entries — the behaviour the user
  explicitly preferred over Notepad++'s approach (which never splits on pauses).
  Continuous typing (even across word/space boundaries) stays in a single compound.
- **No word boundary detection**: initially implemented non-word → word flushing but removed
  it — the user prefers purely timer-based coalescing where an entire sentence typed without
  pausing is one undo entry.
- **Enter always breaks**: Enter flushes any open compound and creates a standalone edit.
  Natural stopping point for prose writing. Does not start the coalesce timer.
- **Breaking events**: cursor movement (arrow keys, Home/End, PageUp/Down), mouse clicks,
  undo/redo, clipboard operations (cut/copy/paste), select all/word, transform case,
  focus loss, and save all flush the compound before proceeding.

### Key methods

- `FlushCompound()` (public) — commits the compound, stops the timer, resets state. Called
  by `MainWindow.OnSave` / `SaveAsAsync` so save is always a clean break point.
- `Coalesce(string key)` (private) — flushes if key changed, opens compound if needed,
  restarts timer.

### Coalescing operations

| Operation | Behaviour |
|---|---|
| Typing (OnTextInput) | `Coalesce("char")` with word boundary flush |
| Backspace | `Coalesce("backspace")` |
| Delete | `Coalesce("delete")` |
| Tab | `Coalesce("tab")` |
| Enter | `FlushCompound()` then standalone insert |
| Delete Line | `Coalesce("delete-line")` |
| Move Line Up/Down | `Coalesce("move-line-up/down")` |
| Undo / Redo | `FlushCompound()` before |
| Cut / Copy / Paste | `FlushCompound()` before |
| Select All / Word | `FlushCompound()` before |
| Transform Case | `FlushCompound()` before (standalone) |
| Arrow / Home / End / PageUp / PageDown | `FlushCompound()` before |
| Mouse click | `FlushCompound()` before |
| Focus loss | `FlushCompound()` |
| Save | `FlushCompound()` via MainWindow |

### Settings

`CoalesceTimerSeconds` in `AppSettings` (default 1.0 s). Exposed as
`EditorControl.CoalesceTimerSeconds` property, wired in `WireViewMenu()`. Minimum
clamped to 0.1 s. Editable in `%APPDATA%/DevMentalMD/settings.json`.

Test count: **287** (266 Core + 21 Rendering).

## 2026-03-05 — Settings Document (tab-based settings UI)

Implemented the Settings page from the backlog — a dedicated tab with a custom Avalonia
`UserControl` that presents all `AppSettings` properties as a searchable, categorized UI.
Visual reference: VS 2026's Options tab.

### Architecture

A `SettingsControl` (Avalonia `UserControl`) replaces the `EditorControl` + `ScrollBar`
area when the Settings tab is active. The control uses native Avalonia form controls
(CheckBox, NumericUpDown, ComboBox) rather than custom DrawingContext rendering.

**Content switching**: `SwitchToTab` toggles `IsVisible` on `Editor`/`ScrollBar` vs
`SettingsPanel`. No Document is shown when settings is active; the tab holds a dummy
empty Document to satisfy the non-nullable `TabState.Document` property.

### Layout

```
┌─ Search bar ───────────────────────────────────────┐
├──────────────┬─────────────────────────────────────┤
│ Category     │  Section headers + setting rows     │
│ sidebar      │  (scrollable)                       │
│ (ListBox)    │                                     │
└──────────────┴─────────────────────────────────────┘
```

- **Search box** at top filters settings by DisplayName and Description (case-insensitive).
- **Category sidebar** (ListBox): "All Settings" shows everything; selecting a category
  shows only that category's settings.
- **Modified indicator**: blue left-border (3px) on rows where the current value differs
  from the default.

### Setting categories

| Category   | Settings                                                        |
|------------|-----------------------------------------------------------------|
| Display    | ShowLineNumbers, ShowStatusBar, ShowStatistics, WrapLines, WrapLinesAt |
| Theme      | ThemeMode                                                       |
| Editor     | CoalesceTimerMs                                                 |
| Scrollbar  | OuterThumbScrollRateMultiplier                                  |
| Advanced   | RecentFileCount, PagedBufferThresholdBytes, DevMode             |

### Setting metadata

`SettingDescriptor` record: Key, DisplayName, Description, Category, Kind (Bool/Int/Long/
Double/Enum), DefaultValue, Min, Max, EnumType. `SettingsRegistry` holds the static list
of all descriptors — single source of truth for setting metadata.

### Control types per SettingKind

| Kind   | Control                  |
|--------|--------------------------|
| Bool   | CheckBox (with desc)     |
| Int    | NumericUpDown            |
| Long   | NumericUpDown (1MB step) |
| Double | NumericUpDown (0.1 step) |
| Enum   | ComboBox                 |

### Live sync

`SettingsControl.SettingChanged` fires with the property name. `MainWindow.WireSettingsPanel`
handles this event with a switch that pushes changes to `Editor`, `ScrollBar`, menu
checkmarks, theme, and status bar visibility — reusing the same logic as the View menu
toggles. All changes call `AppSettings.ScheduleSave()` for debounced persistence.

### Tab behavior

- **Gear button** (⚙ `\uE713`) on the far-right of the menu bar opens the Settings tab.
- Settings tab is closable. Reopening creates a fresh tab if the previous was closed.
- `TabState.IsSettings` flag distinguishes it from document tabs.
- `OnSave` is guarded to skip settings tabs.
- Focus handling allows native control focus when settings is active (doesn't force-focus
  the EditorControl).

### Files created

- `Settings/SettingDescriptor.cs` — metadata record + `SettingKind` enum
- `Settings/SettingsRegistry.cs` — static list of all descriptors
- `Settings/SettingRowFactory.cs` — builds Avalonia controls per descriptor
- `Settings/SettingsControl.axaml` + `.cs` — the main UserControl

### Files modified

- `TabState.cs` — added `IsSettings` property and `CreateSettings()` factory
- `MainWindow.axaml` — added `SettingsControl`, gear button in menu bar (Grid wrapper)
- `MainWindow.axaml.cs` — `SwitchToTab` visibility toggle, `WireSettingsPanel`,
  `OpenSettings`, save guards, focus guards, theme application

Test count: **287** (266 Core + 21 Rendering).

---

## TODO — Missing editor commands (vs VS Code)

Commands we currently have: basic navigation (arrows, Home/End, Ctrl+Home/End, Page Up/Down),
word navigation (Ctrl+Left/Right), all Shift+ selection variants, type/delete/backspace,
undo/redo, cut/copy/paste, Select All, Select Word, Delete Line, Move Line Up/Down,
case transforms, file ops (New/Open/Save/SaveAs/Close/CloseAll), tab switching, view toggles
(line numbers, status bar, word wrap, theme), settings panel, mouse (click/double/triple,
shift-click extend, middle-drag scroll).

**Format:** change `+` to `-` to mark as "skip for now".

### Find & Replace

- [+] Find (Ctrl+F)
- [+] Incremental Search (Ctrl+I)
- [+] Replace (Ctrl+H)
- [+] Find Next / Previous (F3 / Shift+F3)
- [+] Find Word or Selection (Ctrl+F3)
- [-] Find in Selection

### Navigation

- [+] Go to Line (and column if ':') (Ctrl+G)
- [-] Go to Matching Bracket (Ctrl+Shift+\)
- [+] Scroll Line Up/Down without moving caret (Ctrl+Up / Ctrl+Down)

### Editing

- [+] Insert Line Below (Ctrl+Enter)
- [+] Insert Line Above (Ctrl+Shift+Enter)
- [-] Copy Line Up 
- [-] Copy Line Down 
- [+] Delete Word Left (Ctrl+Backspace)
- [+] Delete Word Right (Ctrl+Delete)
- [+] Set Indentation for Line/Selection (Tab) (Indents/Deindents based on context if known or does nothing)
- [+] Indent Line/Selection (Ctrl+])
- [-] Outdent Line/Selection (Shift+Tab with selection, Ctrl+[)
- [-] Toggle Line Comment (Ctrl+/)
- [-] Toggle Block Comment (Ctrl+Shift+/)
- [+] Duplicate Line/Selection (Ctrl+D)
- [-] Join Lines
- [-] Transpose Characters
- [-] Sort Lines Ascending / Descending
- [+] Trim Trailing Whitespace (Rather than an editing command, we'll make a setting for whether to do this automatically)

### Multi-cursor & Advanced Selection

- [-] Add Selection to Next Find Match 
- [+] Select All Occurrences of Find Match
- [+] Add Cursor Above / Below (Shift+Alt+Up / Down) This command and the next are combined. 
- [+] Column / Box Selection (Shift+Alt+Drag or Shift+Alt+Up / Down)
- [-] Expand Selection 
- [-] Shrink Selection 
- 
### View

- [+] Zoom In / Out / Reset (Ctrl+= / Ctrl+- / Ctrl+0)
- [-] Toggle Minimap
- [-] Code Folding / Unfolding

### File

- [+] Save All (Ctrl+Shift+S) (I added this to the menu already but didn't implement)
- [+] Revert File

### Other

- [-] Command Palette (Ctrl+Shift+P)

