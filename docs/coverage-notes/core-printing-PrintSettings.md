# `PrintSettings` (+ `PrintMargins`, `PageRange`, `PageOrientation`, `PaperSizeInfo`)

`src/DMEdit.Core/Printing/PrintSettings.cs` (102 lines)

## Likely untested

- **`GetPrintableArea` / `GetPageSize` with Landscape orientation**
  — swaps width/height. Tested?
- **`GetPrintableArea` with margins that exceed the paper size**
  — returns negative dimensions. Not clamped. Consumer's
  responsibility.
- **`PaperSizeInfo.Letter`/`A4`/`Legal` constants** — trivial
  factory properties.
- **`Defaults` list** — also trivial.
- **`PageRange` validation** — `From > To` is legal but semantically
  invalid. No guard.
- **`PrintMargins.Default`** — 1-inch all sides. No asserts.

## Architectural concerns
- **`Paper`, `Orientation`, `Margins`, `Range` are mutable** on
  an otherwise-immutable-looking PrintSettings. They persist on
  Document.PrintSettings. Consider `with`-based immutability
  for consistency.
- **`PaperSizeInfo` is a `sealed class` with `required` init-only**
  — makes it awkward to construct programmatically. Factory
  properties like `Letter`, `A4`, `Legal` paper over this, but
  a custom paper size still requires the full `new PaperSizeInfo
  { Name = ..., Width = ..., Height = ... }` song.
- **`Id` is nullable** because fallback/built-in sizes don't have
  a platform identifier. Works, but the print service has to
  know what to do with null.
