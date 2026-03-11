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

---

## Current State

**Test baseline: 342** (266 Core + 21 Rendering + 55 App)

### Recently completed

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

### In progress (uncommitted, 2026-03-11)

**L&F theme unification** — finding colors that work for both light and dark themes
with consistent, logical hover/focus effects.

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
- Find/Replace, GoToLine, SelectAllOccurrences, ColumnSelect are registered stub commands
- Windows 11 Mica transparency researched but not implemented (see
  [05-features](design-journal/05-features.md))
- Smart Tab, ExpandWord, Wrap options, toolbar, Undo/Redo toolbar buttons not yet
  implemented
