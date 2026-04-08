# `ChunkedUtf8Buffer`

`src/DMEdit.Core/Buffers/ChunkedUtf8Buffer.cs`
Tests: `tests/DMEdit.Core.Tests/ChunkedUtf8BufferTests.cs` (~40 tests,
recently beefed up around the ASCII fast path — entry 21 — and the
forward-progress safety net — entry "Surrogate-pair safety" in the
journal).

This is the Add-buffer for PieceTable: append-only UTF-8 chunks with a
char-offset public API. Test coverage is genuinely good, but several
small holes remain.

## Likely untested

- **`AppendUtf8` that forces the "can't fit even one complete sequence"
  branch at line 140.** This path walks back the `take` index past any
  continuation bytes and, if `take == 0`, allocates a fresh chunk and
  retries. Hitting it requires a boundary where the last UTF-8 sequence
  in the incoming buffer is a 4-byte codepoint that doesn't fit in the
  current chunk's remaining space but does fit in a new chunk. Low
  probability but the code is there.
- **`AppendUtf8` with a first byte `< 0x80` pseudo-"safety net" — N/A**
  (there is none; the branch is only in the multi-byte split case).
- **`IndexOfAny` with non-ASCII targets, within a sub-range that starts
  mid-way into a previous chunk's non-ASCII content.** Tests use ASCII
  targets plus one non-ASCII-target test, but the latter searches from
  offset 0.
- **`Visit` on a range that spans more than one chunk** (requires more
  than ~64 KB of appended content before the sub-range of interest).
  There's a large-multi-chunk `CharAt` test but not a `Visit` equivalent.
- **`TrimToCharLength` to exactly `_totalChars`.** Line 364 has a guard
  `if (newCharLen >= _totalChars) return;`, but "equal" and "greater"
  share a branch; a direct test would pin the semantics.
- **`TrimToCharLength` to a boundary that lands exactly at the end of a
  non-first chunk.** The cursor reset (line 396) covers this, but the
  `chunk.BytesUsed = bytePos` where `bytePos == chunk.BytesUsed` is an
  unusual edge.
- **Cursor cache invalidation on `TrimToCharLength`.** The cursor is
  reset (good) — covered indirectly by the fact that reads after trim
  work. A direct assertion that a subsequent random-access read works
  after a trim that invalidated the cursor's previous `_cursorChunk`
  would be worth adding.
- **`CharLength` == `ByteLength` invariant for all-ASCII input.** Used
  informally in tests but not asserted as a property across a big
  randomized append run.
- **The `DecodeUpToNChars` safety net (lines 647-659) that emits U+FFFD
  for unsplittable head sequences.** Tested via `CopyTo` / `Visit` /
  `GetSlice` / `IndexOfAny` shape tests, but there is no unit test of
  `DecodeUpToNChars` itself (it's private). That's OK because you don't
  test private methods; the important test is that each caller hits
  the safety net. Each caller has at least one test that asks for an
  odd char count across a surrogate pair — coverage is roughly complete.
- **`WriteTo(Stream)` after `TrimToCharLength` that straddled a chunk.**
  Ensures trimming didn't leave garbage bytes past `BytesUsed`. Only
  the basic `WriteTo` after append is tested.

## Architectural concerns

- **The cursor cache is invalidated by `ResetCursor()` but only after
  successful trims.** If a caller interleaves `Append` and random
  `CharAt`, the cursor is advanced but never invalidated by appends —
  because `Append` only adds content after the cursor's tracked range,
  so the existing cursor remains valid. Correct, but not obvious from
  the code. Worth a short comment on `ResetCursor` explaining when it
  is and isn't necessary.
- **`CharAt` on a surrogate pair returns only the first code unit** and
  silently loses the second — see line 187 `return result[0];`. This
  matches the intent (`CharAt` is a single-UTF-16-code-unit API), but
  the loss of the low surrogate is a trap for any future caller that
  assumes "one char per call covers the whole codepoint." Worth a
  `<remarks>` block explicitly saying "surrogate pair: the high surrogate
  is returned at index N, the low surrogate at index N+1; both are
  valid UTF-16 code units."
- **`FindByteOffset`'s cursor advancement path** (lines 468-484) does a
  linear scan from `_cursorChunk + 1` to find the right chunk. For
  dense, sequential reads that's fine, but a random-seek pattern that
  keeps jumping forward by more than one chunk burns O(chunks)
  complexity per call. Low priority for current usage.
- **`DecodeUpToNChars`'s duplicated UTF-8 length table.** The same
  switch on `(b & 0xE0) == 0xC0` / `(b & 0xF0) == 0xE0` is repeated in
  `CharAt`, `FindByteOffsetInChunk`, `IndexOfAnyAsciiInBytes`, and
  `DecodeUpToNChars`. Four copies. A small `static (int seqLen, int
  charsProduced) Utf8LeadByte(byte b)` helper would consolidate them.
  Mildly risky because that helper is on the hot path; marking it
  `[MethodImpl(AggressiveInlining)]` should neutralize it.

## Simplification opportunities

- **`IsAllAscii` is set per-chunk on append** but never re-checked on
  `TrimToCharLength`. This is intentional (see the test
  `Trim_NonAsciiChunkBackIntoAsciiPrefix_KeepsSlowPath`) — re-enabling
  the flag would require a full rescan. The asymmetry is fine. Worth a
  short comment linking to the test that pins the decision.
- **`ResetCursor` is trivial** and only called in two places. Could
  inline, but leaving it named documents intent; keep.
- **`AppendUtf8` and `Append` share a lot of "grow the chunk, encode,
  track totals" skeleton.** Not obviously worth unifying — the encode
  step differs meaningfully. Leave.

## Bugs / hazards

- **No obvious bugs.** The surrogate-pair safety net in `DecodeUpToNChars`
  has had a regression history (see the journal for the hang that led
  to it), so any touch to that method must re-run the sweep tests at
  lines 494-517. A big bold comment on that effect wouldn't hurt.
