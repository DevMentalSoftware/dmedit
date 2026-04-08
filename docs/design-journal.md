# DMEdit Design Journal

Chronological record of design decisions, user requirements, and architectural direction.
**This is the index.** Full entries live in `docs/design-journal/`. When adding a new
entry, write the full content in the relevant detail file and add a one-line summary here
under "Recently completed". Update the "In progress" section after every change, even a
small one — it is the primary way a fresh session recovers context.

---

## Table of Contents

| File | Dates | Topic |
|------|-------|-------|
| [01-foundations](design-journal/01-foundations.md) | 2026-02-26 | Core editor, IBuffer abstraction, windowed layout, dual-zone scrollbar design |
| [02-document-model](design-journal/02-document-model.md) | 2026-02-26 | Variable heights, WYSIWYG block tree, persistence architecture |
| [03-performance](design-journal/03-performance.md) | 2026-02-28, 2026-04-06 | Perf stats, streaming I/O, paged buffer, ZIP support, cold startup optimization |
| [04-ux](design-journal/04-ux.md) | 2026-02-28 | Undo selection, caret/scroll UX, selection rounded corners |
| [05-features](design-journal/05-features.md) | 2026-03-02 – 2026-03-04 | Feature backlog, editing commands, status bar, line numbers, tab bar |
| [06-settings](design-journal/06-settings.md) | 2026-03-05 | Edit coalescing undo, settings document tab |
| [07-commands](design-journal/07-commands.md) | 2026-03-06 – 2026-03-07 | Command registry, key binding profiles, 21 new commands, command palette |
| [08-status-bar](design-journal/08-status-bar.md) | 2026-03-14 | Interactive status bar buttons, indent detection, GoTo Line, file locking fix, file watching notes |
| [09-storage-and-history](design-journal/09-storage-and-history.md) | 2026-03-22 | Storage-backed edits, file history, checkpoints, projects, git integration roadmap |
| [10-session-and-reliability](design-journal/10-session-and-reliability.md) | 2026-03-24 | Session persist bugs, edit serialization, line tree reliability, memory safety, buffer simplification |
| [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md) | 2026-03-27 | Horizontal scrollbar, find bar improvements, async match counting, GetText guard, line-at-a-time layout, chunked search, ReplaceAll design |
| [12-utf8-add-buffer](design-journal/12-utf8-add-buffer.md) | 2026-03-29 | ChunkedUtf8Buffer replaces StringBuilder _addBuf, binary session persistence, paged eviction roadmap |
| [13-custom-textbox](design-journal/13-custom-textbox.md) | 2026-04-01 | DMInputBox: lightweight custom TextBox replacing Avalonia TextPresenter to fix caret bug |
| [14-forced-wrap](design-journal/14-forced-wrap.md) | 2026-04-02 | Attempted forced wrapping for long lines — moved to AlternateLineBranch |
| [15-char-wrap-mode](design-journal/15-char-wrap-mode.md) | 2026-04-03 | Character-wrapping mode: O(1) scroll math, no pseudo-lines needed |
| [16-print-progress](design-journal/16-print-progress.md) | 2026-04-03 | Print progress dialog, monospace pagination, cancellation, ETA display |
| [17-editing-polish](design-journal/17-editing-polish.md) | 2026-04-06 | Auto-indent on Enter, smart deindent Backspace, smart Home, trailing whitespace cleanup |
| [18-wrap-indicators](design-journal/18-wrap-indicators.md) | 2026-04-06 | Wrap symbol glyph at wrap column; hanging indent analysis (deferred) |
| [19-glyphrun-print](design-journal/19-glyphrun-print.md) | 2026-04-06 | GlyphRun fast path for WPF printing (~55–70% faster), word-break wrap restored, print error plumbing overhaul, hanging indent unblocked |
| [20-hanging-indent](design-journal/20-hanging-indent.md) | 2026-04-06 | Hanging indent on wrapped rows, Avalonia monospace GlyphRun fast path, first step toward removing TextLayout from the editor |
| [21-ascii-fast-path](design-journal/21-ascii-fast-path.md) | 2026-04-07 | ChunkedUtf8Buffer per-chunk IsAllAscii flag — column-mode insert ~28× faster, all CharAt-touching code paths benefit |
| [22-textlayout-crash-hardening](design-journal/22-textlayout-crash-hardening.md) | 2026-04-07 | Slow-path TextLayout sanitize + try/catch fallback — fixes real user crash scrolling a binary file (Avalonia split bug) |

---

## Current State

**Test baseline: 657** (521 Core + 47 Rendering + 89 App, 1 skipped)

### In progress

- **Surrogate-pair safety** — Backspace/Delete used to split a UTF-16
  surrogate pair and leave a stranded half in the buffer, which then
  corrupted any path that round-trips through UTF-8/JSON (e.g. clipboard
  out to other apps).  `Document.DeleteBackward`/`DeleteForward` now
  expand to swallow both halves of a pair (same pattern as `\r\n`),
  `PushInsert` runs `SanitizeSurrogates` so any lone half coming from a
  paste/IME becomes U+FFFD, and `ChunkedUtf8Buffer.DecodeUpToNChars` has
  forward-progress safety nets that emit U+FFFD if it ever sees a
  malformed sequence on the read side.  Tests added under `DocumentTests`.

- **EditorControl caret X offset** — caret renders consistently further right
  than DMInputBox when using the same font/size.  Both use
  `HitTestTextPosition().X` identically.  Root cause unknown.  The new
  mono GlyphRun path (entry 20) may already address this for the editor;
  DMInputBox would need its own migration.
  See [13-custom-textbox](design-journal/13-custom-textbox.md).

- **DMInputBox custom TextBox** — lightweight single-line text control
  replacing Avalonia's TextBox in DMTextBox/DMEditableCombo.  Custom
  `Render` for text/selection/caret.  Fixes Avalonia #12809.
  See [13-custom-textbox](design-journal/13-custom-textbox.md).

- **Search Within Selection** — when OpenFindBar runs with a multi-line
  selection, scope dropdown should auto-pick "Current Selection" and all
  Find/Replace bound to that range.  No materialization, just integer
  range limiting; preserve range across ReplaceAll edits.

- **Paged add-buffer eviction** (roadmap) — older immutable chunks could
  page to disk like PagedFileBuffer.  No code yet.
  See [12-utf8-add-buffer](design-journal/12-utf8-add-buffer.md).

### Recently completed

- **TextLayout slow-path crash hardening** (2026-04-07) — Real user crash
  v0.5.231 scrolling a binary file: Avalonia's `PerformTextWrapping` →
  `ShapedTextRun.Split` threw `InvalidOperationException: Cannot split:
  requested length 3 consumes entire run`.  Two-layer fix in
  `TextLayoutEngine.LayoutLines` slow path: length-preserving
  `SanitizeForTextLayout` (control chars ≠ tab → U+FFFD, lone surrogates
  → U+FFFD) + `MakeTextLayoutSafe` (catch `InvalidOperationException`,
  retry with `NoWrap`, last-resort empty layout).  Sanitization is
  hygiene; the catch is the load-bearing defense — surrogates and
  combining marks can interact across code units in ways no per-char
  scrub can predict.  16 new tests in DMEdit.Rendering.Tests.
  See [22-textlayout-crash-hardening](design-journal/22-textlayout-crash-hardening.md).

- **ChunkedUtf8Buffer ASCII fast path** (2026-04-07) — Per-chunk
  `IsAllAscii` flag short-circuits `FindByteOffsetInChunk`.  Column-mode
  rapid insert ~28× faster (360ms→13ms per insert on 30 cursors × 200-char
  lines); render and layout collapsed by ~100× as a side benefit.
  See [21-ascii-fast-path](design-journal/21-ascii-fast-path.md).

- **Hanging indent + Avalonia mono GlyphRun fast path** (2026-04-06) —
  Wrapped continuation rows indent by half an indent level (`HangingIndent`
  setting, default on).  Editor side: new `MonoLayoutContext` /
  `MonoLineLayout` GlyphRun fast path for monospace, no-control-char lines;
  `LayoutLine` becomes a sum type with path-agnostic Render/HitTest
  dispatch.  Tab-bearing lines fall back to `TextLayout` for now.
  See [20-hanging-indent](design-journal/20-hanging-indent.md).

- **GlyphRun print path + word-break + error plumbing** (2026-04-06) —
  Replaced per-row `FormattedText` printing with direct `DrawGlyphRun`,
  ~55–70% faster (~101 pages/sec on 29K-page doc).  Shared `NextRow`
  helper keeps pagination and rendering in sync.  `ISystemPrintService.Print`
  returns `PrintResult` (success/cancel/error), `RuntimeWrappedException`
  unwrap surfaces WPF managed-C++ throws, friendly error messages with raw
  detail in DevMode expander only.  `AppSettings.DevModeAllowed` centralizes
  the DevMode gate.
  See [19-glyphrun-print](design-journal/19-glyphrun-print.md).

- **Wrap indicators + UseWrapColumn** (2026-04-06) — `UseWrapColumn` boolean
  setting replaces the `WrapLinesAt=0` pattern.  `ShowWrapSymbol` draws a
  geometric arrow at the wrap column for each wrapped row.  Hanging indent
  analyzed and deferred to entry 20.
  See [18-wrap-indicators](design-journal/18-wrap-indicators.md).

- **Cold startup optimization** (2026-04-06) — `PublishReadyToRun=true` on
  all platform release builds gave a noticeable improvement.  Ruled out:
  SingleFile, InterFont removal, Defender exclusion, settings deferral,
  trimming.  ~1s of taskbar→exe overhead is Windows shell activation.
  See [03-performance](design-journal/03-performance.md).

- **Editing polish** (2026-04-06) — Auto-indent on Enter, smart Backspace
  deindent, smart Home toggle, trailing-whitespace cleanup on Enter.
  See [17-editing-polish](design-journal/17-editing-polish.md).

- **Print progress dialog + performance** (2026-04-03) — Modal
  `ProgressDialog` with measure/print phases, ETA, cancel.
  `ComputePageBreaks` rewritten to use `LineIndexTree` arithmetic
  (29K pages: frozen → 331ms).  Removed `WrapLine`/`FindBreakPosition`/
  `CountWrappedLines`.  Concurrent-print guard via `_printTask`.
  See [16-print-progress](design-journal/16-print-progress.md).

- **CharWrap trigger fix** (2026-04-03) — CharWrap mode requires both
  file size ≥ `CharWrapFileSizeKB` AND longest line ≥ `MaxGetTextLength`.
  Removed early-load sites that pre-set CharWrap on size alone.

- **Settings page fixed width** (2026-04-03) — `SettingsContent`
  `MaxWidth=720`, left-aligned, so descriptions wrap and click targets
  don't extend to window edge.

- **DMInputBox unit tests** (2026-04-03) — 26 tests covering text
  property basics, caret/selection clamping, SelectAll/SelectedText,
  IsReadOnly, defaults, edge cases.  Avalonia.Headless.XUnit + TestApp
  added to App.Tests.

- **Character-wrapping mode + pseudo-line removal** (2026-04-03) —
  Char-wrap mode for huge files (O(1) scroll: row N = char N × charsPerRow).
  Removed the entire pseudo-line system (`SplitLongLine`, dual offsets,
  `_val2`/`_sum2` in LineIndexTree, `LineTerminatorType.Pseudo`).
  `LineIndexTree` simplified to single-value treap (~340 lines).
  See [15-char-wrap-mode](design-journal/15-char-wrap-mode.md).

- **Tab Toolbar + context menu fixes** (2026-04-01) — Replaced tab bar
  "+" button with configurable `TabToolbar`.  Three toolbar zones
  (TabToolbar/Center/Right).  `FileRecent` dropdown command
  (`IsToolbarDropdown`/`IsDropdown`).  Fixed advanced-menu visibility
  reset; context menu font; stale context menu auto-open.

- **DMInputBox + caret/scroll polish** (2026-04-01) — Custom
  `DMInputBox : Control` replaces `TextBox` in all DMEdit chrome to work
  around Avalonia caret bug.  `CaretWidth` setting (1.0–2.5).  Caret
  device-pixel snapping.  Row height snapped to pixel multiple to
  eliminate inter-line jitter.
  See [13-custom-textbox](design-journal/13-custom-textbox.md).

- **UTF-8 Chunked Add Buffer** (2026-03-29) — Replaced `StringBuilder _addBuf`
  with `ChunkedUtf8Buffer`.  Eliminated `InsertEdit`; all inserts go through
  `SpanInsertEdit`/`PushInsert`.  Session persistence uses binary
  `{id}.addBuf` companion file; edit JSON references buffer char offsets.
  Linux paste feeds raw UTF-8 via `AppendUtf8`.
  See [12-utf8-add-buffer](design-journal/12-utf8-add-buffer.md).

- **Error Handling & UX Hardening** (2026-03-27) — Major overhaul: fatal
  error dialog deadlock fix (background-thread exceptions use `Show()` +
  manual modality), `ErrorDialog` DockPanel layout, DevMode test commands
  (`Dev.ThrowOnUIThread`/`Dev.ThrowOnBackground`), undo/redo scroll
  preservation, ProgressDialog cancel-on-close, settings-page command
  whitelist, command ordering by definition order, ScrollCaretIntoView
  verification pass for wrapped lines.

- **Bulk PieceTable operations** (2026-03-27) — `PieceTable.BulkReplace`
  with two tiers (`UniformBulkReplaceEdit` / `VaryingBulkReplaceEdit`),
  O(pieces+matches) single pass, O(1) undo via snapshot restore.
  `EditorControl.ReplaceAllAsync` runs match collection in background.
  `ConvertIndentation` rewritten to use `BulkReplaceVarying`.
  See [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md).

- **Search, memory safety & horizontal scrolling** (2026-03-27) —
  Horizontal scrollbar when wrapping is off, find bar Wildcard/Regex
  toggles, async match counting, `GetText` 5KB guard, line-at-a-time
  layout, chunked search via `ArrayPool` (no string alloc), search term
  capped to single-line ≤1024 chars.
  See [11-search-and-memory-safety](design-journal/11-search-and-memory-safety.md).

- **Per-line scroll estimation** (2026-03-27) — Replaced global
  `avgLineHeight` with per-line Y estimation via `LineIndexTree`
  prefix sums.  `EstimateLineY` / `EstimateTopLine` helpers; `GetCharsPerRow`
  deduplicates the chars-per-row math.

- **Pseudo-newlines & streaming load safety** (2026-03-28) —
  `MaxPseudoLine = 500`; lines exceeding this get split in the line tree
  only (text never modified).  Removed O(N) buffer short-circuits from
  `LineCount`/`LineStartOfs`/`LineFromOfs`.  Scroll/interaction lock
  during streaming load.  `_layoutFailed` flag prevents cascading layout
  crashes.  19 new tests.

- **Session Persistence & Memory Safety** (2026-03-24) — Edit serialization
  fixes, line tree reliability, `VisitPieces` 1MB chunk cap, removed
  ProceduralBuffer/StringBuffer/StreamingFileBuffer/DevSamples from production.
  See [10-session-and-reliability](design-journal/10-session-and-reliability.md).

- **Global Error Handling** (2026-03-23) — Single general-purpose
  `ErrorDialog` (resizable, themed, configurable buttons).  Global
  exception handlers in `Program.cs` write crash reports to disk and
  show the dialog.

- **Tail File & Auto-Reload** (2026-03-21) — `TailFile` setting + status
  bar button.  Background load awaits completion before atomic UI-thread
  swap (no flicker).  `EditorControl.ReplaceDocument(doc, scrollState)`
  preserves scroll on swap.  `DualZoneScrollBar` reads from
  `IScrollSource` (single source of truth).

- **Theme refinements + Editor Font setting** (2026-03-20) — Consistent
  light/dark theming, font picker in Display settings (DMEditableCombo +
  fixed-width filter + size NUD + preview).  Auto-detected default
  monospace font.

- **Save crash handling** (2026-03-17) — Crash report infrastructure on
  unexpected save failure; Save As / Close Tab options; `BackupOnSave`
  setting.

- **Column/Block Selection** (2026-03-16) — Alt+drag rectangular selection
  with multi-cursor edits.  Tab-aware column math; one-step undo.
  Disabled when wrapping is on.

- **Interactive status bar** (2026-03-14) — Four clickable segments
  (Ln/Ch, Encoding, Line Ending, Indent), GoTo Line dialog, indent
  detection.  File locking fix removed persistent `_fs`.
  See [08-status-bar](design-journal/08-status-bar.md).

- **Command Palette + 21 commands + key profiles** (2026-03-06–07) —
  F1 modal, text filter, arrow nav.  21 new commands (Find stubs,
  Delete Word L/R, Insert Line Above/Below, Duplicate, Indent, Scroll
  Line U/D, Zoom, Revert).  6 key profiles (Default, VS Code, Visual
  Studio, JetBrains, Eclipse, Emacs).  Centralized command dispatch,
  user-customizable bindings.
  See [07-commands](design-journal/07-commands.md).

### Key deferred items

- **"Non-responsive after crash" report (byron, 2026-04-07)** — User hit
  the binary-file Avalonia split crash (entry 22), tried to relaunch
  dmedit.exe, and the relaunched process was non-responsive.  No
  additional crash report files were written.  Running the Velopack-emitted
  Portable.zip from `Downloads\DMEdit-win-Portable\current\`, no Update.exe
  in play, no actual update available (no new beta since adding Velopack).
  We do **not** know which path is hanging — at least six possibilities
  weren't ruled out: hung zombie original + `SingleInstanceService`
  hand-off swallowing the new launch, `vpk.Run()` wedging on the Portable
  layout, Avalonia init hang, session-restore hang on a file the previous
  process left half-written, settings/lock-file hang, or "not a hang at
  all, just an invisible early-exit."  Diagnostic question to ask next
  time it happens: in Task Manager, was the original (crashed) dmedit.exe
  still present when the new one was launched?  That single answer
  collapses the search space.  Defensive work that was scoped but not
  shipped:
  - **Startup breadcrumb log** in `%AppData%/DMEdit/startup.log` —
    PID/parent PID/args at `Main` entry, after `vpk.Run()`, after
    `SingleInstanceService` ctor (with `IsOwner`), before/after Avalonia
    `StartWithClassicDesktopLifetime`, after `MainWindow` shown, on exit.
    ~30 lines, no behavior change, would diagnose any future report in one read.
  - **Hard watchdog in `Program.HandleFatalException`** — fire-and-forget
    `Task.Delay(10s)` then `Environment.FailFast`.  No matter what the
    dialog flow does, the process must die within 10s of a fatal exception.
    Independently correct regardless of whether it's the cause here.
    Currently nothing forces `Process.Kill()` if the dialog flow itself
    hangs (e.g., `done.Wait()` on the background-thread path waiting on
    a `ManualResetEventSlim` that nobody sets).
  - **`SingleInstanceService` mutex pattern** — `new Mutex(initiallyOwned:
    true, name, out createdNew)` is documented as racy; MS recommends
    `new Mutex(false, name)` + `WaitOne(0)` with `AbandonedMutexException`
    catch.  Correctness fix; not provably load-bearing for this report
    but worth doing on its own merit.
  - **Pipe-ping liveness check** — secondary instance pings the named
    pipe with ~500ms timeout before deferring to the "owner."  Handles
    the hung-but-not-dead-owner case the mutex change can't.
  - **Visible feedback when a launch is consumed by the existing instance** —
    flash taskbar / focus existing window on `FileRequested` (and on an
    empty ping if we add one).  UX paper cut that compounds with the worse bug.

- **Settings page width constraint regression (2026-04-08)** — long
  setting descriptions in `SettingsControl.axaml`'s `SettingsContent`
  StackPanel no longer wrap at the intended ~700px width; they extend
  to the full available column width.  XAML still has `MaxWidth="700"`
  and `HorizontalAlignment="Left"` (added in commit `237f2fe`,
  2026-04-03), and the constraint used to work.  Surprising data point:
  replacing `MaxWidth` with a rigid `Width="700"` *also* had no effect,
  which rules out the standard "TextWrapping=Wrap measure-pass with
  unconstrained available width" quirk and points at something further
  up the layout chain (the `ContentScroll` ScrollViewer with
  `HorizontalScrollBarVisibility="Disabled"`, the Grid column, or a
  programmatic override).  Filtering to a single non-Commands category
  does not help, so it's not the Commands section's fixed-width Border
  forcing parent expansion.  Files: `SettingsControl.axaml`,
  `SettingsControl.axaml.cs`, `SettingRowFactory.cs`.  Next-attempt
  ideas: (a) wrap the StackPanel in a Border with the constraint;
  (b) put the constraint on the ScrollViewer itself; (c) Avalonia
  DevTools breakpoint on the StackPanel's `BoundsProperty` to see what's
  actually setting its width.

- **Preserve caret/selection viewport position on wrap toggle** — caret
  and selection anchor should keep their visual row-from-top when
  WordWrap is toggled.  Touch points: WordWrap toggle, `InvalidateLayout`,
  `ScrollCaretIntoView`.

- **Render churn during caret blink with selection present** — memory
  pulses with every blink when text is selected.  `InvalidateVisual`
  is probably scoped too wide; may be amplified by per-draw allocations
  on the new mono path.

- **Home/End on wrapped continuation rows** — should move to row start
  first, then logical-line start on second press.  Trivial on the mono
  path (RowSpan lookup); harder on `TextLayout` slow path.

- **"Editor Font" reset button in Settings** — single reset that
  restores both family and size.  Lives in `SettingRowFactory`.

- **Memory growth while scrolling a large document** — `MonoLineLayout.Draw`
  allocates a fresh `ushort[]` glyph buffer + `GlyphRun` per row per draw;
  the `TextLayout` path cached its glyph runs.  Cache the arrays (and
  possibly `GlyphRun` instances).  `GlyphRun` is `IDisposable` — currently
  only `Layout?.Dispose()` runs, `Mono` is left to GC.

- **CharWrap not triggering on 1MB single-line file** — opposite of the
  2026-04-03 fix.  Suspect the long-line branch in the loader or the
  `LineTooLongDetected` net isn't running before layout.

- **Small event-handler closure leaks (DMEdit-owned)** — VS Memory Insights
  flagged ~3KB of compiler closures rooted past their useful life.  Hygiene,
  not a real leak — each one points at an event subscription that didn't
  unsubscribe.  Worst offender `MainWindow+<>c__DisplayClass157_1` (472B);
  also two `DispatcherTimer+<>c__DisplayClass18_0` (caret blink + one other).

- **Tab handling on the monospace fast path** — currently any tab line
  falls back to `TextLayout`.  Implement column-aware advance in
  `MonoLineLayout.NextRow`/`Draw` (`(col / indentWidth + 1) * indentWidth`).
  Bootstraps the column-width accumulator that elastic tabstops will need.

- **Per-frame `FormattedText` churn in ToolbarControl/TabBarControl** —
  ~1,312 `TextLineImpl`s allocated by `ToolbarControl.Render` over a 50s
  recording, ~60 by `TabBarControl.Render`.  Same fix as the `DrawGutter`
  cache: per-control `Dictionary<string, FormattedText>` keyed by
  label/glyph + visual state, theme/font invalidation, soft cap.

- **Caret blink could redraw only the caret rect** — `OnCaretTick` calls
  `InvalidateVisual()` which forces full Render.  Move caret to a sibling
  Control with its own narrow Render, blink invalidates only that control.

- **Avalonia upstream issues (file if motivated)** — `NameRecord` string
  interning duplicates ~150KB of font copyright/description strings;
  AXAML resource URIs duplicated in two forms each repeated 100–235×.
  One-time startup costs, not blocking.

- **Block model / WYSIWYG editor** — fully designed and partially
  implemented but not wired in.  Will be optional for Markdown.
  See [02-document-model](design-journal/02-document-model.md).

- **Windows 11 Mica transparency** — researched, not implemented.
  See [05-features](design-journal/05-features.md).

- **Toolbar Undo/Redo buttons** — not yet implemented.

- **Storage-backed large edits** — Add buffer should spill to disk above
  a configurable threshold.  PieceTable already treats Add as opaque via
  `BufFor()` / `VisitPieces`, so the boundary is in place.

- **Delayed clipboard rendering** — `GetSelectedText()` materializes the
  full selection.  Windows `IDataObject` / `OleSetClipboard` supports
  delayed render; Avalonia's `IClipboard` doesn't expose it (needs
  platform-specific interop).  30s render timeout caveat.

- **ErrorDialog → "Report a Bug" button** — wire `GitHubIssueHelper.OpenFeedbackIssue`
  via the `buttons:` array when an error is genuinely reportable.  Skip
  for environmental errors (printer busy, file permission).

- **Windows installer (Velopack + GitHub Releases)** — self-contained
  installer with auto-update support.  `vpk pack` + `vpk upload github`.
  Requires GitHub repo set up first.

- **Guard against accidental whole-document string materialization** —
  `ConvertIndentation` already streams via `BulkReplaceVarying`;
  `ConvertLineEndings` already streams in `FileSaver`.  Remaining concern:
  `ReplaceAll` match-collection phase on huge documents.

- **LineIndexTree** — implicit treap supporting O(log L) insert/remove of
  lines.  Line lengths built during `PagedFileBuffer.ScanWorker`.  Undo
  uses zero-copy piece-based re-insertion.  30GB redo memory explosion
  fixed in the 2026-03-24 reliability pass.
