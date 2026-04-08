# `BlockType` (enum)

`src/DMEdit.Core/Blocks/BlockType.cs`

14-value enum covering paragraph, headings 1-6, code, blockquote,
lists (ordered/unordered), horizontal rule, image, table.

## Likely untested
- Not directly tested (enum). Consumers test through Block tests.

## Architectural concerns
- **No `List` parent type** — `UnorderedListItem` and
  `OrderedListItem` are siblings, not a parameterized "List(ordered)"
  variant. Fine for now.
- **`Image` and `Table` are declared but likely unimplemented.**
  The Block model isn't wired into production anyway, but these
  enum values will eventually need corresponding rendering support.
