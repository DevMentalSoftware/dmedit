# `SettingsRegistry`

`src/DMEdit.App/Settings/SettingsRegistry.cs` (119 lines)

Single source of truth for user-visible settings. Maps
`AppSettings` property names to display metadata.

## Likely untested

- **Every descriptor has a matching property on AppSettings**
  — no runtime check. A typo in `Key` becomes a silent no-op
  in the UI. A smoke test that loops over `All` and confirms
  each key maps to a real property via reflection would
  catch it.
- **Default values match AppSettings defaults** — if they
  drift, the settings page shows wrong defaults.
- **`EnabledWhenKey` references a real key** — same concern
  as above.
- **All categories in `Categories` appear in `All`** —
  unused categories would render empty tabs.

## Architectural concerns

- **Duplicates AppSettings defaults.** Moving defaults into
  the descriptor would be one source of truth, but then
  AppSettings property initializers would need to read from
  the registry (chicken and egg).
- **Not all settings are in the registry** — the registry
  is for user-visible settings only. Advanced state (recent
  files, window bounds, profile overrides) lives on
  AppSettings but not here. Worth a comment on which is
  which.
- **`Hidden` flag for settings that exist but shouldn't
  appear in the UI** — e.g. DevMode, ShowStatistics. Fine
  but a bit awkward.
