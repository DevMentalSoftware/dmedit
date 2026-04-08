# `LineScanner`

`src/DMEdit.Core/Documents/LineScanner.cs` (198 lines)
Tests: `LineScannerTests.cs` (~30 tests). Good coverage.

Incremental line/terminator/longest-line/indent scanner. Used by
`PieceTable.BuildLineTree`, `PagedFileBuffer.ScanWorker`, and should
be used by `StringBuffer` and `StreamingFileBuffer` (see those notes).

## Likely untested

- **`LineScanner.BuildTree`** — builds a LineIndexTree from the
  accumulated lengths. Used by PieceTable but no direct test.
- **`DetectedLineEnding`/`DetectedIndent` properties** — delegate to
  `LineEndingInfo.FromCounts`/`IndentInfo.FromCounts`. Each is
  independently tested, and the scanner-level counters have
  dedicated tests, but no test asserts the composition
  (`scanner.DetectedLineEnding.Label == "CRLF"` after a CRLF scan).
- **`Finish` called twice.** The method is not idempotent — it
  appends another "None" terminator run and another zero-length
  final line. Classic reuse bug. Worth a guard or at least a
  `Debug.Assert`.
- **`Scan` called after `Finish`.** Same concern.
- **`RecordTerminator` coalescing when the same type repeats** —
  `if (_currentRunType != type)` guards against adding a duplicate
  run. Tested indirectly; worth a direct test for a run of 1000
  pure-LF lines producing exactly 1 terminator-run entry.
- **`UpgradeLastTerminatorToCRLF` when the last run is NOT a
  CR** — falls through to `RecordTerminator(CRLF)`. The `else`
  branch is reachable only if the scanner state was somehow
  corrupted (a CR emitted more than 1 line ago). Source handles
  it defensively; no test.
- **Consecutive bare-CRs chunked across `Scan` calls** —
  `_prevWasCr` state on a single `Scan` call is flushed at the
  top of the next call's loop. Tested for one chunk boundary;
  rapid-fire bare CRs across many chunks would stress the
  state machine.

## Architectural concerns

- **`_runningLineLen` and `_currentLineLen` are distinct fields.**
  The comment at line 30-32 explains: `_runningLineLen` spans
  CR→LF as one line, `_currentLineLen` doesn't. This is subtle
  and the only thing ensuring longest-line accuracy. Worth a
  bigger comment or a refactor to a single counter with a
  "CRLF pending" flag.
- **`_currentRunType = (LineTerminatorType)255` as a sentinel.**
  Works, but `LineTerminatorType?` would be clearer.
- **`TerminatorRuns` is a list of tuples.** A `readonly record
  struct TerminatorRun(long StartLine, LineTerminatorType Type)`
  would be self-documenting.

## Simplification opportunities

- **The CRLF upgrade path** (lines 77-88) mutates the last
  line-length entry. This works because LF is only ever one char
  past a CR, but a future reader might miss the "+1" increment
  followed by the "+1 for LF" in `_runningLineLen`. Extract a
  helper `UpgradeCrToCrlf(currentLen)`.
- **Indent tracking** (`_atLineStart`, `_spaceIndentCount`,
  `_tabIndentCount`) is orthogonal to line scanning. Could be
  peeled out into an optional `IIndentObserver` if anyone ever
  wanted a scanner without indent tracking. Not urgent.
