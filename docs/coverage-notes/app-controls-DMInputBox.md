# `DMInputBox`

`src/DMEdit.App/Controls/DMInputBox.cs` (720 lines)
Tests: `tests/DMEdit.App.Tests/DMInputBoxTests.cs` (~26 tests).

Single-line text input. Replaces Avalonia's TextBox for all
editor chrome to work around Avalonia #12809 (caret positioning
bug). Renders text/caret/selection directly via `TextLayout`.

## Likely untested

### Things `DMInputBoxTests` does cover
- Property defaults, two-way binding, text coercion
  (null→empty)
- Caret / selection clamping on text shrink
- SelectAll, SelectedText (forward + reversed), IsReadOnly
- CaretWidth default, Watermark, caret positioning

### Things not covered
- **Render** — visual regression is manual.
- **Pointer events** — click to position caret, drag to
  select, double-click to select word (if implemented).
- **Keyboard handling** — arrows, Home, End, Ctrl+A, Shift
  selection, typing, backspace, delete. The tests check
  property-level behavior but not the input pipeline.
- **Clipboard operations** — cut, copy, paste. Via
  Avalonia's clipboard or native?
- **Focus / caret blink** — blink timer, focus lost → caret
  hidden.
- **`Watermark` rendering** when text is empty. Not
  asserted visually.
- **IME / composition input**.
- **Font family switching at runtime** — TextLayout
  reconstruction.
- **Horizontal scrolling** when text exceeds the box width
  — the caret should stay visible. Does the box support
  horizontal scroll at all?
- **`IsReadOnly` behavior during input** — typing should be
  blocked; caret/selection still move. Tested as a property
  default only.

## Architectural concerns

- **720 lines of duplication with `EditorControl`** (in
  spirit). Each controls its own caret blink, own text
  layout, own hit test. Some overlap with `EditorControl`'s
  small-text paths. Consider a shared `SimpleTextLayout`
  helper.
- **Styled properties** for everything — standard Avalonia
  pattern. OK.
- **No distinct selection brush default** — falls back to a
  hard-coded color? Not obvious from the surface.
- **Owns its own `TextLayout`** — rebuilt on text change.
  Caching the layout across cosmetic property changes
  (background, border) would avoid unnecessary work.
- **Single-line only** — `\n` / `\r` in Text are presumably
  stripped or rendered as glyph boxes. Not specified in the
  public surface.

## Recommendation

- **Add an input-pipeline test** using Avalonia.Headless to
  simulate key presses and assert resulting text / caret
  state.
- **Migration to the mono GlyphRun fast path** once it
  stabilizes would make DMInputBox render identically to
  the editor (addressing the "EditorControl caret X offset"
  journal item).
