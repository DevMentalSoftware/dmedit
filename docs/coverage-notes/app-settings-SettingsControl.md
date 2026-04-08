# `SettingsControl.axaml.cs`

`src/DMEdit.App/Settings/SettingsControl.axaml.cs` (380 lines)

The main Settings page UserControl. Tabbed categories, navigates
via `AppSettings.LastSettingsPage` persistence. Reset-to-defaults
button. Theme-aware row rendering.

## Likely untested

- **Tab switching updates `LastSettingsPage`.**
- **`LastSettingsPage` restored on page open.**
- **Reset all (or per-category) dialog confirmation.**
- **Settings written to disk after every change** (debounced
  via `AppSettings.ScheduleSave`).
- **Theme switch re-renders rows.**

## Architectural concerns

- **Mix of registry-driven rows (`SettingRowFactory`) and
  hand-built sections (`CommandsSettingsSection`,
  font picker, etc.)** — two row-creation pathways.
- **Max width 720** — per the journal (2026-04-03 fixed).
