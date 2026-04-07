# 21 — ChunkedUtf8Buffer ASCII Fast Path

**Date:** 2026-04-07
**Status:** Implemented

---

## Problem

Column-mode rapid insert with many cursors had been slow since we introcuded
the ChunkedUtf8Buffer feature.  A 30-cursor × 200-char-line × hold-key test ran
at ~3 inserts/sec in Release on master (~280ms wall clock per insert).

## How we found it

Worked through a long process of elimination over two days:

1. Initial guess: the new caret-layer code in entry 20 was forcing
   `MaterializeCarets` to run from `ArrangeOverride` per insert.  Built
   `PerfLog` (file logger) and instrumented `OnTextInput`,
   `MeasureOverride`, `ArrangeOverride`, and `UpdateCaretLayers`.  Found
   that `MaterializeCarets` took ~22ms per insert, but only ~14% of the
   total — not the dominant cost.
2. Second guess: GC pressure.  Added Gen 0/1/2 counters and
   `LayoutInvalidations`/`RenderCalls` to the stats bar.  Confirmed
   essentially zero GC escalation during the slow path — not GC.
3. Third guess: render starvation by the input pump.  Confirmed
   coalescing was actually working fine via the Inv/Rnd ratio.
4. Tried a `ColToCharIdx` fast-path optimization (`IndexOf('\t')` over
   the line) — got 14% improvement, real but small.  Tried extending
   to `OfsToCol` and `PaddingNeeded` and got *worse* results because of
   per-call closure allocation in `ForEachPiece`.
5. Finally bisected by switching to master: master was *also* slow,
   ruling out the entire spike branch.
6. Ran a CPU profile of master under the same workload.  Top function:
   `ChunkedUtf8Buffer.FindByteOffset(long)`.

That last step is the one that mattered.  All the earlier guesses were
investigating the *callers* of `CharAt` when the actual cost was *inside
`CharAt`*.  Every char-offset → byte-offset translation in the buffer
walked UTF-8 from the start of a chunk, and a 6 KB chunk meant up to
6,000 bytes scanned per `CharAt` call.  With ~30 cursors × ~5 calls per
cursor per insert, that's ~30,000 walks per character of input — and
every walk grew with line length because the chunks accumulated content
as you typed.

## Fix

A per-chunk `IsAllAscii` boolean.  Set true on chunk allocation, cleared
the moment a multi-byte UTF-8 sequence is appended (detected via
`bytesUsed != charsUsed` in the encoder result).  `FindByteOffsetInChunk`
returns `charIndex` immediately when the flag is true:

```csharp
private static int FindByteOffsetInChunk(ref Chunk chunk, int charIndex) {
    if (charIndex == 0) return 0;
    if (charIndex == chunk.CharCount) return chunk.BytesUsed;
    if (chunk.IsAllAscii) {
        return charIndex;  // 1 byte == 1 char, no scan needed
    }
    // ... existing UTF-8 decode loop ...
}
```

The flag is monotonic: once cleared, it stays cleared.  `TrimToCharLength`
(used by bulk-replace undo) never needs to re-set it because trimming
data from the end of an already-non-ASCII chunk leaves it potentially
non-ASCII.  Setting back to true would require a full re-scan and would
lose more than it gains.

The "all ASCII" predicate is "all bytes < 0x80", which includes tab,
newline, carriage return, and every printable ASCII char.  Tab characters
do not affect `IsAllAscii` — they're 0x09 and well within the fast path.
Tab-aware visual column logic in `ColumnSelection.ColToCharIdx` is a
separate concern at a higher layer that operates on whatever `CharAt`
returns; the buffer's job is just "give me the character at offset N."
So the code paths are 2 (ASCII chunk vs non-ASCII chunk), not 4.

## Measured impact

| Phase | Master baseline | + ColToCharIdx fix | + ASCII fast path |
|---|---|---|---|
| Layout | 41ms | 43ms | **0.46ms** |
| Render | 106ms | 87ms | **0.60ms** |
| Edit | 217ms | 187ms | **12.31ms** |
| Total/insert | ~360ms | ~320ms | **~13ms** |

The total per-insert work dropped by ~28×.  Edit alone dropped by
~18×.  Render and Layout collapsed by ~100× — both call `CharAt`
indirectly through various layout/draw helpers and were paying the
same buffer-walk cost on every visible line on every frame.

This isn't just "faster than master."  The editor can now keep up
with key-repeat rate in 30-cursor column mode, which it could not
before.  It also makes every other operation in the app that touches
the buffer faster: load, search, replace, scroll, render, gutter
drawing — all of them call into `CharAt` somewhere and all of them
benefit.

## Why this was hidden for so long

`CharAt` had a per-byte ASCII fast path (`if (b < 0x80) return (char)b`)
that made it look fast in microbenchmarks of single-character reads.
The slow part was the **lookup** before the read — `FindByteOffset`
needs to know which byte in the chunk corresponds to the given char
index, and that's where the UTF-8 walk happened.  Reading the byte
once you know its position is fast.  Finding the position was the
problem.

The cursor cache in `FindByteOffset` (which fast-paths sequential access
within a chunk) hid the issue from any test that read sequentially.
Column-mode editing accesses 30 different positions per insert in
arbitrary order — the cursor cache thrashes constantly and every call
falls into the slow `FindByteOffsetInChunk` path.

## Code paths now multiplied by 2

This makes correctness testing more important.  Every code path that
goes through the buffer must be verified against both:

1. **All-ASCII chunk**: `IsAllAscii == true`, `FindByteOffsetInChunk`
   takes the fast return.
2. **Non-ASCII chunk**: at least one byte ≥ 0x80 anywhere in the chunk,
   `FindByteOffsetInChunk` takes the UTF-8 walk path.

The dividing line isn't "the character you're asking about" — it's "any
byte anywhere in the chunk."  A 6 KB chunk that's mostly ASCII but has
a single emoji at byte 5,000 is *not* fast-pathable for a `CharAt(0)`
call, because we don't know in advance whether the chunk is entirely
ASCII without keeping the flag.

Test coverage added in `tests/DMEdit.Core.Tests/Buffers/ChunkedUtf8BufferTests.cs`:
- ASCII-only content: append, read, copy, IndexOf, slice
- Non-ASCII content (Latin-1, BMP, supplementary plane / surrogate pair)
- Mixed: an ASCII chunk that becomes non-ASCII mid-append
- Boundary: a chunk that's exactly all ASCII vs one byte beyond
- Trim: trimming a non-ASCII chunk back into the ASCII prefix does
  *not* re-flag the chunk as ASCII (we'd lose the precondition that
  "any byte" was the predicate).

## Files touched

- `src/DMEdit.Core/Buffers/ChunkedUtf8Buffer.cs` — `Chunk.IsAllAscii`
  field, set in `AllocateChunk`, cleared in `Append` and `AppendUtf8`,
  fast-return in `FindByteOffsetInChunk`.
- `tests/DMEdit.Core.Tests/Buffers/ChunkedUtf8BufferTests.cs` — new
  test cases for both code paths.

## Consequences

This is a pure buffer-level byte-offset optimization.  It has no effect on
rendering, input handling, text shaping, or anything the user sees.  Tab
characters (0x09) stay on the fast path — the predicate is "all bytes <
0x80", and every ASCII control character including tab is well below that.
Ligatures, emoji, and TextLayout fallback are concerns of the rendering
layer (the `MonoLineLayout` monospace GlyphRun fast path) and are
discussed in entry 20.

The only real cost of this change is the one already covered above: every
buffer code path must now be correct against both an all-ASCII chunk and
a non-ASCII chunk, so test coverage needs to exercise both branches.


## Follow-ups deliberately not done

- **`PieceTable.FindPiece` cursor cache** — second-highest function in
  the original CPU profile.  Same idea as `ChunkedUtf8Buffer`'s cursor
  cache: remember the last-found piece, check it first.  Smaller win
  now that `FindByteOffset` is cheap, but still worth doing if any
  workload exercises it.
- **`FindFirstTab` closure elimination** — the original `ColToCharIdx`
  fast path still has the closure-allocation issue we identified
  earlier in the day.  It's now invisible because the underlying
  `CharAt` is fast, but the allocation is still happening.  Worth a
  zero-alloc rewrite (`ref struct` enumerator) at some point to clean
  up; not urgent.
- **`MaterializeCarets` deduplication** in `Document.InsertAtCursors` —
  same story.  Was blocking the previous-day perf work, no longer
  blocking anything.  Still a clean-up worth doing.
- ** No hanging indent feature for lines that fall back to TextLayout** — the fallback is now more
  common because of the ASCII fast path, and the lack of hanging indent
  on those lines is more visible.  Adding hanging indent support to
  TextLayout is possible but non-trivial; worth doing eventually but
  not a quick fix.

