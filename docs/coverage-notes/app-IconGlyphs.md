# `IconGlyphs`

`src/DMEdit.App/IconGlyphs.cs` (84 lines)

Static class holding the Fluent UI System Icons font reference
and ~50 glyph code constants.

## Likely untested
- Constant declarations. Trivial.
- **Font loading** — avares://dmedit/Resources/... — if the
  resource is removed, all icon buttons show fallback glyphs.
  Worth a smoke test that the font face is non-null on
  startup.

## Architectural concerns
- **Hard-coded font URI** — tied to the bundled resource.
  Fine.
- **Manual char code constants** — when updating the font,
  each glyph's code must be re-verified. A code-generator
  from the font's metadata.json would be safer.
