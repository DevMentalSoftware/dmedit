# `InlineSpanType` (enum)

`src/DMEdit.Core/Blocks/InlineSpanType.cs`

5-value enum: `Bold`, `Italic`, `InlineCode`, `Strikethrough`, `Link`.

## Likely untested
- Trivial enum.

## Architectural concerns
- **`Link` is the only type that uses the `Url` field** on
  `InlineSpan`. An enum-specific payload isn't possible in C#;
  the alternative is a sealed hierarchy (`abstract record
  InlineSpan`, `LinkSpan : InlineSpan(..., string Url)`, etc.).
  Overkill for five types.
