# `StringBuffer` (internal, test-only backing)

`src/DMEdit.Core/Buffers/StringBuffer.cs`
Tests: no dedicated test file — exercised transitively through every
`PieceTable(string)` and `Document(string)` test.

## Likely untested

- **Direct testing is nonexistent.** The transitive coverage is wide
  but shallow: PieceTable tests construct with strings containing no
  weird content. Specific gaps:
  - **`BuildLineIndex` with content ending in a bare `\r` (not followed
    by anything).** The loop handles it — line 46 `} else { starts.Add(i + 1); }`
    — but no test asserts the resulting `LineCount`/`LongestLine` when
    a PieceTable is built from `"abc\r"`.
  - **`BuildLineIndex` with `\r\n` at the very start of the string.**
  - **`LongestLine` math** when the last line has no terminator vs when
    it has one. Lines 58-60 do a special case for the trailing line.
    A two-line string with equal-length lines should report that length
    correctly; a two-line string where the terminator-less tail is longer
    should win.
  - **`GetLineStart(lineIdx)` with a negative index** or with
    `lineIdx >= _lineStarts.Length` — returns `-1`, not tested.
  - **`CopyTo` with a range that's exactly `[0, Length)`** — trivial
    but not asserted.

## Architectural concerns

- **Duplicated line-scanning logic.** `StringBuffer.BuildLineIndex`,
  `LineScanner`, `StreamingFileBuffer.ScanNewlines`, and
  `PagedFileBuffer`'s scanner all implement the same `\n` / `\r` /
  `\r\n` state machine. `StringBuffer` is the only one that DOESN'T
  use `LineScanner`. If it used `LineScanner` (even in a single-shot
  mode) it would automatically inherit line-ending counts and indent
  detection.
- **Marked `internal`** but the XML doc says "used by tests via
  `PieceTable(string)`". It's actually used by production `Document(string)`
  as well (empty-document construction). The class comment should be
  updated, or if it's really test-only, move it into the test project.
- **Long values cast to `int` in `_data[(int)offset]`.** Deliberate —
  the whole point of `StringBuffer` is that the content fits in a CLR
  string (max ~2 GB). Worth a comment acknowledging this.

## Simplification opportunities

- **`BuildLineIndex` could delegate to `LineScanner.Scan` + `Finish`**
  and return `scanner.LineLengths` and its `LongestLine`. Would shrink
  the class by ~20 lines and eliminate a parallel implementation. Risk:
  `LineScanner` has slightly different semantics around the trailing
  non-terminated line; needs verification against the current behavior.
