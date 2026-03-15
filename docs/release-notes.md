# DevMentalMD Release Notes

## Beta 2 — 2026-03-15

### Keyboard Settings & Key Bindings
- Full keyboard settings UI for viewing and editing key bindings
- Commands support a second alternate gesture
- Chord gestures — multi-key sequences (e.g., Ctrl+K, Ctrl+C)
- 6 predefined key mapping profiles: Default, VS Code, Visual Studio, JetBrains,
  Eclipse, Emacs
- Conflict warnings when bindings overlap
- FindShortcut search to quickly locate commands by name

### Commands & Editing
- 21 new editor commands: Find stubs, Delete Word Left/Right, Insert Line
  Above/Below, Duplicate Line, Indent/Outdent, Scroll Line Up/Down, Zoom
  In/Out/Reset, Revert File
- Command Palette (F1) with text filter, arrow-key navigation, Enter to execute
- SmartIndent — automatic indentation on Enter, including multiline support
- Show Whitespace toggle
- New selection commands with transient settings that reset on close

### Look & Feel
- Unified light/dark theme using FluentTheme palettes with consistent hover and
  focus effects across all controls
- Custom TextBox, ComboBox, Button, and GridSplitter themes
- FindBar redesign — Find Next/Previous moved to submenu with a directional
  arrow button
- Status bar color matched to menu bar for visual continuity

### Interactive Status Bar
- Four clickable segments: Ln/Ch, Encoding, Line Ending, and Indent
- Hover highlights and flyout menus on each segment
- GoTo Line dialog
- Automatic indentation style detection from file content

### Session & File Management
- Session persistence — open tabs, scroll positions, and edits restored on startup
- Async tab loading — tabs appear instantly from session state, then finish
  loading in the background with an animated progress indicator
- Proper handling of saved and closed documents in the session
- Recent files: auto-prune missing local files, preserve network paths, saved
  files added to recent list
- CloseAll context menu on tabs

### I/O & Infrastructure
- Additional charset/encoding support beyond UTF-8
- PagedFileBuffer no longer holds a FileStream open, preventing save conflicts
- Simplified file loading — all non-zipped files use PagedFileBuffer
- Improved file identity handling for zipped text files
- Improved exception handling throughout

---

## Beta 1 — 2026-03-06

### Core Editor Engine
- Custom text rendering engine built on Avalonia DrawingContext
- Piece-table document model built from scratch
- Full keyboard navigation, insert/delete, undo/redo with compound events, and
  selection with click-drag
- Edit coalescing so undo operates in natural blocks

### Large File Support
- IBuffer abstraction with paged file I/O — loads pages on demand for files of
  any size
- Windowed layout for documents over 500 lines — only the visible portion is
  measured and rendered
- Automatic detection and direct loading of zipped text files

### Scrolling & Navigation
- Dual-zone custom scrollbar with adjustable speed and minimum thumb size
- Middle-mouse-button auto-scroll over the entire editor window
- Page Up/Down, Line Up/Down with correct preferred-column tracking
- Caret position restored on undo/redo

### Display
- Line numbers in a right-aligned gutter with auto-expanding width
- Word wrap at a configurable column with a vertical guide line
- Rounded-corner selection highlighting across multiline and varying line widths
- Light and dark theme support

### Tabbed Documents
- Tabbed document interface with overflow handling
- Tab position and scroll state restored at startup
- Close and CloseAll keyboard shortcuts

### Menus & Settings
- File, Edit, View, and Search menus
- Persistent settings between sessions (View menu options)
- Settings panel accessible from the menu bar
- Custom window chrome on both Windows and Linux

### Status Bar
- Current line and column display
- Selection count and line count

### Cross-Platform
- Runs on Windows and Ubuntu Linux
- Portable icon font for UI glyphs
- Workarounds for Avalonia file dialog issues on Linux
- Publish scripts for Windows and Linux
