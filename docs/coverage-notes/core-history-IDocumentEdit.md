# `IDocumentEdit`

`src/DMEdit.Core/Document/History/IDocumentEdit.cs`

7-line interface: `Apply(table)` and `Revert(table)`.

## Likely untested
- Nothing at the interface level.

## Architectural concerns
- **No serialization contract.** Each edit type invents its own
  JSON shape in `EditSerializer`. A `WriteTo(Utf8JsonWriter) /
  ReadFrom(Utf8JsonReader)` pair on the interface would localize
  the format; currently adding a new edit type requires touching
  three files (the edit, the serializer, and the dispatcher).
  Lower priority if no new edit types are planned.
- **`Apply` and `Revert` must be inverses.** There is no test
  harness that, for a random document + random edit, asserts
  `Apply → Revert → Apply` is a fixed point. A property test over
  `SpanInsertEdit`/`DeleteEdit`/`CompoundEdit` would catch a whole
  class of line-tree-splice bugs.
