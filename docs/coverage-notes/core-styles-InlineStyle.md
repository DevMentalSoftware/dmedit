# `InlineStyle`

`src/DMEdit.Core/Styles/InlineStyle.cs` (30 lines)

POCO with nullable override properties. `null` = inherit.

## Likely untested
- None — trivial POCO.

## Architectural concerns
- **`Strikethrough` and `Underline` are non-nullable bools** —
  false doesn't inherit; it means "explicitly off." If the block
  style had strikethrough on and the span wants it off, there's
  no way to say "inherit off." Minor; probably fine.
- **No `FontSize` override** — inline styling can't resize text.
  By design? Worth a comment.
