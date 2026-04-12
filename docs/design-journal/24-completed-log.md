# 24 — Completed Work Log (2026-03-06 – 2026-04-10)

Entries moved here from the main design journal index to keep it
focused on current state. Reverse chronological order.

---

- **Coverage audit completed** (2026-04-10) — All test coverage gaps
  identified in the 2026-04-08 audit (Priority 1 and 2) are closed.
  Architectural refactors (EditorControl/MainWindow splits, Document
  partial-file split, PieceTable cleanup, LineScanner unification,
  shared pagination helper) are done.  Hidden state smells addressed
  (`LoadResult.BaseSha1` → `internal set`, `TrimAddBuffer` → `internal`).
  Settings bool-row text wrapping fixed (horizontal StackPanel → Grid
  with `Auto,*` columns).  `docs/coverage-notes/` deleted — scratch
  notes have served their purpose.  Test count: 572 → 933.

- **TextLayout slow-path crash hardening** (2026-04-07) — Real user crash
  v0.5.231 scrolling a binary file: Avalonia's `PerformTextWrapping` →
  `ShapedTextRun.Split` threw `InvalidOperationException: Cannot split:
  requested length 3 consumes entire run`.  Two-layer fix in
  `TextLayoutEngine.LayoutLines` slow path: length-preserving
  `SanitizeForTextLayout` (control chars ≠ tab → U+FFFD, lone surrogates
  → U+FFFD) + `MakeTextLayoutSafe` (catch `InvalidOperationException`,
  retry with `NoWrap`, last-resort empty layout).  16 new tests.
  See [22-textlayout-crash-hardening](22-textlayout-crash-hardening.md).

- **ChunkedUtf8Buffer ASCII fast path** (2026-04-07) — Per-chunk
  `IsAllAscii` flag short-circuits `FindByteOffsetInChunk`.  Column-mode
  rapid insert ~28× faster (360ms→13ms per insert on 30 cursors × 200-char
  lines); render and layout collapsed by ~100× as a side benefit.
  See [21-ascii-fast-path](21-ascii-fast-path.md).

- **Hanging indent + Avalonia mono GlyphRun fast path** (2026-04-06) —
  Wrapped continuation rows indent by half an indent level (`HangingIndent`
  setting, default on).  Editor side: new `MonoLayoutContext` /
  `MonoLineLayout` GlyphRun fast path for monospace, no-control-char lines;
  `LayoutLine` becomes a sum type with path-agnostic Render/HitTest
  dispatch.  Tab-bearing lines fall back to `TextLayout` for now.
  See [20-hanging-indent](20-hanging-indent.md).

- **GlyphRun print path + word-break + error plumbing** (2026-04-06) —
  Replaced per-row `FormattedText` printing with direct `DrawGlyphRun`,
  ~55–70% faster (~101 pages/sec on 29K-page doc).  Shared `NextRow`
  helper keeps pagination and rendering in sync.  `ISystemPrintService.Print`
  returns `PrintResult` (success/cancel/error).
  See [19-glyphrun-print](19-glyphrun-print.md).

- **Wrap indicators + UseWrapColumn** (2026-04-06) — `UseWrapColumn` boolean
  setting replaces the `WrapLinesAt=0` pattern.  `ShowWrapSymbol` draws a
  geometric arrow at the wrap column for each wrapped row.
  See [18-wrap-indicators](18-wrap-indicators.md).

- **Cold startup optimization** (2026-04-06) — `PublishReadyToRun=true` on
  all platform release builds gave a noticeable improvement.  Ruled out:
  SingleFile, InterFont removal, Defender exclusion, settings deferral,
  trimming.  See [03-performance](03-performance.md).

- **Editing polish** (2026-04-06) — Auto-indent on Enter, smart Backspace
  deindent, smart Home toggle, trailing-whitespace cleanup on Enter.
  See [17-editing-polish](17-editing-polish.md).

- **Print progress dialog + performance** (2026-04-03) — Modal
  `ProgressDialog` with measure/print phases, ETA, cancel.
  `ComputePageBreaks` rewritten to use `LineIndexTree` arithmetic
  (29K pages: frozen → 331ms).
  See [16-print-progress](16-print-progress.md).

- **CharWrap trigger fix** (2026-04-03) — CharWrap mode requires both
  file size ≥ `CharWrapFileSizeKB` AND longest line ≥ `MaxGetTextLength`.

- **Settings page fixed width** (2026-04-03) — `SettingsContent`
  `MaxWidth=720`, left-aligned.

- **DMInputBox unit tests** (2026-04-03) — 26 tests covering text
  property basics, caret/selection clamping, SelectAll/SelectedText,
  IsReadOnly, defaults, edge cases.

- **Character-wrapping mode + pseudo-line removal** (2026-04-03) —
  Char-wrap mode for huge files (O(1) scroll: row N = char N × charsPerRow).
  Removed the entire pseudo-line system.  `LineIndexTree` simplified to
  single-value treap (~340 lines).
  See [15-char-wrap-mode](15-char-wrap-mode.md).

- **Tab Toolbar + context menu fixes** (2026-04-01) — Replaced tab bar
  "+" button with configurable `TabToolbar`.  Three toolbar zones.
  Fixed advanced-menu visibility reset; context menu font.

- **DMInputBox + caret/scroll polish** (2026-04-01) — Custom
  `DMInputBox : Control` replaces `TextBox` in all DMEdit chrome to work
  around Avalonia caret bug.  `CaretWidth` setting (1.0–2.5).  Caret
  device-pixel snapping.  Row height snapped to pixel multiple.
  See [13-custom-textbox](13-custom-textbox.md).

- **UTF-8 Chunked Add Buffer** (2026-03-29) — Replaced `StringBuilder _addBuf`
  with `ChunkedUtf8Buffer`.  Binary session persistence.  Linux paste
  feeds raw UTF-8 via `AppendUtf8`.
  See [12-utf8-add-buffer](12-utf8-add-buffer.md).

- **Error Handling & UX Hardening** (2026-03-27) — Fatal error dialog
  deadlock fix, `ErrorDialog` DockPanel layout, undo/redo scroll
  preservation, ProgressDialog cancel-on-close, ScrollCaretIntoView
  verification pass for wrapped lines.

- **Bulk PieceTable operations** (2026-03-27) — `PieceTable.BulkReplace`
  with two tiers, O(pieces+matches) single pass, O(1) undo via snapshot.
  See [11-search-and-memory-safety](11-search-and-memory-safety.md).

- **Search, memory safety & horizontal scrolling** (2026-03-27) —
  Horizontal scrollbar when wrapping is off, find bar Wildcard/Regex
  toggles, async match counting, `GetText` 5KB guard, line-at-a-time
  layout, chunked search via `ArrayPool`.
  See [11-search-and-memory-safety](11-search-and-memory-safety.md).

- **Per-line scroll estimation** (2026-03-27) — Replaced global
  `avgLineHeight` with per-line Y estimation via `LineIndexTree`
  prefix sums.

- **Pseudo-newlines & streaming load safety** (2026-03-28) —
  `MaxPseudoLine = 500`; scroll/interaction lock during streaming load.
  `_layoutFailed` flag prevents cascading layout crashes.  19 new tests.

- **Session Persistence & Memory Safety** (2026-03-24) — Edit serialization
  fixes, line tree reliability, `VisitPieces` 1MB chunk cap, removed
  ProceduralBuffer/StringBuffer/StreamingFileBuffer/DevSamples from
  production.
  See [10-session-and-reliability](10-session-and-reliability.md).

- **Global Error Handling** (2026-03-23) — Single general-purpose
  `ErrorDialog`.  Global exception handlers write crash reports to disk.

- **Tail File & Auto-Reload** (2026-03-21) — `TailFile` setting + status
  bar button.  Background load with atomic UI-thread swap.
  `EditorControl.ReplaceDocument(doc, scrollState)`.

- **Theme refinements + Editor Font setting** (2026-03-20) — Consistent
  light/dark theming, font picker in Display settings.

- **Save crash handling** (2026-03-17) — Crash report infrastructure on
  unexpected save failure; Save As / Close Tab options; `BackupOnSave`.

- **Column/Block Selection** (2026-03-16) — Alt+drag rectangular selection
  with multi-cursor edits.  Tab-aware column math; one-step undo.

- **Interactive status bar** (2026-03-14) — Four clickable segments,
  GoTo Line dialog, indent detection.
  See [08-status-bar](08-status-bar.md).

- **Command Palette + 21 commands + key profiles** (2026-03-06–07) —
  F1 modal, text filter, arrow nav.  6 key profiles.  Centralized
  command dispatch, user-customizable bindings.
  See [07-commands](07-commands.md).
