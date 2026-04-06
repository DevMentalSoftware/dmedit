## 2026-02-28 — Performance stats panel (Phase 1 complete)

Added a `PerfStats` infrastructure to `EditorControl` for measuring layout and render
performance. `TimingStat` tracks exponential moving average (α=0.1), min, and max.
Stats displayed in a status bar at the bottom of the window (DevMode only), updated
every 5th render frame via `Dispatcher.UIThread.Post` to avoid invalidating visuals
mid-render pass. Also fixed `ClipToBounds` on `EditorControl` — wrapped text was
painting past the control's bounds into the status bar area.

## 2026-02-28 — Status bar design (TODO — deferred)

The status bar infrastructure is in place (Border + TextBlock, docked bottom). Currently
it only shows dev stats. The following items should be added as permanent status bar
segments visible in all modes:

### Left section
- **Caret position**: `Ln 42, Col 17` — line and column of the caret
- **Selection count**: `(42 selected)` — character count when text is selected

### Center section (DevMode only)
- **Perf stats**: Layout/Render timing (EMA + min/max), line counts, scroll %

### Right section
- **Indent style**: `Spaces: 4` or `Tab Size: 4` — clickable to toggle/change
- **Line endings**: `CRLF` or `LF` — clickable to convert
- **Encoding**: `UTF-8`, `ANSI`, etc. — clickable to change
- **Document type**: `Text`, `Markdown`, `XML`, etc. — dropdown, initially just `Text`
- **Zoom**: percentage, TBD

### Design notes
- Inspired by VS Code / Visual Studio status bars
- Each segment should eventually be clickable (opens a picker or toggles the setting)
- Word count may be useful for Markdown mode (writing-focused editors like iA Writer show this)
- File dirty indicator (●) complements the title bar asterisk
- Git branch display deferred — adds dependency, not core to text editing

## 2026-02-28 — Streaming file loader (StreamingFileBuffer)

Replaced `LazyFileBuffer` (mmap, UTF-16 LE only) with `StreamingFileBuffer` — a true
streaming IBuffer that reads files in 1 MB binary chunks on a ThreadPool worker thread.

Key design:
- Pre-allocates `char[]` sized to file byte length (worst-case UTF-8)
- Uses `System.Text.Decoder` for incremental UTF-8 decoding (handles split multi-byte
  sequences at chunk boundaries)
- BOM detection: UTF-8, UTF-16 LE, UTF-16 BE; default UTF-8
- Builds line-start index (`long[]`) incrementally as chunks arrive via `ScanNewlines`
- Thread safety: `volatile _loadedLen` acts as memory barrier; background writes to
  `_data[_loadedLen..]`, UI reads `_data[0.._loadedLen-1]`; `_lineStarts` growth
  protected by `lock`
- Events: `ProgressChanged` (per chunk), `LoadComplete` (when done)
- Integrates with existing `WholeBufSentinel` — PieceTable's `Length` / `VisitPieces`
  call `ResolveLen()` which reads `_loadedLen`, growing seamlessly as chunks arrive

`FileLoader` threshold lowered from 200 MB to 10 MB. Files ≤ 10 MB use
`File.ReadAllText` + `StringBuffer`. Files > 10 MB use `StreamingFileBuffer`.
`LazyFileBuffer.cs` deleted.

Stats bar shows two-part load time for streaming files: "Load: 17.5ms + 553.0ms"
(first renderable chunk + total). Non-streaming files show a single time.

Performance: 185 MB UTF-8 file — first chunk 17.5ms, total 553ms (was a UI freeze).

## 2026-02-28 — Save optimization (two-path FileSaver)

Save was 1034ms for 185 MB (slower than load). Three bottlenecks identified:
1. 64 KB chunk size → 2800 iterations
2. `VisitPieces` WholeBufSentinel path allocated `new char[take]` per chunk
3. `StreamWriter` default buffer is small

Rewrote `FileSaver` with two paths:
- **Fast path** (`SaveFromBuffer`): unedited buffer-backed docs (`IsOriginalContent`),
  reads directly from `IBuffer.CopyTo` in 1 MB chunks, bypasses PieceTable entirely.
  Thread-safe, no PieceTable access. Result: 180ms.
- **General path** (`SaveFromPieceTable`): edited docs, 1 MB chunks with 1 MB
  StreamWriter buffer via `table.ForEachPiece`.

Added `PieceTable.IsOriginalContent` and `PieceTable.OrigBuffer` properties.
Save already uses `Task.Run` (non-blocking UI).

## 2026-02-28 — Fix: UI lockup on Enter key in 185 MB document

After pressing Enter in a 185 MB document, PieceTable splits from 1 piece to 2–3.
The single-piece delegation fast path (`LineCount`, `LineStartOfs`) no longer applies.
`LineCount` saw `Length > EagerIndexThreshold` and returned `-1`. `EnsureLayout()`
interpreted `-1` as "small enough for full layout" and called `GetText()`, materializing
100M+ chars → freeze.

Fix: `PieceTable.LineCount`, `LineStartOfs`, `LineFromOfs`, and `GetLine` now fall back
to the buffer's line index (`_origBuf.LineCount`, `_origBuf.GetLineStart(idx)`) when the
document exceeds `EagerIndexThreshold` and the buffer has a line index. These are
**approximate** (off by ±edit chars) but prevent the freeze by keeping windowed layout
engaged. Added `BinarySearchBufferLines` helper for `LineFromOfs`.

A slight one-time JIT hesitation on the very first edit is expected — .NET compiles the
new buffer-fallback code paths on first execution. Subsequent edits are instant.

## 2026-02-28 — Edit timing stat

Added `TimingStat Edit` to `PerfStatsData` and `_editSw` stopwatch to `EditorControl`.
All edit operations in `OnKeyDown` (Enter, Tab, Backspace, Delete, Undo, Redo) and
`OnTextInput` are instrumented. Stats bar shows "Edit: 0.12ms (0.08–0.45)" after any
edit occurs.

Test count: **216** (195 Core + 21 Rendering).

## 2026-02-28 — Fix: scroll-to-bottom with word wrap

When scrolled to the bottom of a large document with word-wrapping, the last few lines
were clipped below the viewport. Root cause: `LayoutWindowed` maps scroll offset to
`topLine` using a **global** `avgLineHeight`, but lines near the end may wrap more than
average. The actual rendered content is taller than estimated, pushing the last lines
below the viewport.

Fix: after laying out the windowed content, if `bottomLine >= lineCount` (we're at the
end) **and** we're at max scroll (`_scrollOffset.Y >= scrollMax - 1`), check whether the
content bottom extends past the viewport. If so, anchor `_renderOffsetY` to
`_viewport.Height - _layout.TotalHeight` so the last line sits at the viewport bottom.

The `>= scrollMax - 1` guard is critical — without it, the anchor fires continuously
while scrolling up from the bottom (because the fetched lines still include the end),
making scroll-up appear stuck. With the guard, the anchor only engages at actual max
scroll; the first scroll-up click starts moving immediately.

Note: Notepad++ has a much worse version of this same problem with word-wrap enabled —
scrolling to the bottom consistently shows 200–800 lines short of the real end, and the
position is non-deterministic. Our approach is approximate (global-average scroll mapping)
but deterministic, and the bottom-anchor ensures the true end is always reachable.

## 2026-02-28 — Smooth partial-row scrolling with wrapped lines

Windowed layout's formula `_renderOffsetY = topLine * avgLineHeight - scrollOffset` had
discontinuities when `topLine` changed — the render offset jumped by `avgLineHeight` while
scroll only moved by one row height. This caused jitter and whitespace gaps.

Fix: incremental render offset tracking with four cached fields (`_winTopLine`,
`_winScrollOffset`, `_winRenderOffsetY`, `_winFirstLineHeight`). For single-row scrolls
(ds < 2*rh, i.e., arrow button clicks), topLine is constrained to change by ±1 and the
offset is computed using actual cached line heights. Multiple safety clamps prevent top gaps
(`_renderOffsetY > 0`), bottom gaps (content doesn't reach viewport bottom), and resize
instability (incremental state is NOT reset on viewport resize, only on content changes).

## 2026-02-28 — Transparent zip file support

Auto-detect zip files by magic bytes (PK header: `50 4B 03 04`). Single-entry zips are
transparently decompressed and streamed into the existing `StreamingFileBuffer`. Multi-entry
zips are rejected with an informative error.

Key changes:
- `StreamingFileBuffer` gained a second constructor accepting a `Stream` + `IDisposable?`
  owner. Refactored `LoadWorker` and `DetectEncodingAndCreateDecoder` to work with any
  `Stream` (not just `FileStream`). BOM detection handles non-seekable streams by returning
  prefetched bytes.
- `FileLoader` now returns `LoadResult(Document, DisplayName, WasZipped)` instead of raw
  `Document`. Added `IsZipFile()` helper and `LoadZip()` for the zip path. Small zips
  (≤ 10 MB uncompressed) read entirely into memory; larger ones stream via
  `StreamingFileBuffer`. The `ZipArchive` is owned by the buffer and disposed when loading
  completes.
- `MainWindow` updated to use `LoadResult.DisplayName` for the title bar (shows
  "archive.zip → inner.txt" for zips). `SaveAs` suggests the inner entry name for zipped
  sources.
- Save always writes plain text (no re-zipping).

13 new tests in `ZipFileTests.cs`. Test count: **229** (208 Core + 21 Rendering).

## 2026-02-28 — Memory usage in stats bar

Added `MemoryMb` and `PeakMemoryMb` fields to `PerfStatsData`, sampled via
`GC.GetTotalMemory(false)` each render frame. Stats bar now shows
"Mem: 245 MB (max 312 MB)" alongside load/save timing. Peak tracks the session maximum.

## 2026-02-28 — Future features noted in development plan

Two future milestones documented in the plan file:

1. **Open Folder** — folder browsing with metadata database, text/md file searching,
   multi-file zip support, and nested zip/folder handling.

2. **Memory stats** — implemented this session (see above).

## 2026-02-28 — Memory-efficient large file support (PagedFileBuffer)

Three-phase project to achieve sublinear memory scaling for large files.

### Phase 1 — Eliminate `_origBufCache` doubling

`PieceTable.BufFor(BufferKind.Original)` previously materialized the entire `IBuffer`
into a cached string on the first edit. For a 185 MB file that meant +370 MB on the
first keystroke. Removed `_origBufCache`; `VisitPieces`, `CharAt`, and `BuildLineStarts`
now access `_origBuf` directly. Trade-off: more transient allocations per read (small
`char[]` each time) but no persistent doubling. Allocation budget tests raised accordingly.

### Phase 2 — `PagedFileBuffer`

New `IBuffer` implementation (`Buffers/PagedFileBuffer.cs`, ~637 lines) that keeps only
a bounded LRU cache of decoded text pages in memory. The raw file stays on disk, locked
with `FileShare.Read`.

Architecture:
- **Background scan**: reads file in 1 MB chunks, decodes via `Encoding.GetDecoder()`
  (handles multi-byte boundaries), builds `PageInfo[]` (byte↔char offset mapping) and
  a sampled line index (every 1024th line's char offset).
- **LRU cache**: `LinkedList<int>` + `Dictionary<int, LinkedListNode<int>>` for O(1)
  promote/evict. Default `MaxPagesInMemory = 8` ≈ 16 MB decoded cache.
- **On-demand reload**: evicted pages re-read from disk via `FileStream.Seek` + decode
  (~1 ms per page on SSD). `lock(_fs)` ensures seek+read atomicity.
- **BOM detection**: UTF-8, UTF-16 LE, UTF-16 BE, or no-BOM (assumes UTF-8).
- **`IBuffer` additions**: `IsLoaded(offset, len)` and `EnsureLoaded(offset, len)`
  (default `true`/no-op in `IBuffer`) for placeholder rendering support.
- **Thread safety**: `Interlocked.Read`/`Exchange` for `_lineCount` and `_totalChars`
  (C# forbids `volatile long`); `lock(_lock)` for `_pageData`, LRU, and `_lineSamples`.

Memory budget (928 MB file, ~5M lines, MaxPages=8):
  PageInfo[] ≈ 30 KB, decoded cache ≈ 16 MB, line index ≈ 40 KB → **~16 MB total**.
  User-tested: MaxPages=3 → 42 MB, MaxPages=8 → ~60 MB, MaxPages=128 → 500 MB.
  No perceptible scrolling delay at any cache size on SSD.

24 new tests in `PagedFileBufferTests.cs`.

### Phase 3 — FileLoader integration + placeholder rendering

Three-tier file routing in `FileLoader` (configurable via `AppSettings.PagedBufferThresholdBytes`):
  ≤ 10 MB → `File.ReadAllText` (string)
  10 MB–50 MB → `StreamingFileBuffer` (~2× memory)
  > 50 MB → `PagedFileBuffer` (bounded ~16 MB)

Zip streams always use `StreamingFileBuffer` (non-seekable, can't re-read pages).

`EditorControl` checks `IsLoaded()` before fetching text in `LayoutWindowed`. When
pages aren't loaded, `EnsureLoaded()` queues thread-pool loads and grey rounded-rectangle
placeholders render in place of text. `ProgressChanged` fires per page load, triggering
`InvalidateLayout()` which replaces placeholders with real text.

`MainWindow.WireStreamingProgress` now handles `PagedFileBuffer` alongside
`StreamingFileBuffer` for progress bar updates during the initial scan.

Test count: **253** (232 Core + 21 Rendering).

## 2026-04-06 — Cold startup optimization

Investigated ~2 second cold startup on Windows (from taskbar click). Profiler showed
warm startup is fast — nearly all time is Avalonia framework init
(`StartWithClassicDesktopLifetime`, `Window.ctor`, `TextLayout.ctor`) and JIT.

### Applied

- **ReadyToRun (R2R)**: Added `PublishReadyToRun=true` to `release.yml` (Windows +
  Linux publish commands) and `packaging/macos/build-macos.sh`. Pre-compiles IL to
  native code at publish time, eliminating JIT overhead on cold start. Noticeable
  improvement.

### Tried and ruled out

- **PublishSingleFile**: No additional benefit on top of R2R (NVMe makes file I/O
  count irrelevant).
- **Removing `WithInterFont()`**: Inter font assembly is tiny relative to Avalonia —
  no measurable difference.
- **Windows Defender exclusion**: Added build output to `ExclusionPath` — no change.
- **Deferring settings panel init**: Moved `SettingsPanel.Initialize` (~130 control
  rows) from `MainWindow` constructor to first `OpenSettings()` call. No perceptible
  difference because Avalonia framework init dominates. Reverted.
- **Trimming (`PublishTrimmed`)**: Ruled out without testing. Primary benefit is
  deployment size, not startup speed. Would require JSON source generators for
  `AppSettings`/`SessionManifest` serialization and risks breaking Velopack reflection
  and runtime-loaded `DMEdit.Windows.dll`.

### Findings

- ~1 second overhead launching from taskbar shortcut vs double-clicking exe directly.
  Shortcut points to `dmedit.exe` (not a Velopack stub). This is Windows shell
  activation overhead — not controllable from the app.
- RAMMap standby-list flush did not reproduce cold start delay on NVMe hardware.
- Remaining startup time is Avalonia framework init, outside our control.
