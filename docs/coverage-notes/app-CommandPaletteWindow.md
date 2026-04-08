# `CommandPaletteWindow`

`src/DMEdit.App/CommandPaletteWindow.cs` (480 lines)

F1 command palette. Fuzzy-filtered list of all commands,
keyboard-navigable, optional category grouping.

## Likely untested

- **Fuzzy matching algorithm** — no tests.
- **Recently-used commands bumped to top** (if implemented).
- **Disabled commands shown greyed out or hidden?**
- **Category grouping toggle** (`CommandPaletteGroupByCategory`).
- **Escape closes without running.**
- **Enter runs the selected command.**
- **Arrow navigation wraps around?**
- **Filter text persistence across opens.**

## Architectural concerns

- **480 lines** — moderate. Most of the logic is UI.
- **No shared filter/match library** — if the palette's
  fuzzy matcher is home-grown, worth extracting it.
- **Settings tab commands** are filtered out when the
  settings tab is active, per the journal note about
  "settings-page command whitelist" in error handling
  hardening.
