# `ToolbarControl`

`src/DMEdit.App/Controls/ToolbarControl.cs` (367 lines)

Custom-drawn toolbar. Icon button row with overflow chevron
dropdown when narrow. Command dispatch via `ToolbarItem`
records.

## Likely untested

- **Overflow activation** — when buttons exceed available
  width. Untested.
- **Overflow dropdown content** — populated with the hidden
  buttons.
- **`ToolbarItem.IsToggle` + `IsChecked`** delegate
  evaluation on each render.
- **Disabled button rendering** — uses `_disabledFg`.
- **Hover state transitions.**
- **Theme switch mid-session** via `ApplyTheme`.
- **`IsDropdown` buttons** — show a chevron and fire a
  different event. Tested?

## Architectural concerns

- **Per-frame `FormattedText` churn** — journal flag.
  ~1 312 TextLineImpl allocations over a 50s recording.
  Fix: per-control cache keyed by (glyph, state, theme)
  with soft cap. Open.
- **Hard-coded theme colors** (line 52-56, 62-70) — bypass
  the brush system for direct color math. Theme switch
  forces all brushes to be reconstructed. Fine for a rare
  op.
- **`ButtonTheme.axaml` duplication** — the comment at
  line 52-56 says "Match ButtonTheme.axaml". Two sources
  of truth for button colors. Worth consolidating.
