# `IScrollSource`

`src/DMEdit.App/Controls/IScrollSource.cs` (23 lines)

Read-only scroll state interface consumed by `DualZoneScrollBar`.
Five properties: max, value, viewport, extent, row height.

## Likely untested
- Interface only. Implementor is `EditorControl`; single
  source of truth. No direct test of the contract.

## Architectural concerns
- **No way to notify of change.** The scrollbar re-reads on
  every render; the editor invalidates the scrollbar when
  state changes. An event would be cleaner, but the current
  poll-every-frame approach is fine for a scrollbar.
- **Values are doubles with implied units** (pixels, rows).
  A tagged struct would be safer but overkill.
