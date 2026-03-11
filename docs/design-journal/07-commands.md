## 2026-03-06 — Command Registry, Key Binding System & Keyboard Settings UI

Replaced scattered hardcoded keyboard handling with a centralized command infrastructure.
All ~55 existing shortcuts now flow through a single dispatch point and are user-
customizable from the Settings panel.

### Architecture

```
CommandRegistry (static metadata: Id, DisplayName, Category, DefaultGesture)
    ↓ defaults
KeyBindingService (runtime: overlays user overrides from AppSettings)
    ↓ resolves Key+Modifiers → CommandId
MainWindow.OnKeyDown (centralized dispatch)
    ├── Window commands → ExecuteWindowCommand()
    └── Editor commands → EditorControl.ExecuteCommand()
```

### Phase 1 — Command Infrastructure (5 new files + test project)

- `Commands/CommandDescriptor.cs` — sealed record for command metadata
- `Commands/CommandIds.cs` — ~55 const string IDs in `"Category.Name"` format
- `Commands/CommandRegistry.cs` — static list of all CommandDescriptors with default KeyGestures
- `Commands/KeyGestureComparer.cs` — IEqualityComparer<KeyGesture> (Avalonia's doesn't override Equals)
- `Commands/KeyBindingService.cs` — two-pass Rebuild() (defaults first, then user overrides always win),
  O(1) gesture→command resolution, conflict detection, SetBinding/ResetBinding/ResetAll
- `tests/DevMentalMd.App.Tests/` — 21 tests (6 registry integrity + 13 service behavior + 2 comparer)

### Phase 2 — Centralized Dispatch

- `EditorControl.cs`: removed `OnKeyDown` (~190 lines), added `ExecuteCommand(string commandId)`
  with full switch mapping all Edit.* and Nav.* commands
- `MainWindow.axaml.cs`: replaced OnKeyDown with centralized dispatch through KeyBindingService,
  extracted `CycleTab()`, `ToggleLineNumbers()`, `ToggleStatusBar()`, `ToggleWrapLines()`, `SaveAll()`,
  added `SyncMenuGestures()` for dynamic menu shortcut text, wired `MenuSaveAll.Click`

### Phase 3 — Persistence

- `AppSettings.cs`: added `Dictionary<string, string>? KeyBindingOverrides` — maps command ID
  to gesture string (empty = unbound, missing = use default, null dict = no overrides)

### Phase 4 — Keyboard Settings UI

- `Settings/KeyboardSettingsSection.cs` — code-only UserControl: grouped command list with
  category headers, current bindings, filter box, key capture box (tunneling handler),
  conflict detection, Assign/Remove/Reset buttons
- `Settings/SettingsRegistry.cs` — added "Scrollbar" and "Keyboard" categories
- `Settings/SettingsControl.axaml.cs` — accepts KeyBindingService, handles Keyboard category
  showing KeyboardSettingsSection, fires KeyBindingChanged event

### Key design decisions

- **Two-pass Rebuild()**: Pass 1 loads defaults for non-overridden commands, Pass 2 applies
  user overrides last so they always win the gesture→command map (fixes conflict when user
  rebinds to a gesture that's another command's default)
- **Tunneling key capture**: TextBox uses `AddHandler(KeyDownEvent, ..., RoutingStrategies.Tunnel)`
  so captured keys don't bubble to MainWindow's dispatch
- **OnTextInput untouched**: Character typing still goes directly to EditorControl, not through
  the command system
- **Menu gestures synced dynamically**: `SyncMenuGestures()` updates all menu InputGesture text
  from KeyBindingService, so customized shortcuts show in menus

### Test baseline: 308 (266 Core + 21 Rendering + 21 App)

---

## 2026-03-06 — Predefined Key Mapping Profiles

Replaced the previous "one Default profile + user overrides" model with six selectable
profiles that ship as embedded JSON resources. Users choose a profile in Settings >
Keyboard and the binding table changes to match that editor's conventions.

### Profiles

| Profile | Description |
|---|---|
| Default | Our own bindings, based loosely on VS Code |
| VS Code | Standard VS Code shortcuts |
| Visual Studio | Classic VS bindings |
| JetBrains | IntelliJ / Rider bindings |
| Eclipse | Eclipse IDE bindings |
| Emacs | Emacs-style bindings with chords and M-x alternatives |

Each profile JSON has `bindings` (primary slot) and optional `bindings2` (secondary
slot). Switching profiles clears all user overrides (they were relative to the old
profile).

### Test baseline: 342 (266 Core + 21 Rendering + 55 App)

---

## 2026-03-07 — 21 New Editor Commands

Added 21 new commands spanning Find, Edit, Nav, View, and File categories. Nine are
stubs (registered, bound, dispatch returns true but no behavior); twelve are fully
implemented.

### Stubs (silent no-ops for now)

Find.Find, Find.Replace, Find.FindNext, Find.FindPrevious,
Find.FindWordOrSelection, Find.IncrementalSearch, Nav.GoToLine,
Edit.SelectAllOccurrences, Edit.ColumnSelect.

Stubs are handled in `ExecuteWindowCommand()` — return `true` so the key doesn't
propagate.

### Fully implemented

| Command | Dispatch | Notes |
|---|---|---|
| Edit.DeleteWordLeft | Editor | Uses existing `FindWordBoundaryLeft` |
| Edit.DeleteWordRight | Editor | Uses existing `FindWordBoundaryRight` |
| Edit.InsertLineBelow | Editor | Inserts newline after current line |
| Edit.InsertLineAbove | Editor | Inserts newline before current line |
| Edit.DuplicateLine | Editor | Duplicates current line or selection span |
| Edit.Indent | Editor | Single-line: 4 spaces; multi-line: indent all |
| Nav.ScrollLineUp | Editor | Scrolls viewport without moving caret |
| Nav.ScrollLineDown | Editor | Scrolls viewport without moving caret |
| View.ZoomIn | Window | FontSize + 1 (max 72) |
| View.ZoomOut | Window | FontSize - 1 (min 6) |
| View.ZoomReset | Window | FontSize = 11.ToPixels() |
| File.RevertFile | Window | Reloads file from disk via `RevertFileAsync` |

### Tab → Indent swap (Default + Emacs only)

The Default and Emacs profiles reassign the Tab key from `Edit.Tab` (insert literal
tab character) to `Edit.Indent` (smart indent: single-line inserts 4 spaces, multi-line
selection indents all lines). VS Code, Visual Studio, JetBrains, and Eclipse keep
`"Edit.Tab": "Tab"` unchanged.

### New Search menu

Added a top-level **Search** menu between Edit and View containing all Find commands
plus Go to Line. This ensures stub commands are accessible via menu even without key
bindings.

### Menu additions

- **File**: Revert File (after Save All)
- **Edit**: Insert Line Below/Above, Duplicate Line, Delete Word Left/Right, Indent,
  Advanced sub-menu (Select All Occurrences, Column Select)
- **View**: Zoom sub-menu (In/Out/Reset), Scroll Line Up/Down

### Profile binding notes

- Emacs: DeleteWordLeft = Alt+Back, DeleteWordRight = Alt+D (standard M-DEL / M-d).
  Find stubs left unbound (Ctrl+S conflicts with File.Save).
- Eclipse: FindNext = Ctrl+K, FindPrevious = Ctrl+Shift+K, GoToLine = Ctrl+L.
- All profiles: ZoomIn/ZoomOut intentionally unbound; ZoomReset = Ctrl+D0.
- All profiles: InsertLineBelow, InsertLineAbove, DuplicateLine intentionally unbound.

### Files modified

- `Commands/CommandIds.cs` — 21 new constants
- `Commands/CommandRegistry.cs` — 21 new entries (new Find category)
- `Commands/Profiles/*.json` — all 6 profiles updated
- `Controls/EditorControl.cs` — 8 new command cases + 4 helper methods
- `MainWindow.axaml` — Search menu, Edit/View/File additions
- `MainWindow.axaml.cs` — menu wiring, window dispatch, RevertFileAsync, SyncMenuGestures
- `ProfileLoaderTests.cs` — updated intentionally-unbound exclusion set

### Test baseline: 342 (266 Core + 21 Rendering + 55 App)

---

## 2026-03-07 — Command Palette

Added a Command Palette window (F1) that lists all registered commands with their
current key bindings. Users can type to filter, use arrow keys to navigate, and press
Enter to execute. Similar to VS Code's Ctrl+Shift+P feature.

### Behavior

- Opens as a modal dialog centered on the main window
- TextBox at top filters commands by display name, category, or command ID
  (case-insensitive substring match)
- Arrow Up/Down moves the selection and fills the textbox with the command name
- Enter executes the selected command and closes the dialog
- Escape or clicking outside closes without executing
- Click on a row executes immediately
- PageUp/PageDown move selection by 10 items
- Low-level typing primitives (Newline, Tab, Backspace, Delete) are hidden from
  the list since executing them from the palette doesn't make practical sense

### Binding

F1 in all profiles (Ctrl+Shift+P was taken by Edit.ProperCase). Added
`Window.CommandPalette` to CommandIds, CommandRegistry, and all 6 profile JSONs.

### Menu

Added "Command Palette" item at the bottom of the Search menu with a separator.

### Theming

Inherits the current editor theme (light/dark). Row colors:
- Name: `EditorForeground`
- Category: `SettingsDimForeground`
- Gesture: `SettingsAccent`
- Selected row: `SettingsRowSelection`

### Files

- `CommandPaletteWindow.cs` — new code-only Window (no XAML)
- `Commands/CommandIds.cs` — added `WindowCommandPalette`
- `Commands/CommandRegistry.cs` — added "Command Palette" entry
- `Commands/Profiles/*.json` — all 6 profiles: `"Window.CommandPalette": "F1"`
- `MainWindow.axaml` — added menu item in Search menu
- `MainWindow.axaml.cs` — added `OpenCommandPalette()`, dispatch case, menu wiring,
  SyncMenuGestures entry

### Test baseline: 342 (266 Core + 21 Rendering + 55 App)
