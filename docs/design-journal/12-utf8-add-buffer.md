# 12 — UTF-8 Chunked Add Buffer (2026-03-29)

## Motivation

Pasting large content from the native clipboard caused excessive memory usage.
`StringBuilder _addBuf` stored UTF-16 (2 bytes/ASCII char), and `_addBufCache`
materialized the entire buffer as a second string copy on first read. A 50MB
ASCII paste consumed ~200MB.

## ChunkedUtf8Buffer

Replaced `StringBuilder _addBuf` + `string? _addBufCache` with a new
`ChunkedUtf8Buffer` class (`Core/Buffers/ChunkedUtf8Buffer.cs`).

**Storage:** List of variable-size `byte[]` chunks holding UTF-8 encoded text.
Pieces continue to store char offsets; the buffer translates char<->byte internally.

**Growth strategy:**
- Initial chunk: 64KB (covers typical typing sessions).
- Growth chunks: 1MB when the current chunk fills.
- Large paste: one chunk sized to fit (e.g., 500MB paste = one 500MB chunk).
- Wasted space in abandoned chunks is negligible vs. the data they hold.

**Char->byte translation:** Binary search on cumulative char counts per chunk to
find the right chunk, then byte-level UTF-8 sequence scanning within the chunk.
Sequential-access cursor cache avoids repeated binary search for forward scans.

**Key APIs (all char-offset based):**
- `Append(ReadOnlySpan<char>)` / `AppendUtf8(ReadOnlySpan<byte>)` — returns char start
- `CharAt`, `CopyTo`, `Visit`, `GetSlice`, `IndexOfAny` — read operations
- `TrimToCharLength` — for bulk-replace undo
- `WriteTo` / `ReadFrom` — binary persistence

## PieceTable changes

- Removed `BufFor()` method and `_addBufCache` field entirely.
- All Add-buffer read paths now go through `ChunkedUtf8Buffer` methods directly.
- Added `AddBuffer` property and `SetAddBuffer()` for session persistence.
- No changes to `Piece` struct, piece offsets, or document-level APIs.

## Session persistence

Binary companion file (`{id}.addBuf`) alongside `.edits.json`:
- Edit JSON uses `"bufInsert"` type with `bufStart`/`bufLen` char offsets instead
  of materializing text strings for inserts.
- On restore, load `.addBuf` into a `ChunkedUtf8Buffer`, set it on the PieceTable,
  then replay edits via `InsertFromAddBuffer`.
- Backward-compatible: old `"insert"` entries with text still work (append to add
  buffer during replay as before).

## Future direction: paged eviction

Since chunks are append-only and immutable once the buffer moves to a new chunk,
they can be written to disk in the background and evicted from memory — same
pattern as `PagedFileBuffer` for the Original buffer. The active (latest) chunk
stays in memory; older chunks become paged references that re-read from disk on
demand. This would:
- Make session save near-instant (only flush the current chunk's tail).
- Reclaim memory for old edited content after writing to disk.
- Unify the read path for Original and Add buffer pieces.

The `ChunkedUtf8Buffer` chunk list is already the natural unit for this. A
`PagedChunk` variant where `Data` is null (evicted) would re-load on access.
