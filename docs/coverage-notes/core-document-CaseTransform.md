# `CaseTransform` (enum)

`src/DMEdit.Core/Document/CaseTransform.cs`
Tests: `DocumentTests.TransformCase_*` (6 tests).

3-value enum: `Upper`, `Lower`, `Proper`. Trivial.

## Likely untested

- Nothing at the enum level. The consumer is `Document.TransformCase`,
  which has tests for all three values.

## Architectural concerns

- **"Title" would be a more standard name than "Proper"** — most
  systems call this "title case." Enum member name is already
  baked into some call sites but not user-visible unless it appears
  in a menu label; the rename is mild and worth doing for clarity.
- **No `None` member.** If any caller ever wants a default "don't
  transform" option, they'll invent one. Currently unneeded.
