# Triage — cross-cutting themes and priority list

This document summarizes findings across the 116 per-file
coverage notes generated 2026-04-08. Use it to schedule
remediation work.

## Priority 1 — concrete bugs worth fixing soon

1. **`StreamingFileBuffer.LongestLine => 10_000` is a lie.**
   Hardcoded constant. For a zip containing a multi-MB
   single-line JSON, CharWrap mode will not trigger → the
   `TextLayout` slow path that caused the entry 22 crash is
   reachable. Either scan line lengths (symmetric with
   `LineScanner`) or return −1 and have PieceTable treat
   unknown as "assume big." See
   `core-buffers-StreamingFileBuffer.md`.

2. **`StyleSheet.UpdateFontMetrics` mutates the shared
   default block style** when the BlockType isn't
   registered. `GetBlockStyle(unknownType)` returns the
   singleton default; mutating its `AvgCharWidth` affects
   every other unregistered type. See
   `core-styles-StyleSheet.md`.

3. **`PieceTable._maxLineLen` is a cached duplicate of
   `_lineTree.MaxValue()`** maintained by five different
   update paths. Every new line-tree mutation risks
   drift. Recommend: drop the cache, always call
   `_lineTree.MaxValue()` (O(1)). See
   `core-document-PieceTable.md`.

4. **`LineIndexTree.MaxValue()` has zero direct test
   coverage** despite being load-bearing for
   `PieceTable.MaxLineLength` → CharWrap trigger → entry 22
   crash fix. The per-update bottom-up recompute is
   silent on failure. See `core-collections-LineIndexTree.md`.

5. **`PieceTable.BulkReplace` accepts unsorted or
   overlapping matches without validation** and will
   corrupt the piece list. Add a precondition. See
   `core-document-PieceTable.md`.

6. **`Document.InsertAtCursors` with text containing a
   newline** inserts the newline at each cursor, producing
   undefined multi-line behavior. Either forbid or
   document. See `core-document-Document.md`.

7. **`Document.PasteAtCursors` with
   `lines.Length != colSel.LineCount`** silently no-ops.
   Should throw. See `core-document-Document.md`.

8. **`Document.SelectWord` window-clamp silently truncates
   long words.** 1 024-char window means any word starting
   more than 1 024 chars into a single-line file has its
   selection cut short. See `core-document-Document.md`.

9. **`FileSaver`: encoding round-trip failure** leaves a
   raw `EncoderFallbackException` for the user. Wrap
   with context. See `core-io-FileSaver.md`.

## Priority 2 — high-impact test gaps

- **`LineIndexTree.MaxValue`** — all five uncovered paths
  (insert, remove, update changing the max, unique max
  removal, randomized property).
- **`LineIndexTree.InsertRange`** with ≥ 256 elements (the
  `BuildBalanced` stackalloc vs heap threshold).
- **`PagedFileBuffer`**:
  - LRU promote / evict ordering.
  - Dispose mid-scan.
  - `ScanError` propagation.
  - SHA-1 correctness vs independent hash.
  - BOM edge cases (file < 3 bytes, `EF BB` only, etc.).
  - Multi-byte codepoint straddling 1 MB page boundary.
  - `TakeLineLengths` / `TakeTerminatorRuns` double-call.
- **`StreamingFileBuffer`** — no direct tests at all.
- **`PieceTable.Insert` / `Delete` bare-CR + bare-LF split
  across edits** — CRLF merge semantics in the line tree.
- **`CodepointBoundary` span overloads** — no direct tests.
- **`EditHistory.CanUndo` during an open compound with
  mixed undo-stack state** — sharp corner.
- **`EditHistory.EndCompound` called without matching Begin.**
- **`LineScanner.Finish` called twice** (not idempotent).
- **Test that every `IDocumentEdit` subclass is
  recognized by `EditSerializer`** — reflection smoke test.
- **Test that every `Commands.All` entry has a unique Id.**
- **Test that every `SettingsRegistry.All` entry resolves
  to a real `AppSettings` property.**

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
