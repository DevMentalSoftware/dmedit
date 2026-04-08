# `EditorControl`

`src/DMEdit.App/Controls/EditorControl.cs` (5 007 lines — the
largest file in the codebase by a wide margin)
Tests: none direct. The closest coverage is `DMInputBoxTests`
for the single-line DM chrome control and various integration
tests that go through commands.

Implements `Control, ILogicalScrollable, IScrollSource`. Owns
the whole editor UI: layout orchestration, caret rendering,
pointer handling, keyboard input, scroll math, gutter, wrap
logic, incremental search, perf stats, clipboard ring,
column-mode UI, mouse selection, edit coalescing, and
theme/font property plumbing. Wires many Core commands and
services together.

## Likely untested (the big summary)

### Rendering
- **`Render` pipeline** — no test touches it. Visual regression
  is manual via Dev menu + test documents.
- **Caret layer dispatch** on every blink vs full redraw —
  worth a test asserting `CaretLayer.InvalidateVisual` does
  NOT cause `EditorControl.Render` to be called.
- **Column-caret pool growth** — the `_columnCaretPool` grows
  monotonically. No test asserts that a 100-cursor selection
  followed by a collapse to 1 caret leaves 100 layers in the
  pool and reuses them on the next column selection.

### Layout
- **Row-height device-pixel snapping** — critical for
  inter-line alignment. No direct test.
- **Viewport windowed layout** — `LayoutLines(topLine,
  bottomLine)` called with a visible window. No test that
  verifies only the visible lines are laid out.
- **CharWrap mode math** — `_charWrapCharsPerRow`, `CharsPerRow`.
  Core logic in a 5k-line file; exercised only through
  integration scenarios.
- **`LineTooLongDetected` event** — fired when
  `LayoutLines` throws `LineTooLongException`. Should trigger
  CharWrap. Per the journal, this has a known bug
  ("CharWrap not triggering on 1MB single-line file").

### Input
- **Keyboard handling / command dispatch** — each of the
  100+ commands runs through EditorControl, which calls
  `Commands.*.Wire(...)` at construction. The flow is not
  tested end-to-end; each command relies on its Core-side
  test.
- **Chord gesture dispatch** — the first-key / second-key
  state machine lives here. `KeyBindingServiceTests` tests
  the lookup; the state machine is not unit-tested.
- **Pointer events** — click, drag, middle-drag for scroll,
  triple-click for line-select, Alt+drag for column mode.
  All manual.
- **Text input (IME, surrogate pairs coming in via paste)**
  — `SanitizeSurrogates` is tested at the Document level;
  the EditorControl entry point isn't.

### Scroll math
- **`HScrollValue` / `HScrollMaximum`**, `ScrollValue`,
  `ScrollExtentHeight`, `ScrollViewportHeight`, `RowHeightValue`
  — all implement `IScrollSource`. No test verifies
  consistency (e.g. `ScrollValue + ScrollViewportHeight
  == ScrollExtentHeight` when scrolled to bottom).
- **`_preferredCaretX` drift** — reset rules across typing,
  arrows, Home/End, click. Tested manually.

### Search / clipboard / misc
- **Incremental search state machine** (`_inIncrementalSearch`,
  `_isearchString`, `_isearchFailed`) — no tests.
- **Clipboard ring cycling** (`_isClipboardCycling`, index,
  insert length) — no tests.
- **Edit coalescing** (`_coalesceKey`, `_coalesceTimer`) —
  no tests. Known to work in practice but fragile.

### `PerfStatsData`
- **Nested perf-stats class** — 30+ fields. Memory / GC
  counters updated on each render. `SampleMemory` is called
  periodically. Not tested; not load-bearing for correctness.

## Architectural concerns (the big summary)

### Size
- **5 007 lines in one file.** Refactoring into partial
  classes by concern (Input, Scroll, Layout, Render, Search,
  Column, Caret, PerfStats, Coalesce, ClipboardRing) would
  mechanically split it into ~10 files of ~500 lines each
  without changing semantics. Each concern becomes
  approachable and testable.

### State
- **Dozens of private fields** — `_layout`, `_layoutFailed`,
  `_caretVisible`, `_inRenderPass`, `_primaryCaret`,
  `_columnCaretPool`, `_keepScrollOnSwap`, `_caretTimer`,
  `_pointerDown`, `_middleDrag`, `_columnDrag`,
  `_middleDragStartY`, `_overwriteMode`, `_clipboardRing`,
  `_isClipboardCycling`, `_clipboardCycleIndex`,
  `_cycleInsertedLength`, `_preferredCaretX`,
  `_coalesceKey`, `_compoundOpen`, `_coalesceTimer`,
  `_lastSearchTerm`, `_inIncrementalSearch`, `_isearchString`,
  `_isearchFailed`, `_zoomPercent`, `_wrapLines`,
  `_wrapLinesAt`, `_charWrapMode`, `_charWrapCharsPerRow`,
  `_indentWidth`, `_showLineNumbers`, `_showWhitespace`,
  `_useWrapColumn`, `_showWrapSymbol`, `_hangingIndent`,
  `_useFastTextLayout`, `_theme`, plus layout caches
  (`_rowHeight`, `_charWidth`, `_extent`, `_viewport`,
  `_gutterWidth`). It's a lot to keep in one's head.
  Grouping into sub-structs (`InputState`, `SelectionState`,
  `ScrollState`, `LayoutState`) would help.

### Coupling
- **Knows about `Document`, `PieceTable`, `EditHistory`,
  `LayoutResult`, `TextLayoutEngine`, `ClipboardRing`,
  `KeyBindingService`, `EditorTheme`, `AppSettings`,
  `CommandRegistry`.** Owns the wiring between them; many
  commands are hand-connected in a ctor helper method.
  A command-to-action registry indexed by `Command.Id` would
  reduce the hand-wiring by 200+ lines.

### Event storm
- **Exposes ~10 events** (`HScrollChanged`, `ScrollChanged`,
  `StatusUpdated`, `LineTooLongDetected`, `MetadataChanged`,
  `OverwriteModeChanged`, `BackgroundPasteChanged`, plus
  the implicit `Changed` it subscribes to on `Document`).
  Each is a separate subscription / unsubscription concern.
  When the document changes, all the handlers have to be
  rewired. Worth a `DocumentBinding` helper that wires/un-wires
  atomically.

### Layout state / threading
- **`_inRenderPass` guard** (line 177) — defers event fires
  to avoid invalidating siblings during render. A sharp
  corner; worth a dedicated small helper that queues
  invalidations.
- **`_layoutFailed` flag** — sticky; once set, all layouts
  return empty. No way to clear it short of creating a new
  control. Worth a "retry next frame" path for transient
  failures (font-loading hiccups, etc.).

## Bugs / hazards (from journal + code read)

1. **"EditorControl caret X offset"** — open bug in the journal.
2. **"Memory growth while scrolling a large document"** —
   `MonoLineLayout.Draw` allocates per frame? Actually fixed
   per entry 21; the concern here is stale.
3. **"Render churn during caret blink with selection present"**
   — the `CaretLayer` split should have fixed this, but the
   journal lists it as still open. Worth verifying.
4. **"Home/End on wrapped continuation rows"** — open.
5. **"CharWrap not triggering on 1MB single-line file"** —
   open; `LineTooLongDetected` event isn't routing to CharWrap.
6. **Closure leaks** — the `_coalesceTimer` and caret timer
   closures are flagged in VS Memory Insights per the journal.
   Unsubscribe on dispose.

## Recommendation

**Refactor this file into partials first, then everything else
becomes feasible to review/test.** Suggested split:

```
EditorControl.Properties.cs   // styled properties + CLR wrappers
EditorControl.Input.cs        // keyboard, pointer, chord dispatch
EditorControl.Scroll.cs       // IScrollSource impl, horizontal scroll
EditorControl.Layout.cs       // InvalidateLayout, layout pass, row math
EditorControl.Render.cs       // Render override, gutter, caret layer mgmt
EditorControl.Selection.cs    // stream + column selection, mouse drag
EditorControl.Search.cs       // find bar wiring, incremental search
EditorControl.Coalesce.cs     // edit coalescing + compound groups
EditorControl.Clipboard.cs    // clipboard ring + PasteMore
EditorControl.PerfStats.cs    // perf data class + sampling
```

Approximate line counts: each partial would be 300-800 lines,
the main file `EditorControl.cs` drops to ~200 lines (fields,
ctor, events, `ApplyTheme`, cleanup). Every concern becomes
readable and each partial can get its own targeted tests.
