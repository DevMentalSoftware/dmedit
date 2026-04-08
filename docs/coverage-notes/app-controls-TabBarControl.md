# `TabBarControl`

`src/DMEdit.App/Controls/TabBarControl.cs` (1 375 lines)

Custom-drawn tab bar with Windows 11 Notepad styling. Rounded
convex top corners, concave bottom curves, close buttons,
drag-and-drop tab reordering, overflow handling, toolbar zones.

## Likely untested

- **Entire control** — no dedicated tests.
- **Tab layout math** — comfort width vs squished width, gap
  insertion, overflow when too many tabs.
- **Drag and drop** to reorder tabs.
- **Close button hit-test** vs tab body click.
- **Hover highlight** state transitions.
- **Right-click context menu** (close others, close to right,
  etc. — if implemented).
- **Toolbar zones** (TabToolbar, Center, Right — per entry
  in the journal).
- **Caption button space reservation** (the 140px constant).
- **Icon rendering** — app icon at left.

## Architectural concerns

- **1 375 lines in one custom-drawn control.** Similar to
  `EditorControl`, could benefit from partial files:
  layout math, render, pointer handling, drag-drop,
  toolbar zones.
- **Journal flagged `Toolbar/TabBarControl.Render` as a
  source of per-frame `FormattedText` churn** — ~60
  TextLineImpl allocations per render. Fix is a
  per-control cache by label/font. Open.
- **Hard-coded pixel constants** — not DPI-aware.
- **Caption button space (140px)** is a Windows-specific
  assumption. Custom window chrome on non-Windows platforms
  would need a different reserve.
