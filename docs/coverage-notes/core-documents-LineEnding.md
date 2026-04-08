# `LineEnding` enum + `LineEndingInfo`

`src/DMEdit.Core/Documents/LineEnding.cs` (119 lines)
Tests: `LineEndingInfoTests.cs`.

## Likely untested

- **`Detect(string)` and `Detect(IBuffer, sampleLen)` are both
  implemented with the same state machine**, duplicated. One of
  them is probably tested; the other may not be.
- **`Detect(IBuffer)` with a sample that cuts off a `\r\n`**
  — `i + 1 < len` guard handles it (the CRLF becomes a bare CR
  in the sample and is counted as CR). This is a subtle
  sampling artifact; worth a comment and possibly a test that
  asserts the approximation is acceptable.
- **`PlatformDefault`** — returns CRLF on Windows, LF otherwise.
  Tested on whichever platform CI runs on; the other path is
  untested.
- **`FromCounts` tie-breaking** — `lf == crlf`, `crlf == cr`, etc.
  Source picks in a specific order (LF → CRLF → CR). The exact
  tie-breaker isn't asserted anywhere visible.

## Architectural concerns

- **Two `Detect` overloads are copy-paste duplicates** with
  `string` vs `IBuffer` differences. Combine into a
  `Detect(Func<long, char>, long length, int sampleLen)` or a
  generic `Detect<T>` where T supports indexing. Or bite the
  bullet and have `string` → `Detect` call the `IBuffer` overload
  with a `StringBuffer` wrapper.
- **Sampling at 64 KB** is a fixed constant. For a file where the
  first 64 KB is a license header in one style and the rest is
  code in another style, the detection reports the header's
  style. Not necessarily wrong, but worth documenting the
  sampling bias.
- **`Label` and `NewlineString`** both switch on the same enum.
  Could live on the enum itself via extension methods.

## Simplification opportunities

- **Dedupe the two `Detect` methods.**
- **`PlatformDefault`** uses `Environment.NewLine == "\r\n"` which
  is a runtime call. Cache in a static field.
