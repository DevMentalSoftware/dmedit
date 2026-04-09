# Triage — cross-cutting themes and priority list

This document summarizes findings across the 116 per-file
coverage notes generated 2026-04-08. Use it to schedule
remediation work.

## Priority 1 — cleared

All nine Priority-1 items from the initial triage have been
fixed on `HardenAndCoverage`. Cross-references below point
at the commit that closed each item; the per-file notes are
still accurate background reading.

1. ✅ **`StreamingFileBuffer.LongestLine`** now tracks the
   real longest line (per-line during scan + end-of-scan
   finalization for a trailing unterminated line) — closed
   in `faedfc2` (CharWrapMode detection fix). Regression
   tests in `StreamingFileBufferTests` cover multi-MB
   single-line files, multi-line outliers, and trailing
   unterminated lines.
2. ✅ **`StyleSheet.UpdateFontMetrics`** now clones the
   shared default before registering when an unregistered
   `BlockType` is updated — closed in `caff3d0`. Tests in
   `StyleSheetTests`.
3. ✅ **`PieceTable._maxLineLen`** cache removed; every path
   now reads `_lineTree.MaxValue()` directly and
   `AssertLineTreeValid` cross-checks against a ground-truth
   walk in DEBUG — closed in `1ff1a68`.
4. ✅ **`LineIndexTree.MaxValue()`** has direct coverage
   for insert, remove, update-growing-max,
   update-shrinking-max, and randomized property tests —
   closed in `1ff1a68`.
5. ✅ **`PieceTable.BulkReplace`** now validates that match
   positions are sorted ascending and non-overlapping, and
   that uniform/varying replacement counts match — closed
   in `caff3d0`. Tests in `BulkReplaceTests`.
6. ✅ **`Document.InsertAtCursors` with newline** now drops
   column mode after a multi-line broadcast (documented in
   the method's XML-doc comment). Tests in `DocumentTests`.
7. ✅ **`Document.PasteAtCursors` length-mismatch** now
   processes `min(carets, lines)` with the "drop excess"
   rule documented on the method — both under- and
   over-sized inputs behave predictably.
8. ✅ **`Document.SelectWord`** now uses a growing window
   that doubles (capped at int.MaxValue) when the window
   edge is reached inside the line — no silent truncation.
9. ✅ **`FileSaver` encoding fallback** wraps
   `EncoderFallbackException` with file path, encoding, and
   approximate offset — closed in `caff3d0`. Tests in
   `FileSaverTests`.

## Priority 2 — test gaps (status update)

Items marked ✅ have been closed on `HardenAndCoverage`.
Items marked 🟡 remain open.

- ✅ **`LineIndexTree.MaxValue`** — five direct paths added
  (`1ff1a68`).
- 🟡 **`LineIndexTree.InsertRange`** with ≥ 256 elements
  (the `BuildBalanced` stackalloc vs heap threshold).
- **`PagedFileBuffer`**:
  - ✅ LRU promote / evict ordering.
  - ✅ Dispose mid-scan.
  - ✅ `ScanError` propagation.
  - ✅ SHA-1 correctness vs independent hash.
  - ✅ BOM edge cases (file < 3 bytes, `EF BB` only, etc.).
  - ✅ Multi-byte codepoint straddling 1 MB page boundary.
  - ✅ `TakeLineLengths` / `TakeTerminatorRuns` double-call.
- ✅ **`StreamingFileBuffer`** — covered via
  `StreamingFileBufferTests` (LongestLine regression suite
  + trailing unterminated line + multi-line outlier).
- ✅ **`PieceTable.Insert` / `Delete` bare-CR + bare-LF
  split across edits** — CRLF merge semantics pinned in
  `PieceTableTests` (`Insert_LfAfterBareCr_*`,
  `Insert_CrBeforeBareLf_*`, and four more).
- ✅ **`CodepointBoundary` span overloads** — direct tests
  in `DocumentTests` cover `WidthAt`, `WidthBefore`,
  `StepRight`, `StepLeft`, and `SnapToBoundary` for both
  BMP and surrogate-pair paths.
- ✅ **`EditHistory.CanUndo` during an open compound with
  mixed undo-stack state** — direct tests in
  `EditHistorySerializationTests`.
- ✅ **`EditHistory.EndCompound` called without matching
  Begin** — direct test in
  `EditHistorySerializationTests` (silent no-op + nested
  commit invariants).
- ✅ **`LineScanner.Finish` called twice** — current
  non-idempotent behavior pinned in `LineScannerTests`.
- ✅ **Test that every `IDocumentEdit` subclass is
  recognized by `EditSerializer`** — reflection smoke test
  in `EditSerializerTests`.
- ✅ **Test that every `Commands.All` entry has a unique
  Id** — already covered by `CommandRegistryTests`.
- ✅ **Test that every `SettingsRegistry.All` entry
  resolves to a real `AppSettings` property** — plus type
  match, category validity, and `EnabledWhenKey` cross-ref
  — in new `SettingsRegistryTests`.

## Priority 3 — architectural refactors (high blast radius)

### `EditorControl` split (5 007 lines → ~10 partial files)

Single biggest win. Suggested partial split:
```
EditorControl.Properties.cs
EditorControl.Input.cs
EditorControl.Scroll.cs
EditorControl.Layout.cs
EditorControl.Render.cs
EditorControl.Selection.cs
EditorControl.Search.cs
EditorControl.Coalesce.cs
EditorControl.Clipboard.cs
EditorControl.PerfStats.cs
```
Mechanical refactor; no behavior change. Prerequisite to
every other EditorControl improvement.

### `MainWindow` split (3 859 lines → ~10 partials)

Same rationale and mechanism. See `app-MainWindow.md`.

### `Document` god-class (1 278 lines)

Extract editing commands, word classification, line ops,
bulk replace wrappers into partial files or extension
methods. See `core-document-Document.md`.

### `PieceTable` + `_maxLineLen` cleanup

Drop the cache (priority 1 #3) and unify the two "Insert"
paths (`Insert(string)` and `InsertFromBuffer`). See
`core-document-PieceTable.md`.

### Shared `LineScanner` across `StringBuffer`,
`StreamingFileBuffer`, `PieceTable.SpliceInsertLines`,
`FileSaver.NormalizeLineEndings`

Four parallel `\n`/`\r`/`\r\n` state machines. Every bug
fix to one drifts from the others. Unify on `LineScanner`.

### Shared pagination helper

`MonoLineLayout.NextRow` and
`WpfPrintService.PlainTextPaginator.NextRow` are
comment-documented mirrors. Move to a single
`PlainTextPaginator` in Core.

## Priority 4 — hygiene and code quality

### Dedup

- **`TextLayoutEngine.MakeTextLayout` and
  `BlockLayoutEngine.CreateTextLayout`** — near-identical.
- **`LineEndingInfo.Detect(string)` and `Detect(IBuffer)`** —
  duplicated state machine.
- **`CrashReport.Write` and `WriteAsync`** — 80% duplicated
  formatting.
- **`FileSaver.WriteToFile(IBuffer)` and
  `WriteToFile(PieceTable)`** — unify via visitor.
- **`UniformBulkReplaceEdit` and `VaryingBulkReplaceEdit`**
  — shared base class.
- **`HistoryEntry` and `UndoRedoResult`** — same shape.
- **`LoadPagedAsync` and `LoadZip` in FileLoader** — same
  wire-up pattern.
- **Hardcoded GitHub repo URL** in `UpdateService` and
  `GitHubIssueHelper` — consolidate.

### Silent failures that should at least log

- `AppSettings.Load` / `Save` (swallowed).
- `CrashReport.Write` / `WriteAsync` (swallowed).
- `FeedbackClient.SubmitAsync` error paths.
- `StyleSheet`, `FileSaver`, `ClipboardRing.Push` (size
  drop), `NativeClipboardDiscovery` load failures.

### Hidden state smells

- **`LoadResult.BaseSha1` is `{ get; set; }` on a record**
  so the async load can backfill it. Awkward.
- **`DeleteEdit` has four constructors** for slightly
  different use cases. Factory methods.
- **`PieceTable.TrimAddBuffer` is public** but only safe
  from bulk-replace undo. Document the contract or mark
  internal.
- **`Document`'s mutable public properties** (`Selection`,
  `ColumnSel`, etc.) don't fire `Changed` on external
  writes. Subtle. Consider making them private and
  exposing setters.
- **`BlockStructureChangedEventArgs`** could be a
  readonly record struct.

## Priority 5 — long-term / nice to have

- **Block model wiring.** The entire Blocks + Styles
  namespace is dead code from the editor's perspective.
  Either commit to shipping WYSIWYG or delete. Per
  journal, it's "partially implemented but not wired in."
- **Strongly-typed setting descriptors** (`SettingDescriptor<T>`
  instead of boxed `object`).
- **Load default styles from a JSON resource** instead of
  140 lines of `StyleSheet.CreateDefault()`.
- **Command registry as data** (JSON/YAML) rather than
  500 lines of static readonly fields.
- **`SanitizeForTextLayout` sharing** — hoist to a Core
  helper; both TextLayoutEngine and (future) BlockLayoutEngine
  need it.

- **Generalize `ColumnSelection` to a free multi-cursor model.**
  Today `ColumnSelection` is `(AnchorLine, AnchorCol, ActiveLine,
  ActiveCol)` — strictly a rectangle.  After a multi-line
  broadcast paste (or any future "click + ctrl-click to add a
  cursor" feature) the post-edit caret set is *not* a rectangle,
  so the editor has to drop column mode entirely.  A
  `MultiCursorSelection` holding `IReadOnlyList<Selection>` would
  let the editor preserve carets across these transitions and
  unblock free multi-cursor editing.  Touch points: every
  `Document.*AtCursors` method, `EditorControl` caret rendering,
  arrow-key movement (per-cursor), session persistence
  (serialization of arbitrary cursor sets), and the column-mode
  entry/exit logic.  Motivations stack: post-broadcast caret
  state, post-distribute caret state, click-to-add-cursor,
  matched-paste end state.  Estimated multi-day feature.  See the
  2026-04-08 conversation about column-mode multi-line paste.

## Cross-cutting recurring themes

### Encoder/decoder duplication
Four copies of the UTF-8 byte-length switch in
`ChunkedUtf8Buffer`. One helper.

### Parallel arrays vs struct-of-arrays
`LineIndexTree`, `PagedFileBuffer._pages`/`_pageData`. SoA is
fast but hazardous. At minimum, comment the invariants.

### Silent "swallow and return default" patterns
AppSettings, CrashReport, FileLoader, FileSaver,
LinuxClipboardService, LinuxFileDialog, RecentFilesStore,
SessionStore, StyleSheet UpdateFontMetrics. Each instance
is defensible; collectively they make every error path
invisible. A common `ErrorReporter.Log(ex, context)`
would at least leave breadcrumbs.

### Per-frame allocation flagged by VS Memory Insights
(From journal):
- `MonoLineLayout.Draw` (fixed per entry 21).
- `ToolbarControl.Render` — 1 312 `TextLineImpl`s per 50s.
- `TabBarControl.Render` — 60 `TextLineImpl`s per 50s.
- Caret blink ≫ render (fixed per CaretLayer split).

### Event handler closure leaks
(From journal):
- `MainWindow+<>c__DisplayClass157_1` (472B worst offender).
- Two `DispatcherTimer+<>c__DisplayClass18_0`.

### Thread safety
`PagedFileBuffer` has three synchronization mechanisms
(`_lock`, `_cts`, `_loadedEvent` plus interlocked +
volatile). Not documented at the field level. An "sync
invariants" block at the top of the class would help.

## What was consistently well-tested

- `FenwickTree`.
- `LineIndexTree` structural ops (save for `MaxValue`).
- `ChunkedUtf8Buffer` including the surrogate-pair
  hang regression suite.
- `PieceTable` Insert/Delete/GetText/line-index basics.
- `Document` editing commands, case transform, expand
  selection (rune-aware).
- `EditHistory` serialization round-trip.
- `BulkReplace` uniform + varying + convert-indentation.
- `KeyBindingService` single-key, chord, override, conflict.
- `Block` core API including pristine/dirty lifecycle.
- `LineScanner` chunked-input + terminator runs + indent.
- `PagedFileBuffer` multi-page, concurrent access, BOM basics.
- `DMInputBox` property/clamp basics.
- `TextLayoutEngine` entry-22 crash hardening.

## How to use this document

1. **Triage bugs**: start at Priority 1. Each entry links
   back to its per-file note for context.
2. **Fill test gaps**: Priority 2 is a direct TODO list.
3. **Schedule refactors**: Priority 3 is the big-ticket
   work. Do `EditorControl` and `MainWindow` splits first;
   they unblock everything else.
4. **Delete this directory** after processing — these are
   scratch notes, not design docs.
