# `MainWindow`

`src/DMEdit.App/MainWindow.axaml.cs` (3 859 lines — second
largest file after `EditorControl`)
Tests: none direct.

The main editor window. Owns tabs, menu bar, toolbar, tab bar,
status bar, find bar, command dispatch, chord state machine,
file watcher integration, settings tab, command palette
integration, single-instance file-open handler, window state
persistence, theme management, update service wiring.

## Likely untested (summary)

### Tab management
- **Open tab** from path / untitled / session restore.
- **Close tab** with unsaved changes → SaveChangesDialog.
- **Close tab with background paste in progress.**
- **Tab drag-reorder** (if implemented here vs TabBarControl).
- **Active-tab switch** preserves caret/scroll/selection of
  outgoing tab.
- **Active-tab switch** to a still-loading tab.

### Menu / toolbar
- **Menu command binding** (`_menuCommandBindings`) — each
  entry pairs a `MenuItem` with a `Command`. Updates
  enabled state, gesture text, visibility.
- **Chord gesture display** (`_menuGestureParts`,
  `_gestureHooked`) — shows "Ctrl+K, Ctrl+S" in menu items.
- **Advanced menu items** toggled by `HideAdvancedMenus`.
- **Menu overrides** from AppSettings.

### Chord state machine
- **Two-keystroke chord dispatch** — `_chordFirst`,
  `_chordTimer`. Key 1 arms, key 2 dispatches, timeout
  cancels.
- **Interruption during chord** (click, focus loss).
- **Chord that conflicts with a single-key binding.**

### Alt-menu access keys
- **`_altPressedClean`, `_menuAccessKeyActive`** — defer
  menu activation to Alt KeyUp so Alt+drag doesn't trigger.
- **Alt+F opens File menu** without stealing focus from
  the editor first.

### File operations
- **Open** via dialog (Avalonia StorageProvider, Linux zenity
  fallback).
- **Save** with backup-on-save, SHA-1 update, dirty-flag reset.
- **Save As** with picker.
- **Revert** with confirmation.
- **Print** dispatch to WpfPrintService via reflection.
- **Save as PDF** via PdfGenerator.

### File watcher
- **External modification notification** → reload prompt
  or auto-reload.
- **Tail file mode** — see TailFile setting.

### Single instance
- **File-open request from secondary process** — add a new
  tab, focus window.

### Status bar
- **Four interactive segments** (Ln/Col, encoding, line
  ending, indent) — click → corresponding dialog.
- **Stats timer** updates perf readout.

### Settings tab
- **Settings opened in a tab**, not a window — the
  `_settingsTab` field.
- **Close settings tab** restores the prior active tab.

### Update service
- **Check on startup** if `AutoUpdate` is enabled.
- **Update-available UI affordance**.
- **Apply and restart**.

### Window state
- **Save window bounds on close** (width, height, position,
  maximized).
- **Restore on launch** — `_windowStateReady` flag gates
  the restore.

## Architectural concerns

- **3 859 lines in one file.** Same recommendation as
  `EditorControl`: partial-file split by concern:
  - `MainWindow.TabManagement.cs`
  - `MainWindow.MenuAndToolbar.cs`
  - `MainWindow.ChordKeyboard.cs`
  - `MainWindow.FileOps.cs` (open/save/print)
  - `MainWindow.FileWatcher.cs`
  - `MainWindow.WindowState.cs`
  - `MainWindow.StatusBar.cs`
  - `MainWindow.SettingsTab.cs`
  - `MainWindow.UpdateService.cs`
  - `MainWindow.Theme.cs`
- **Heavy coupling to every App service** — by design. The
  wiring is the hard part.
- **Command registration split** between
  `RegisterWindowCommands` (here) and
  `EditorControl.RegisterCommands` (in EditorControl) — two
  places where `Command.Wire` is called. A single registry
  would be clearer.

## Bugs / hazards (from journal and code read)

1. **"Non-responsive after crash" (byron, 2026-04-07)** — see
   `Program.md` and the journal key-deferred items. MainWindow
   is part of the startup path.
2. **Menu access-key race**: `_altPressedClean` + `_menuAccessKeyActive`
   is subtle.
3. **Event-handler closure leak** flagged by VS Memory
   Insights — `MainWindow+<>c__DisplayClass157_1` (472B).
   Worst offender per the journal.
4. **`_menuCommandBindings` never cleared** — if the menu is
   rebuilt (e.g. profile switch), stale entries accumulate.
   Worth checking.
5. **`_menuGestureParts` / `_gestureHooked` lifecycle** —
   same concern.
6. **Static `FilePickerFileType[] FileTypeFilters`** is fine;
   shared across instances.

## Recommendation

**High priority**: partial-file split before deeper changes.
Same reason as `EditorControl` — state density makes every
edit risky.
