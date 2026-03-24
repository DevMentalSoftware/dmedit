# 10 — Session Persistence, Edit Serialization & Memory Safety

Date: 2026-03-24

This entry covers a major reliability pass triggered by a crash-on-Linux investigation
that uncovered multiple latent bugs in edit serialization, session restore, line tree
management, and memory safety.

---

## Root cause: Linux crash

The original symptom — silent crash on Save-to-PDF on Linux — turned out to be caused
by a full disk (`/dev/sda1` at 100%) from a failed Ubuntu upgrade.  Every
`File.WriteAllText` call truncated the target file to zero bytes and then failed to
write content, producing zero-byte settings, recent files, crash reports, and session
files.  The silent-crash aspect led to the ErrorDialog and global handler improvements.

## Edit serialization bugs

### MaterializeText not called (critical)

`EditSerializer` read `del.DeletedText` which returns the `_text` field — but for
piece-based deletes (the common case from `DeleteRange` → `CapturePieces`), `_text`
is null.  The serializer wrote `""` for the deleted text.  On restore, the delete
appeared to work (Apply uses offset+length), but the document was silently wrong
because the Revert path had no content to restore.

**Fix:** `EditSerializer` now calls `del.MaterializeText(table)` which reads the
actual text from piece table buffers.  A 16 MB budget caps materialization to prevent
multi-GB string allocations for huge deletes; oversized deletes serialize without text
but still Apply correctly.

### DeleteEdit deserialization ignored explicit length (critical)

The deserializer used `new DeleteEdit(ofs, text)` which derives `Len` from
`text.Length`.  When text was `""` (budget-exceeded or the MaterializeText bug above),
`Len` became 0 — a complete no-op.  The base file loaded unchanged, and insert edits
landed at their original offsets in the un-modified content, producing visible
corruption ("millions of deleted lines came back").

**Fix:** Serializer always writes explicit `len` field; deserializer uses a new
`DeleteEdit(ofs, len, text)` constructor that preserves the original length.

### Recapture moved into Apply (correctness)

The original design recaptured piece descriptors for all children of a CompoundEdit
*before* any were applied.  For compounds where later deletes depend on earlier edits
having shifted offsets, this captured the wrong ranges.  Moving recapture into
`DeleteEdit.Apply` itself means each delete captures at the exact moment the table is
in the correct pre-delete state.

## Line tree reliability

### Line tree not built before edit replay (critical)

`CaptureLineInfo` returned null when `_lineTree` was null (lazy creation).  During
session restore, the tree was never built because nothing accessed `LineCount` before
`RestoreEntries`.  All deletes recorded null line info, so undo fell into the
`ReinsertedNonNewlineChars` path which treats a multi-million-line delete as a
single-line length change — corrupting the tree.

**Fix:** `PagedFileBuffer` now calls `table.InstallLineTree()` at the end of loading.
`CaptureLineInfo` boundary condition also fixed (`<=` → `<`) for trailing-newline
deletes.

### Piece table / line tree atomicity

`DeleteEdit.Revert` previously called `InsertPieces` then `RestoreLines` as separate
operations.  A layout pass could observe the intermediate state (content restored but
line tree still reflecting the deleted range), producing `len = 948M` in
`LayoutWindowed`.

**Fix:** Combined into `PieceTable.InsertPiecesAndRestoreLines` — a single method
that updates both atomically.

## Memory safety

### VisitPieces chunk cap

`VisitPieces` allocated a `char[]` the size of each piece.  For a WholeBufSentinel
piece covering a multi-GB file, this produced a single multi-GB allocation.  Now caps
at 1 MB per callback, looping within each piece.  Affects `BuildLineTree`,
`ConvertIndentation`, and all other callers.

### ReadPieces chunk cap + cache fix

Original-buffer reads in `ReadPieces` (used by `MaterializeText`) now chunked at 1 MB.
Also fixed `_addBuf.ToString()` call to use `BufFor()` which caches the result.

### GetText() marked internal

`PieceTable.GetText()` materializes the entire document as a string.  Only used by
tests.  Marked `internal` to prevent accidental use in production code.

## Buffer simplification

Removed `ProceduralBuffer`, `StringBuffer`, and `StreamingFileBuffer` from production
code.  `ProceduralBuffer` and `StringBuffer` moved to test projects.
`StreamingFileBuffer` deleted (was only used for ZIP text loading, now handled by
`PagedFileBuffer` with a `Stream` overload).  `DevSamples` removed entirely — no
longer needed with real sample files available.  Production code now only uses
`PagedFileBuffer` and the empty-document `EmptyBuffer` singleton.

## Error handling

### General-purpose ErrorDialog

Replaced `SaveErrorDialog` and `SaveFailedDialog` with a single `ErrorDialog`:
resizable, themed, configurable buttons.  Dev mode adds a collapsed expander showing
the full stack trace.

### Global crash handlers

`Program.cs` installs `AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException`, and
`Dispatcher.UIThread.UnhandledException`.  All write a crash report to the session
directory and show the ErrorDialog.  The app never silently vanishes.

### SaveAsPdf try/catch

`SaveAsPdfAsync` now catches exceptions and shows the ErrorDialog instead of crashing
the process.
