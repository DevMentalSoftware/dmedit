# `PagedFileBuffer`

`src/DMEdit.Core/Buffers/PagedFileBuffer.cs`
Tests: `tests/DMEdit.Core.Tests/PagedFileBufferTests.cs` (~30 tests
covering small file, BOM detection, CRLF, line-start sampling, page
eviction, concurrent access, terminator-type tracking).

The load-bearing buffer for production: paged background scan, LRU
cache, per-page SHA-1, on-demand re-read. Test surface is decent but
the class has a lot of state and several corners are unguarded.

## Likely untested

### LRU behavior
- **`PromoteLru` semantics** — repeated access to page N should keep it
  at the head of the LRU and protect it from eviction. No test asserts
  "after touching page X, X is kept and the stalest page is the one
  evicted." The eviction-loop test just checks that data is readable
  end-to-end.
- **Eviction ordering when the cache is exactly at capacity and a new
  page arrives**, verified by which specific page index gets evicted.
  Exposing `internal int GetLoadedPageCount()` or `internal bool
  IsPageLoaded(int)` for testing would let this be asserted cheaply.
- **`EnsurePageLoaded` race where two threads both miss the cache**
  for the same page. The "double-load" check at line 587 handles this,
  but it's only reached under concurrency and the concurrent test
  hammers random offsets — not specifically the same page from two
  threads at once.

### Scan lifecycle
- **`ScanError` propagation.** Not tested. If the background scan
  throws (unreadable file, permission denied, decoder error), the
  buffer should surface it via `ScanError` and still fire `LoadComplete`.
  Easy to exercise with `File.SetAttributes` + `ReadOnly` or by
  pointing the scanner at a locked file.
- **Dispose mid-scan.** Cancels the CTS, but no test kicks off a
  load, immediately disposes, and asserts that nothing blows up. The
  buffer's file-reopen-per-page design makes this safer than the old
  shared-`_fs` version, but still worth a smoke test.
- **`TakeLineLengths` called twice** — second call should return null
  because the reference was cleared. Not asserted.
- **`TakeTerminatorRuns` called twice** — same issue.
- **`Sha1` is non-null after `LoadComplete` and matches an
  independently-computed hash.** Load the file, await, compute SHA-1
  of the raw bytes yourself, assert match. Currently nothing verifies
  the hash value at all.

### File/encoding edge cases
- **File shorter than 3 bytes (BOM read returns 1 or 2 bytes).**
  `DetectEncodingWithBom` branches on `bomRead >= 3` and `bomRead >= 2`
  — the `bomRead == 1` / `bomRead == 0` path falls through to "no BOM,
  UTF-8, rewind". Untested.
- **File that starts with bytes equal to `EF BB` (2 of 3 UTF-8 BOM
  bytes) followed by something else.** Should be treated as no BOM.
  Not directly tested.
- **File with a multi-byte UTF-8 sequence spanning the 1 MB page
  boundary.** The decoder is `stateful` so the scan path handles this,
  but `LoadPageFromDisk` creates a **fresh** decoder per page, which
  the source comment calls out as "boundary chars might be slightly
  off in practice." No test verifies that a codepoint sitting at
  byte offset 1 048 575/1 048 576 of the file round-trips correctly
  after its page is evicted and re-read.
- **UTF-16 BE with a byte count that's not a multiple of 2.** Pager
  reads 1 MB chunks; if the file length is odd and the last page is
  UTF-16, the final decoder flush has to handle a dangling byte.

### `FindPageForCharOffset`
- **Binary search correctness at page boundaries.** Not explicitly
  tested at `page.CharStart + page.CharCount - 1` (last char of a page)
  and `page.CharStart` (first char of next page) as a pair.
- **Race between `FindPageForCharOffset` and `EnsurePageCapacity`
  growing `_pages`.** The "snapshot reference" comment on lines 506-507
  is load-bearing: the snapshot is an atomic assignment, but the index
  `lo` may have been computed against the new `count` while the array
  reference was the old one. Actually the snapshot is done at lines
  506-507 **before** the binary search, so both `count` and `pages`
  are from the same moment — it works, but it's fragile. A
  comment explaining the "read count, read array ref, use both
  consistently" sequence wouldn't hurt.

### `EnsureLoaded`
- **`EnsureLoaded` on a range that's already in memory.** Should be a
  no-op (the `_pageData[i] == null` check). Not asserted.
- **`EnsureLoaded` on a range beyond the currently-scanned frontier.**
  Source just returns (line 272). Behavior documented, not tested.

## Architectural concerns

- **State sprawl.** The class has three separate locks/synchronization
  mechanisms: `_lock`, `_cts`, `_loadedEvent`, plus `Interlocked` for
  `_lineCount`/`_totalChars`, plus `volatile` for `_pageCount`,
  `_scanFrontier`, `_done`, `_longestLine`. It's correct, but the
  rules for which guard protects which field are only implicit. A
  "synchronization invariants" block at the top of the class would
  help any future edit.
- **Line-lengths storage is a `List<int>`** (line 73) that lives until
  `TakeLineLengths` is called. After the caller takes it, the field is
  nulled. If another `GetLineStart` comes in after that, it returns
  `-1` (line 195). This is intentional — `PieceTable.EnsureLineTree`
  is expected to take ownership — but it creates a subtle
  post-transfer state that's hard to reason about. A comment at
  `TakeLineLengths` explaining "after this is called, GetLineStart
  will return -1 forever" would help.
- **`GetLineStart` is O(lineIdx)** (line 197-199 sums line lengths one
  by one under the lock). For large-line-count files with random-access
  lookups before `TakeLineLengths` is called, this is O(n) per call —
  pathological. In practice `PieceTable` takes the list immediately
  after load and builds a treap, so the O(n) window is brief, but
  there is no assertion that `GetLineStart` is only hit during the
  brief window.
- **Two line-tracking fields** — `_lineCount` (long, interlocked)
  duplicates what `_lineLengths.Count` knows. Kept separate so readers
  can observe it without the lock. Subtly racy if they're allowed to
  drift by one for a read; currently they don't, because
  `AppendLineStart` writes the list first then bumps the count. Worth
  documenting at the field.
- **Terminator runs and line lengths are both "borrowed by the caller
  post-load"** — two different Take methods, two different fields, two
  different transfer protocols. Could be one struct "LoadedScanResult"
  returned by a single `TakeScanResult()` call. Would remove a class
  of "forgot to take one of them" bugs.

## Simplification opportunities

- **`EnsurePageCapacity` doubles or grows to `needed`.** Could use
  `Array.Resize`. Micro.
- **`_pageData` is `char[]?[]`** — awkward syntax. A
  `(PageInfo info, char[]? data) struct` array would couple the two
  into a single indexer and remove the parallel-array hazard. Mild
  refactor.
- **`_lruList`/`_lruNodes` as a pair is standard LRU** but .NET 10 has
  no built-in `LinkedHashMap`. The current approach is correct; a
  move to `OrderedDictionary<int, char[]>` (.NET 9+) would cut one of
  the two collections. Consider when the minimum target allows it.
- **`Chars(int, char='a')` helper in the test file** uses `new string(ch, n)`
  which is fine. The production code for building test content is
  indistinguishable from `string.Create`; leave.

## Bugs / hazards

- **The comment at lines 571-576 admits "boundary chars might be
  slightly off"** with the per-page fresh decoder. This is a real risk
  for multi-byte encodings whose sequences straddle page boundaries.
  The argument that "the scan worker used a stateful decoder so the
  boundary is decoder-safe" only holds if the UTF-8 / UTF-16 sequence
  boundaries happen to line up with page byte counts — which is true
  for UTF-16 on even-sized pages, and for UTF-8 only because a stateful
  decoder consumes whole sequences. For a re-read, however, you need
  to re-read from the same `ByteOffset`, and if that offset lands
  mid-sequence in raw file bytes, the fresh decoder will drop the
  leading continuation bytes. Worth an explicit test: take a file with
  a `\u00E9` (2-byte UTF-8) at exactly byte 1 048 576, evict its
  page, read back, assert the char is still `\u00E9`.
- **`ScanWorker` wires `_loadedEvent.Set()` in `finally`** but doesn't
  null-check `_cts` on exit — already disposed if `Dispose` ran
  concurrently. The linked CTS dispose at 440 may throw. Low
  probability; worth a try/catch.
