# `EditorTheme`

`src/DMEdit.App/Services/EditorTheme.cs` (201 lines)

Theme record with colors for editor + chrome. Light/Dark
static instances.

## Likely untested

- **`Light` / `Dark` static instances** construction.
- **Color equality** (if used for diff) — trivial.
- **Brush caching** — if each color is wrapped in a
  `SolidColorBrush` per-theme, those brushes are shared
  across controls.

## Architectural concerns

- **~30 brushes per theme** — each control that holds a
  theme reference subscribes to all of them. Theme switch
  invalidates all consumers.
- **No third theme** — light/dark are the only options.
  System-follow is handled by `ThemeMode` enum elsewhere.
- **Brushes are mutable state if any caller modifies them**
  — worth making them `IImmutableBrush` (not sure if
  Avalonia has that).
- **Hex strings vs Color objects** — mixed in AppSettings
  and EditorTheme. Keep consistent.
