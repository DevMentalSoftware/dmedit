# 18 — Wrap Indicators

**Date:** 2026-04-06
**Status:** Partial (wrap symbol + UseWrapColumn implemented; hanging indent deferred)

---

## UseWrapColumn Setting

Previously, disabling the wrap-column feature required setting `WrapLinesAt=0`,
which was not discoverable.  Added a separate `UseWrapColumn` boolean (default
`true`).  When off, lines wrap at the viewport edge only and the column guide
line is hidden.  `WrapLinesAt` is greyed out in Settings when `UseWrapColumn`
is off (via `EnabledWhenKey`).

Internally, a `WrapColumnActive` helper property combines both checks
(`_useWrapColumn && _wrapLinesAt >= 1`) and is used in `GetTextWidth`,
`ComputeCharWrapCharsPerRow`, column guide drawing, and wrap symbol positioning.

## Wrap Symbol (implemented)

When word-wrap causes a logical line to span multiple visual rows, a small
arrow glyph is drawn at the wrap column to indicate continuation.  The symbol
is positioned just to the right of the WrapLinesAt column guide line, or at
the right edge of the text area when wrapping at viewport width.

- **Setting:** `ShowWrapSymbol` (bool, default `true`).
- **Rendering:** For each `LayoutLine` with `HeightInRows > 1`, draw the
  symbol once per wrapped visual row (all rows except the last).  The arrow
  is drawn geometrically (via `DrawLine` calls) rather than as a text glyph
  to avoid font-fallback inconsistencies.  Uses `WhitespaceGlyphPen` so it
  matches the existing theme.
- **Padding:** When the setting is on and wrapping is active, an extra 12 px
  (`WrapSymbolPadRight`) is added to the right-side text-area padding so the
  symbol doesn't overlap the scrollbar.  `TextAreaPadRight` was changed from
  a `const` to a computed property for this purpose.

## Hanging Indent (deferred)

Indent continuation rows so wrapped text is visually offset from the first
row of each logical line — a common feature in other editors.

### Why it's hard

The current layout pipeline creates **one Avalonia `TextLayout` per logical
line**.  `TextLayout.Draw()` renders all wrapped rows as a single unit; there
is no API to offset individual continuation rows.

To add hanging indent, we'd need to either:

1. **Split each wrapped line into multiple `TextLayout` objects** — one for
   the first visual row, one per continuation row (drawn with an X offset and
   reduced `MaxWidth`).  This changes `TextLayoutEngine.LayoutLines()` and
   `LayoutLine`'s structure (one-to-many), and ripples into hit-testing
   (`HitTest`), caret positioning (`GetCaretBounds`), selection rendering
   (`HitTestTextRange`), and scroll math.

2. **Clip-and-offset tricks** — render the full `TextLayout` twice with
   different clips and X offsets.  Fragile, wasteful, and breaks hit-testing.

Option 1 is the only clean approach.  It is a significant refactor of the
layout pipeline touching TextLayoutEngine, LayoutLine, LayoutResult, and
several parts of EditorControl.

### When to revisit

If the layout pipeline is ever restructured for other reasons (e.g.
variable-height lines, mixed fonts, or block-level rendering), hanging indent
could be added at that time with lower marginal cost.
