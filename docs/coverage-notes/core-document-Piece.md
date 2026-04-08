# `Piece`

`src/DMEdit.Core/Document/Piece.cs` (in namespace `DMEdit.Core.Documents`)
Tests: no dedicated file; exercised transitively via every PieceTable test.

20-line immutable `readonly record struct` with `BufIdx`, `Start`, `Len`,
and two helpers (`TakeFirst`, `SkipFirst`).

## Likely untested

- **`IsEmpty`** on the zero-len case — implicitly hit everywhere but no
  direct assert.
- **`TakeFirst(0)` / `SkipFirst(0)`** — edge cases. Tests pass through
  these paths but never assert the resulting record is equivalent to
  `this with { Len = 0 }` / unchanged.
- **`TakeFirst(Len)` / `SkipFirst(Len)`** — should produce an
  "everything" / "empty" piece respectively. Fine per the record-struct
  math but not asserted.

## Architectural concerns

- **Namespace mismatch.** The file's `namespace` is `DMEdit.Core.Documents`,
  but the directory is `Document/` (singular). Other files in the same
  directory (`Selection`, `ColumnSelection`, `Document.cs`) also live in
  `DMEdit.Core.Documents`. Meanwhile, several *other* Document files
  have their own namespace confusion vs the `Documents/` directory one
  level up (LineScanner, EncodingInfo, IndentInfo). See the
  `core-documents-*.md` notes. This isn't wrong — just messy.

## Simplification opportunities

- **None.** This is a boilerplate record struct; it's exactly the
  right size.
