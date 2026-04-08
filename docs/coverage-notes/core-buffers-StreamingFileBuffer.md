# `StreamingFileBuffer`

`src/DMEdit.Core/Buffers/StreamingFileBuffer.cs`
Tests: **none direct.** Exercised only transitively via
`ZipFileTests.cs` (which loads zip entries through `FileLoader` which
picks this buffer for zip content).

Per the class remarks, the only production use is "loading zipped
text files" — `FileLoader` uses `PagedFileBuffer` for plain text and
this one for zip entries (non-seekable streams). That's a significant
surface area with no dedicated test coverage.

## Likely untested

### Basics that the zip tests happen to hit
- `Length`, `this[]`, `CopyTo`, `GetLineStart`, `LoadComplete` fires —
  all touched indirectly when a 100-line zip content is read back.

### Everything else
- **Stream-based constructor path** directly (not via zip) — confirms
  `_path == null`, `_externalStream != null`, ownership transfer to
  `_owner`.
- **Dispose while loading** — cancels the scan; the zip tests always
  await `LoadComplete` first.
- **`ScanError` population** when the underlying stream throws — e.g.
  a `Stream` that throws on `Read`. Currently nothing verifies that
  `_done` still gets set and `_loadedEvent.Set()` still fires on that
  path.
- **Non-seekable stream with BOM** — `DetectEncodingAndCreateDecoder`
  has four distinct non-seekable branches (lines 327-340, 348-351) that
  return `prefetched` bytes to be decoded at line 215. None of these
  are directly tested. Zip entry streams are non-seekable, but the
  zip tests all use UTF-8 content without a BOM at the entry start,
  so the non-seekable-with-BOM branches don't execute.
- **Non-seekable stream with a single-byte partial BOM** — if
  `stream.Read(bom, 0, 3)` returns 0, 1, or 2 bytes because EOS comes
  first. Only `bomRead > 0` vs `bomRead == 0` is distinguished (lines
  349-351). A completely empty non-seekable stream (`bomRead == 0`)
  returns UTF-8 no-BOM with `prefetched == null` — works, but
  untested.
- **Trailing bare `\r` at EOF** — line 250, `if (_prevWasCr)` finalization
  after the read loop exits. A file/stream ending in exactly one `\r`
  should produce an extra line start. Untested.
- **Cross-chunk `\r\n`** (line 369, `if (_prevWasCr && charCount > 0)`)
  — the code handles a `\r` at the end of chunk N and a `\n` at the
  start of chunk N+1. Hitting this requires a 1 MB+ stream with a
  `\r\n` exactly at the chunk boundary. Untested.
- **Indent detection counters** (`_spaceIndentCount`, `_tabIndentCount`)
  — `DetectedIndent` returns their `FromCounts`, but no test reads it
  back from a StreamingFileBuffer.
- **SHA-1 correctness** — computed and stored, but no test compares
  against an independently-computed value.
- **`LongestLine` is hardcoded to `10_000`** (line 132). This is a
  suspicious constant. Either it's a TODO or it's a lie. A buffer that
  claims a specific longest-line length without actually measuring is
  going to mislead downstream callers. See below.
- **`_lineStarts` growth path** (lines 423-428) when the pre-allocated
  size is exceeded. For small files the initial `estimatedLines`
  calculation should be plenty, but a stream with many short lines
  would hit this. Untested.
- **`_data` growth path** (lines 287-292) when decoded chars exceed
  the initial allocation. The initial allocation is `maxChars = min(estimatedLen, int.MaxValue)`,
  so for a file-based ctor with correct `byteLen`, the `needed > _data.Length`
  branch only triggers for UTF-16/UTF-32 content encoded into more
  bytes per char than expected, or for a stream ctor with an
  underestimated `estimatedCharLen`. Zip tests may or may not hit it
  depending on content size vs initial estimate.

## Architectural concerns

- **`LongestLine => 10_000`** — a magic constant that is unconditionally
  wrong. It exists because this class doesn't build a line-length
  index — it just tracks line starts. So it can't know the longest
  line. Downstream (`PieceTable.MaxLineLength`, CharWrap mode triggers)
  depends on this value to decide whether to enable char-wrap mode
  for huge files. A streaming buffer lying that every line is
  ≤ 10 000 chars means a zip containing a single 10 MB JSON line will
  *not* trigger char-wrap and will then fall into the `TextLayout`
  slow path that caused the entry 22 crash. **This is a real bug
  lying in wait.** The fix is either: (a) scan line lengths alongside
  line starts (symmetric with `LineScanner`), or (b) return `-1`
  (unknown) and have the PieceTable treat unknown as "assume big" for
  the purpose of char-wrap triggering.
- **Line index is `long[]` for line starts** — duplicates what
  `LineScanner` / `PagedFileBuffer._lineLengths` does with `int`
  lengths. Two parallel schemes. Another reason to unify on
  `LineScanner`.
- **`_data` is a giant `char[]`** — the whole decoded stream is held
  in memory. This is explicit in the class comment ("arbitrary stream…
  decoded to UTF-8… incrementally"), and the use case is "small
  enough that PagedFileBuffer isn't necessary," but the `byte-per-char`
  worst case on a decompressed zip can balloon. The comment at line
  19 says "we only use this to support loading zipped text files" —
  so the caller-side bound is "the uncompressed size of a zip entry."
  A safety cap (e.g. "refuse to decompress > 500 MB") would be wise.
- **No `EnsureLoaded`/`IsLoaded` overrides** — StreamingFileBuffer
  inherits the default `true`/no-op implementations from `IBuffer`.
  Correct, because once loaded the whole thing is in memory. But
  during the background load, `this[offset]` will happily return
  decoded data up to `_loadedLen`, and callers that ask for data
  beyond that offset get an `ArgumentOutOfRangeException`. No test
  exercises the "read ahead of loaded length" race.
- **File-based path is dead code**: the class doc at line 18-20 says
  it's only used for zipped content, but the path-based constructor
  at line 85 still opens a `FileStream` and runs the background scan.
  Either remove it or document what else might still call it.

## Simplification opportunities

- **Merge the indent and line-ending scanning into `LineScanner`** —
  there is no good reason `StreamingFileBuffer` has its own newline
  state machine alongside `PagedFileBuffer`'s use of `LineScanner`.
  Every time anyone fixes a bug in one (bare-`\r` at EOF, cross-chunk
  `\r\n`, etc.), the other drifts.
- **The file-based ctor could delegate to the stream-based ctor**
  (open the FileStream, pass it + owner to the stream ctor). Removes
  the double code path in `LoadWorker`.

## Bugs / hazards (prioritized)

1. **`LongestLine => 10_000` is wrong.** Cf. architectural note above.
   This is the biggest concrete issue in the class.
2. **Non-seekable stream BOM code paths are untested** even though
   production zip loads exercise them for any zip with a BOM'd entry.
3. **Trailing-`\r`-at-EOF finalization** is plausible-looking but
   unverified.
