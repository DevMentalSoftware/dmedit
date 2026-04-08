# `EditSerializer`

`src/DMEdit.App/Services/EditSerializer.cs` (238 lines)
Tests: `tests/DMEdit.App.Tests/EditSerializerTests.cs`.

Serializes / deserializes `IDocumentEdit` instances for session
persistence. Handles SpanInsert, Delete, Compound, Uniform /
Varying bulk replace.

## Likely untested

- **Edit types added after this file was last touched** — if a
  new `IDocumentEdit` subclass ships, `EditSerializer` would
  silently skip it. A smoke test "every concrete
  `IDocumentEdit` type has a serializer case" would catch it.
- **Malformed JSON round-trip** — missing required field.
- **Backward compat** — JSON shape evolution. No version field;
  a newer file loaded by an older app silently breaks.
- **Oversized DeleteEdit** (the "text omitted" path) — tested
  per the `DeleteEdit.md` note; serializer must handle the
  `_text = null` case on write.
- **VaryingBulkReplaceEdit with a `(Pos, Len)` tuple array** —
  JSON tuple serialization.
- **CompoundEdit nested in CompoundEdit** — recursion.

## Architectural concerns

- **Per-edit-type switch/case** — new edit types require
  touching this file. Would be avoided by having each edit
  type implement `ToJson`/`FromJson` on the interface (see
  `IDocumentEdit.md`).
- **Tightly coupled to PieceTable internals** (buffer
  indices, add buffer offsets). If the buffer model changes,
  the serializer breaks silently.
