# `ClipboardRingWindow`

`src/DMEdit.App/ClipboardRingWindow.cs` (279 lines)

Modal popup showing the clipboard ring entries; user picks
one to paste. Keyboard-navigable list.

## Likely untested
- **Item selection, paste-on-select** — no tests.
- **Empty ring** — should show a message.
- **Long entries** — truncated display.
- **Keyboard nav** (Up/Down/Enter/Escape).

## Architectural concerns
- **Separate window** vs an embedded popup — heavy for a
  cycling UI. Per journal, the inline PasteMore cycling
  (Ctrl+Shift+V) is the fast path; this window is the
  browsable alternative.
