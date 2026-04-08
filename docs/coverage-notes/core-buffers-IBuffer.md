# `IBuffer`

`src/DMEdit.Core/Buffers/IBuffer.cs`
Tests: none direct (interface only).

Tiny interface — nothing much to test by itself. The interface ships
with several **default method implementations**: `GetLineStart → -1`,
`LineCount => -1`, `LongestLine => -1`, `LengthIsKnown => true`,
`IsLoaded(…) => true`, `EnsureLoaded(…) => { }`.

## Likely untested

- **No implementor actually relies on the defaults.** `StringBuffer`,
  `ChunkedUtf8Buffer`, `PagedFileBuffer`, and `StreamingFileBuffer` all
  override the first four. A mock or minimal test implementor that falls
  through to the defaults would be valuable, both as documentation and
  as regression protection if PieceTable starts depending on any of
  those defaults.

## Architectural concerns

- **`Length`, `this[long]`, and `CopyTo` are the only required members.**
  The other members are "optional hints." This is fine, but not stated
  in a single place. Worth a `<remarks>` block on the interface itself
  saying "the three required members + the `Dispose` contract are the
  whole mandatory surface; everything else is performance/metadata
  shortcuts."
- **`CopyTo(long, Span<char>, int)` with `len` as a separate parameter
  is redundant** with `destination.Length`. Every call site passes a
  matching length. Removing the `len` param would eliminate a whole
  class of potential mismatches. Minor but easy.

## Simplification opportunities

- None beyond the `CopyTo` signature cleanup.
