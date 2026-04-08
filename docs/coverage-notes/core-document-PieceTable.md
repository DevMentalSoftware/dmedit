# `PieceTable`

`src/DMEdit.Core/Document/PieceTable.cs` (1 186 lines)
Tests: `PieceTableTests.cs` (~50), `BulkReplaceTests.cs` (~20),
`LargeDocumentTests.cs` (~15), plus integration in
`PagedFileBufferTests` and `ZipFileTests`.

The core storage abstraction. Pieces, line tree, bulk replace, line
splice, add-buffer management. Substantial test surface but the state
space is huge.

## Likely untested

### Line tree / incremental maintenance
- **`Insert` when `_lineTree == null`** — the `Debug.Assert(false,
  "Insert called without a line tree.")` branch at line 256. Only
  reachable by calling `Insert` before `EnsureLineTree` on a freshly
  constructed PieceTable that hasn't touched its line tree. The
  Release-build behavior is `_maxLineLen = -1` and continue — arguably
  worse than a throw; the assert is hit only in DEBUG. Worth either
  removing the path altogether (require the line tree) or converting
  to an explicit `InvalidOperationException` so Release builds behave
  consistently.
- **`Delete` with the same precondition.**
- **`InsertFromBuffer` with the same precondition.**
- **`SpliceInsertLines` at the end of the document** (no trailing
  character after the inserted text). Hit indirectly by
  `MultipleAppends_Coalesce` and others, but not with an assertion
  that the final line's length updated correctly after a multi-line
  insert at `Length`.
- **`SpliceInsertLines` with an insert that ends in a bare `\r`**
  (not followed by `\n`). Tested by `PagedFileBufferTests.TerminatorType_MixedEndings`
  indirectly, but `PieceTable` has its own bare-`\r` handling at
  line 719 that's only exercised if a live insert introduces the
  bare CR. Worth a targeted test.
- **`SpliceInsertLines` with an insert that starts with `\n` when
  the previous char in the buffer is `\r`** — does the splice
  correctly merge the CRLF? The `_prevWasCr` logic at line 706 is
  local to the scan of the inserted text, not the buffer context.
  That means an insert of `"\n"` immediately after an existing `"\r"`
  is treated as a bare `\n`, not as a CRLF merge. The line tree
  update will create an extra line. This may be the intended
  behavior (inserting "\n" at the end of a line creates a new empty
  line even if the line terminator was CR), or it may be a bug.
  Worth a test that pins the expected behavior.
- **`CaptureLineInfo` when `ofs + len == Length`** (delete to
  end-of-document). The `ofs + len < lineEnd` short-circuit on
  line 772 excludes this case, so it falls through to the multi-line
  branch. But `LineFromOfs(ofs + len)` where `ofs + len == Length`
  maps to the last line — correct. Not directly asserted.

### `EnsureInitialPiece` / lazy buffer state
- **Construction on a buffer with `LengthIsKnown == false`** —
  `EnsureInitialPiece` is only called on the first mutation/access.
  Tests use `PagedFileBuffer` with `StartLoading`+`WaitUntilLoaded`,
  which sets `LengthIsKnown` before PieceTable ever sees it. The
  *intermediate* state where `Length` is observed while the buffer
  is mid-load has no explicit test.
- **`InstallLineTree`'s "reconcile the initial piece to match the
  final buffer length" branch (lines 631-636)** — the piece was
  lazily created with a partial length and now needs correction.
  Requires a test that touches the buffer during streaming scan.

### `FindPiece`
- **`FindPiece(Length)`** — returns `(_pieces.Count, 0)`. Critical
  for `Insert` at end-of-doc. Tested via `InsertAtEnd`.
- **`FindPiece(ofs)` with `ofs > Length`** — returns
  `(_pieces.Count, 0)` as well, and the caller relies on the
  subsequent `ThrowIfGreaterThan` check to catch the out-of-range.
  Untested.
- **`FindPiece` is O(pieces)**. A line-level index (or a Fenwick
  tree over piece lengths) would make it O(log pieces), but current
  usage is dominated by single-piece-per-line documents for which
  the linear scan is fine. Low priority.

### Bulk replace
- `BulkReplaceTests.cs` has ~20 tests. Spot checks:
  - **`BulkReplace` with an empty `matchPositions`** — early return at
    line 1002/1026. Likely tested.
  - **`BulkReplaceCore` with a match at exactly `Length`** — the
    last match's `matchPos + matchLen == Length`. The final
    `CopyPiecesUpTo(long.MaxValue, …)` (line 1082) should correctly
    walk to end.
  - **Replacement with empty string (`replacement.Length == 0`)** —
    `replacementPiece` becomes `default`, `IsEmpty` is true, it is
    not added. Removes content at those positions. Worth a test.
  - **Overlapping matches** — source comment says "sorted ascending
    and non-overlapping" but no validation. If someone passes
    overlapping matches, `SkipPieces` will cross the next match's
    start offset and `CopyPiecesUpTo` will attempt to copy negative
    bytes. No test pins this. Add a precondition check (or a
    debug assert).
- **`TrimAddBuffer`** is exposed for bulk-replace undo. Tested via
  `UniformBulkReplaceEdit`/`VaryingBulkReplaceEdit` round-trips,
  but no direct test that trimming leaves the buffer in a
  consistent state for a subsequent `Append`.

### `GetText(start, len)` edge cases
- **`start + len > Length`** throws (line 378) — tested.
- **`len > MaxGetTextLength`** throws — tested by
  `LargeDocumentTests` presumably.
- **`start < 0`** throws — not directly tested.
- **`Debugger.Break()` at line 372** — intentional devmode trap.
  Harmless in prod but worth a comment.

### Line accessors
- **`LineStartOfs(lineIdx)` when `lineIdx == LineCount`** — the
  check `lineIdx >= _lineTree.Count` returns `-1`. This is the
  "off-the-end" sentinel. Callers need to handle it.
- **`GetLine(lineIdx)` when the line has no terminator and is the
  last line** — tested by `GetLine_ReturnsTextWithoutNewline`.
- **`GetLineChunk`** — no dedicated test beyond what `EditorControl`
  exercises. Used by the rendering layer. Worth a few direct tests
  (charOffset=0, maxChars=small, charOffset past end, etc.).

### Piece capture / restore
- **`CapturePieces(ofs, 0)`** returns `[]` — tested by the callers
  that short-circuit on empty selections.
- **`CapturePieces` across many pieces** (e.g. after repeated inserts
  that fragment a region). Randomized test would help.
- **`ReadPieces` on a Piece[] whose total length exceeds int.MaxValue**
  — `Math.Min(totalLen, int.MaxValue)` caps the `StringBuilder`
  capacity, then the `sb.Append` loop blindly runs. Appending past
  int.MaxValue chars throws. Very edge case.

## Architectural concerns

### Line tree lifecycle
- **Two different construction paths:**
  1. `PieceTable(IBuffer)` with `LengthIsKnown == true` → initial
     piece created in ctor; line tree built lazily on first
     `LineCount`/`LineStartOfs`/etc. access via `BuildLineTree`,
     which visits all pieces.
  2. `PieceTable(IBuffer)` with `LengthIsKnown == false` → piece
     deferred; caller must eventually call `InstallLineTree`
     with pre-computed lengths.
  These two paths have subtly different semantics (path 1 rescans
  the buffer; path 2 trusts the pre-computed data). Worth a
  class-level comment explaining which is which.
- **Four methods can update `_lineTree` post-mutation:**
  incremental `Update` (non-newline), `SpliceInsertLines` (newline
  insert), `SpliceDeleteLines` (newline delete), `RestoreLines`
  (undo), `ReinsertedNonNewlineChars` (single-line undo). Each one
  has its own maintenance rules for `_maxLineLen`. The invariant
  "`_maxLineLen == _lineTree.MaxValue()`" is only checked in
  `AssertLineTreeValid` (DEBUG), and that check doesn't actually
  verify `_maxLineLen` — only the total sum. **Strong suggestion:
  drop `_maxLineLen` entirely and make `MaxLineLength` call
  `_lineTree.MaxValue()` every time.** O(1) already, no caching
  needed, and a whole class of "forgot to update `_maxLineLen`"
  bugs goes away. Only one caller actually asks for it
  (`PieceTable.MaxLineLength` getter), so the cache has marginal
  value.

### State sprawl
- **`HasLongLines` is sticky** and set whenever `_maxLineLen >
  LongLineThreshold`. Used for CharWrap triggering. If the user
  edits the long line back down to a normal length,
  `HasLongLines` stays true. Intentional per the source comment,
  but non-obvious to callers.
- **`_initialPieceResolved`, `_addBufIdx`, `_buffers`,
  `_pieces`** — construction sequence is subtle. `_buffers[0]` is
  the original, `_buffers[_addBufIdx]` is the active add buffer,
  but `_addBufIdx` is a field not a computed prop. If a future
  refactor adds a third buffer before the add buffer, the index
  drifts. A comment at the field explains, but the invariant isn't
  enforced.

### Duplication with `InsertFromBuffer` vs `Insert`
- `Insert(ofs, string)` and `InsertFromBuffer(ofs, bufIdx, bufStart, len)`
  share ~50 lines of structure. The difference: `Insert` appends to
  the add buffer first; `InsertFromBuffer` takes an existing buffer
  span. Could be one private method + two thin public wrappers.
  The newline-scan block at 913-928 in `InsertFromBuffer` duplicates
  the `hasNewlines` detection that `Insert` does via
  `text.AsSpan().IndexOfAny('\n', '\r')` at line 217 — the former
  reads a `char[]` chunk, the latter operates on the input string.
  A single helper taking a visitor span would consolidate.

### SpliceInsertLines state machine
- The `prevCr` / scan loop at lines 706-722 is a bespoke copy of
  `LineScanner`'s logic. Same issue as `StreamingFileBuffer` and
  `StringBuffer`. Unify on `LineScanner`.

## Simplification opportunities

- **`GetText()` no-arg** at line 345 — flagged "test-only" in the
  comment, made `internal`, still used in many places. Could be
  moved to a test helper extension method. Minor.
- **`CaptureLineInfo`'s `(int StartLine, int[] LineLengths)?`
  return** — consider a named struct so call sites can pattern-match
  by name. `is var (sl, ll)` at line 1105 is clever but opaque.
- **`EmptyBuffer`'s `this[offset] => throw`** — correct for a
  zero-length buffer but surprising if someone else ever constructs
  one. The `Length == 0` guard at the caller should catch all real
  uses.

## Bugs / hazards

1. **`_maxLineLen` is a cached duplicate** of `_lineTree.MaxValue()`.
   See "state sprawl" above. Biggest concrete risk in the class —
   silent drift between cache and truth.
2. **Missing line tree in `Insert`/`Delete`/`InsertFromBuffer`** is
   handled by a `Debug.Assert(false)` + `_maxLineLen = -1`
   fallthrough. Release builds silently continue with wrong state.
3. **Overlapping or unsorted matches in `BulkReplace`** have no
   precondition check and will corrupt the piece list. Add one.
4. **Bare-CR insert + subsequent LF insert** may not merge into a
   CRLF in the line tree, even though the buffer contains a CRLF
   sequence. Needs verification, then either a fix or a comment.
5. **`TrimAddBuffer` is exposed publicly** but the PieceTable has
   no way to know whether the trimmed range is still referenced by
   any piece. The contract is "only the bulk-replace undo path may
   call this, and it must first have restored the piece list to
   the pre-replace state." No enforcement. Worth an `internal` or
   a documented `[Obsolete("Internal-only")]`-ish contract.
