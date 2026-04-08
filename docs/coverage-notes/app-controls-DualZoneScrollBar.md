# `DualZoneScrollBar`

`src/DMEdit.App/Controls/DualZoneScrollBar.cs` (682 lines)

Custom scrollbar with dual-zone mode for very large documents.
Three-zone thumb (outer-top fixed-rate, inner proportional,
outer-bottom fixed-rate). Reads state from an `IScrollSource`
(single source of truth).

## Likely untested

- **Dual-zone activation threshold** — when the proportional
  thumb would shrink below `MinInnerThumbHeight`. Untested
  directly.
- **Fixed-rate drag math in the outer zones** — the
  "ReferenceDocLines = 100" constant governs scroll speed
  relative to document size. Hard to verify by inspection.
- **Outer zone visibility at extremes** — no outer-top at
  top, no outer-bottom at bottom. Tested by manual interaction.
- **Thumb hit-test** — drag starts in the right zone,
  subsequent moves stay in that zone even if the pointer
  leaves.
- **Wheel events** — delegated to scroll source.
- **Keyboard** — Page Up/Down, Home/End. Delegated or
  handled here?
- **`ApplyTheme`** — visual refresh. Untested.
- **`IScrollSource` rebinding** — if `ScrollSource` is set
  to a new instance mid-session. Untested.
- **`IScrollSource` reads stale values mid-render** —
  should be safe because the read is synchronous with draw.

## Architectural concerns

- **682 lines for a scrollbar** is a lot. Fine for a
  domain-specific control, but worth confirming that the
  dual-zone spec is well-documented somewhere (the journal
  reference at line 26 helps).
- **Hard-coded constants** (BarWidth=17, ArrowHeight=17,
  etc.) — not DPI-aware. On a high-DPI monitor the thumb
  looks tiny. Should scale with system DPI.
- **Theme coupling** — holds an `EditorTheme` reference.
  Reasonable for a custom-drawn control.
- **Linear scan for scroll conversion math** — likely not;
  the conversions are O(1). Fine.
