# 19 — GlyphRun Print Path & Print Error Plumbing

**Date:** 2026-04-06
**Status:** Implemented

---

## Motivation

Two problems that looked like they might share a solution:

1. **WPF printing was `FormattedText`-bound.**  After the 2026-04-03 pagination
   rewrite (journal 16) measurement was fast, but rendering still created a
   fresh `FormattedText` per wrapped row.  Profiling showed
   `FormattedText+LineEnumerator.MoveNext` as the top function, and throughput
   topped out around **59–68 pages/sec** in Release on a ~29K-page test doc.

2. **Avalonia hanging indent was blocked** on the one-`TextLayout`-per-line
   architecture (journal 18).  A single `TextLayout` renders all its wrapped
   rows as one unit with no per-row X offset, so hanging indent needed either
   splitting into multiple `TextLayout` objects or doing glyph-level drawing
   ourselves.

Initially framed as a spike — write a monospace word-wrap + `GlyphRun` draw
path for WPF print, see whether the speedup is worth committing to and
whether the same primitives unblock Avalonia hanging indent.  In the end
both questions came back yes, and the "spike" code was production-quality
enough to ship as-is, so it stopped being a spike and became a completed
performance feature.

## Outcome

**WPF print: faster, with a new ceiling that isn't worth chasing.**

- Baseline (Release, FormattedText): 59–68 pages/sec.
- GlyphRun fast path at 100% coverage: **~101 pages/sec** (with word-break
  wrap).  Roughly **55–70% faster**.
- New profile: `FormattedText+LineEnumerator.MoveNext` is completely gone
  from Top Functions.  The new hot path is dominated by `system.printing.dll`
  (native XPS serialization / spooler).  Further speedup on this side would
  require bypassing `XpsDocumentWriter` entirely (GDI+ `PrintDocument`,
  P/Invoke spooler, generated PDF with shellexec) — not pursued, none of
  the options are worth the complexity for a batch operation.

**Hanging indent: unblocked.**

The confirmed primitives — `GlyphTypeface.CharacterToGlyphMap` plus manual
`DrawGlyphRun` per row, with a simple backward-scan word-break helper —
give us per-row X offset for free, which is exactly what hanging indent
needs.  Scope decision: hanging indent will only be supported when the
chosen font is monospace and the GlyphRun fast path is active.  Lines or
runs that fall through to `TextLayout` / `FormattedText` (proportional
fonts, missing glyphs, etc.) won't get hanging indent — they'll render
flush like today.  This avoids reproducing word-break wrap and per-row
positioning inside the proportional-font path, which would be a large
refactor for marginal benefit.

## WPF implementation

### Typeface resolution gotcha

`new Typeface("Cascadia Code, Consolas, Courier New")` produces a Typeface
with a fallback-list `FontFamily`.  `TryGetGlyphTypeface` returns **false**
on such a typeface — WPF does font fallback inside `FormattedText` shaping,
not at typeface construction, so there is no single concrete face to return.
First-pass GlyphRun code silently fell through to 0% coverage and only the
modest "first resolvable face won the race" speedup appeared.

Fix: on failure, walk the comma-separated family list, construct a
single-family `Typeface` for each, and take the first one whose
`TryGetGlyphTypeface` succeeds.  Log the chosen face via `Trace.WriteLine`
so anyone debugging can confirm it in DebugView.

### Fast path, with fallback

- Constructor precomputes a `ushort[128]` table mapping ASCII 32–126 →
  glyph index, plus a uniform `_glyphAdvance = AdvanceWidths[space] * fontSize`.
- `DrawRow` builds a per-row `glyphs[]` and `advances[]`, then calls
  `DrawingContext.DrawGlyphRun` with a `GlyphRun` constructed directly.
- Per-char lookup: ASCII → table; anything else → `CharacterToGlyphMap.TryGetValue`.
  If a char is a control character (`< 32`) or the font has no glyph for it,
  the row falls back to `FormattedText`.  Covers Latin-1 supplement and
  anything else the resolved face carries natively.
- On the user's ~1M-line random-length test file the fast path covers 100%
  of rows.

### Word-break wrap

The 2026-04-03 optimization dropped word-break wrap in favor of `charsPerRow`
arithmetic — that was a render-quality regression we needed back.  Restored
via a `NextRow(line, rowStart, charsPerRow)` helper: scans backwards from
the hard-row-end for a space and drops it from the drawn row; hard-breaks
mid-token only when no space is found in the row.

Both `ComputePageBreaks` and `GetPage` call `NextRow` so pagination and
rendering stay in sync.  Short lines (`length <= charsPerRow`) skip the
text read entirely — only long lines materialize via `GetLine`.  This makes
`ComputePageBreaks` read-heavy on docs dominated by long lines but the
measured cost was still acceptable (the user's 29K-page doc stayed quick
enough that the "Measuring…" phase wasn't visually painful).

### Font matching

The original print path hardcoded `"Cascadia Code, Consolas, Courier New"`
and `DefaultFontSize = 11.0` — the size was passed straight into
`FormattedText.emSize`, which is in WPF DIPs (1/96 inch).  The editor
stores `EditorFontSize` in typographic points (1/72 inch) and runs it
through `UiHelpers.ToPixels()` (`pts × 96 / 72`) before assigning to the
Avalonia control.  So the editor displayed 11pt (≈14.67 DIPs) but the
printout drew at 11 DIPs (≈8.25pt) — visibly smaller.

Fixed by threading the editor's font through the ticket:

- `PrintJobTicket.FontFamily` (string?) and `FontSizePoints` (double?) —
  both nullable so the defaults still apply when a ticket is built without
  font info (tests, future callers).
- `MainWindow.PrintAsync` sets them from `SettingRowFactory.GetEffectiveFontFamily(_settings)`
  and `_settings.EditorFontSize` after receiving the ticket from
  `PrintDialog`.  Same source the editor reads from, so the printout
  matches whatever face the user picked.
- `PlainTextPaginator` constructor accepts the family + point size,
  builds the `Typeface` from the family, and converts points → DIPs
  (`pts × 96 / 72`) before storing in `_fontSize`.  Single conversion
  point — no chance of double-conversion downstream.

### Diagnostic toggle

`AppSettings.UseGlyphRunPrinting` (bool, default `true`, not in the
Settings UI) flows to `PrintJobTicket.UseGlyphRun`, which `PlainTextPaginator`
honors.  Setting it to `false` in `settings.json` restores the legacy
`FormattedText` path for every row.  Intended only for bisecting a
GlyphRun-specific regression if one is ever reported.

### Remaining follow-ups (deliberately not done)

- **Monospace assumption is silent.**  The print path treats the chosen
  font as monospace: `_glyphAdvance` is computed once from the space
  glyph's `AdvanceWidths * fontSize` and then applied uniformly to every
  character.  When the editor was hardcoded to a monospace family this
  was always safe, but now that the printout uses whatever face the user
  picked in the editor's font picker (see *Font matching* below), a
  proportional choice (e.g. Calibri, Verdana) will render with mis-spaced
  rows on the GlyphRun fast path — every glyph drawn at the space's
  advance.  Fix options: (a) detect non-monospace at startup via a
  per-glyph advance comparison and fall back to `FormattedText` for the
  whole job, (b) build a per-character `advances[]` from
  `AdvanceWidths[glyph]` instead of using a uniform value (more correct
  but breaks the `charsPerRow` arithmetic in `NextRow`).  Most users
  print plain text in a monospace face, so this is a "fix when reported"
  item rather than urgent.

- **`PieceTable.GetLine(long)` allocations.**  Shows up in the post-spike
  profile for both the measurement pass (long lines only) and the render
  pass.  Could be eliminated with a `ForEachChar(lineIdx, delegate)` or
  `Span`-based accessor that skips the `string` allocation.  Small win;
  revisit only if print throughput matters again.
- **Leading-whitespace collapsing on wrapped rows.**  Current `NextRow`
  preserves a leading space on a continuation row if the break landed on
  a consecutive-space run.  Tolerable; can refine later.
- **Tab as a break character.**  Only U+0020 is treated as a break.
  Tab-separated content will hard-break mid-token.  Print is plain-text
  so this is unlikely to matter.
- **XPS serialization ceiling.**  The new top function is
  `system.printing.dll`.  Bypassing would mean GDI+ `PrintDocument`,
  P/Invoking the spooler, or generating PDF and shelling out.  None are
  worth the complexity for a batch print operation.

## Print error plumbing (collateral work)

The spike surfaced a silent-failure chain in the print error path that
got cleaned up along the way.

### `ISystemPrintService.Print` → `PrintResult`

Returned `bool` before.  On failure the thrown exception was caught on the
STA print thread and discarded; the caller saw `false` with no way to
show the user *why*.  Now returns a `PrintResult` record with
`Success`/`Cancelled`/`ErrorMessage`/`ErrorDetails` — exception data
formatted into strings on the producer side so nothing crosses the thread
boundary as an `Exception` object (still honoring the original no-throw
design rule).

### `RuntimeWrappedException` unwrap

WPF's `System.Printing` and `PresentationCore` have managed-C++ internals
that occasionally throw non-Exception objects.  The CLR wraps these in
`RuntimeWrappedException` with an opaque message ("An object that does not
derive from System.Exception…").  `PrintOnWpfThread` now catches
`RuntimeWrappedException` specifically, unwraps `.WrappedException`, and
synthesizes a readable `InvalidOperationException` whose `Message` names
the wrapped object type and short content.  The full original wrapper
still lives in the synthesized exception's `InnerException` for dev-mode
display.

### `PrintAsync` dialog tone

When print fails, the top of the dialog now reads as a plain-language
sentence ("An error occurred while printing and the job was not sent to
the printer.  If this keeps happening, check that the printer is
connected and powered on, and that no jobs are stuck or paused in the
Windows print queue.") instead of the synthesized exception `Message`.
Raw detail only appears in the DevMode expander.

### `AppSettings.DevModeAllowed`

Centralized the DevMode-gate check.  Debug builds return `true`
unconditionally; Release builds require `DMEDIT_DEVMODE=true` in the
environment.  The actual `settings.DevMode` bool is still a separate
user-toggleable switch — the gate only controls whether that switch is
honored.  `AppSettings.Load` and `SettingsControl` both use the new
helper so the rule is consistent.

### `WindowsPrintService` diagnostics

`Discover` used `Debug.WriteLine` — compiled out of Release, so any
discovery failure in a Release build was completely invisible.  Switched
to `Trace.WriteLine` and added a static `DiscoveryError` property that
captures the reason.  `MainWindow.FilePrint.Wire` now surfaces this in
the status bar when `IsAvailable` is false, so a silent-no-op click on
the Print button can never happen again.  Specific message for the
"stale `DMEdit.Windows.dll`" case (when `Activator.CreateInstance` returns
an object that doesn't satisfy the interface after a signature change) —
prompts the user to rebuild the solution.

## Files touched

**Print path:**
- `src/DMEdit.Core/Printing/ISystemPrintService.cs` — `PrintResult` record;
  `Print` return type.
- `src/DMEdit.Core/Printing/PrintJobTicket.cs` — `UseGlyphRun` flag.
- `src/DMEdit.Windows/WpfPrintService.cs` — GlyphRun fast path, `NextRow`
  word-break helper, typeface resolution walk, `RuntimeWrappedException`
  unwrap, `PrintResult` construction.
- `src/DMEdit.App/Services/AppSettings.cs` — `UseGlyphRunPrinting` hidden
  setting, `DevModeAllowed` helper.
- `src/DMEdit.App/Services/WindowsPrintService.cs` — `Trace.WriteLine`,
  `DiscoveryError` property, stale-dll diagnosis.
- `src/DMEdit.App/MainWindow.axaml.cs` — `PrintAsync` error-dialog path,
  ticket `UseGlyphRun` assignment, non-silent Print command wire.

**Settings:**
- `src/DMEdit.App/Settings/SettingsControl.axaml.cs` — `DevModeAllowed`
  helper adoption.
