# `FileSaver`

`src/DMEdit.Core/IO/FileSaver.cs` (199 lines)
Tests: `LargeDocumentTests.FileSaver_*` (4 tests covering round-trip,
large doc timing, unedited fast path alloc budget, edited path alloc
budget).

Streaming saver with temp-file-then-rename, line-ending
normalization, SHA-1 computation, and fast/general paths.

## Likely untested

- **`SaveAsync` cancellation token** — the inner loop checks
  `ct.ThrowIfCancellationRequested()`, but no test cancels
  mid-save. The temp file cleanup in the outer catch handles
  the `OperationCanceledException`, which is not asserted.
- **`backupOnSave = true`** for a file that doesn't already
  exist — the `File.Exists(path)` guard at line 73 skips the
  copy. Untested.
- **`backupOnSave = true`** for a file that does exist — creates
  `path + ".bak"`. Untested.
- **`backupOnSave = true`** when the `.bak` file already exists
  from a previous save — `File.Copy(..., overwrite: true)`
  overwrites. Untested.
- **Temp file cleanup on exception** — `File.Delete(tmpPath)`
  in the catch. If the temp file was never created (exception
  before `WriteToFile`), the delete silently ignores the
  `FileNotFoundException`. Works; untested.
- **`File.Move(tmpPath, path, overwrite: true)` failure** —
  if the target is locked by another process or on a different
  volume that doesn't support atomic rename, the move throws
  and the temp file is cleaned up but the original remains.
  Untested.
- **Encoding-level failures** — if the document contains a
  character that cannot be encoded in the target encoding
  (e.g. a CJK char in Windows-1252), `encoder.GetBytes` throws.
  No test asserts the failure mode or the temp file cleanup.
  Worth at least one test.
- **`NormalizeLineEndings` with a chunk boundary that splits
  a `\r\n`** — `prevCr` carries the state. Tested by
  `FileSaver_LargeDoc_EditedBuffer_*`? Not explicitly.
- **`NormalizeLineEndings` with target = CR** — converts `\n` →
  `\r`, `\r\n` → `\r`. Is this path tested?
- **`NormalizeLineEndings` with a `\r` at the very end of the
  final chunk** — `prevCr` stays true, but there's no next
  iteration. The `\r` was already converted to `nl`, so the
  output is correct. Worth a test asserting "file ending in a
  bare CR normalizes correctly to the target style."
- **`WritePreamble` with `GetPreamble() == null`** — no bytes
  written. Trivial.
- **BOM write for UTF-16 LE / BE** — produces a file that
  starts with `FF FE` / `FE FF`. Tested by the round-trip?
  Round-trip through `FileLoader` should catch it.
- **Fast path (`IsOriginalContent && LengthIsKnown`)** uses the
  `WriteToFile(IBuffer, …)` overload. Tested by
  `FileSaver_LargeDoc_UneditedBuffer_UsesDirectPath`.
- **General path on a string-backed PieceTable** — uses
  `WriteToFile(PieceTable, …)`. Tested by basic round-trip.

## Architectural concerns

- **Two near-identical `WriteToFile` overloads** — one for
  `IBuffer`, one for `PieceTable`. They differ only in the
  content-fetch step (`buf.CopyTo` vs `table.ForEachPiece`).
  A helper delegate `Action<long, int, Span<char>>` would
  unify them. Would save ~30 lines. Would also make it
  easier to add a `Stream`-based overload if the user ever
  wants to save to a non-file destination.
- **`NormalizeLineEndings` is per-chunk** with a `prevCr`
  carry. Subtle state machine that's easy to break. Tested
  indirectly via round-trip. Worth extracting to a named
  type `LineEndingNormalizer` with state + `Write(span)` +
  `Flush()`. Also reusable for `FileLoader` on the receive
  side (though loader currently doesn't normalize).
- **SHA-1 is computed during save**, then returned for the
  caller to store as `BaseSha1`. The assumption is that the
  post-save SHA-1 of the written file matches the SHA-1 of
  the content-as-saved. True, as long as the file isn't
  modified between `File.Move` and whoever might re-read it.
  Worth a comment.
- **Temp file convention is `path + ".tmp"`** — collides if
  another save is in progress. No lock. Worth a UUID suffix
  or at least a `FileShare.None` on the temp open to fail
  fast.
- **`backupOnSave` creates `path + ".bak"` overwriting the
  previous backup.** Only one backup is kept; older versions
  are lost. Worth a numbered scheme (.bak, .bak1, .bak2) or
  at least a comment stating the policy.

## Simplification opportunities

- **Dedupe `WriteToFile` overloads** per above.
- **Extract `NormalizeLineEndings` state.**
- **`NlBufSize` is a one-liner** used twice. Inline.

## Bugs / hazards

1. **Encoding round-trip failure (`encoder.GetBytes` throwing)
   leaves the temp file orphaned AND the exception propagates
   unchanged.** The user sees a raw `EncoderFallbackException`
   without context about which character failed or where.
   Wrap + rethrow with a clearer message.
2. **Two concurrent saves of the same file** both open
   `path + ".tmp"` in `FileMode.Create`. The second one
   truncates and overwrites the first's content. Rename race
   may land anywhere. Low priority (UI serializes saves) but
   a comment at the field would document the assumption.
3. **`backupOnSave` overwrites the existing `.bak`** — if the
   user relies on `.bak` for recovery after discovering a
   mistake two saves later, it's gone.
