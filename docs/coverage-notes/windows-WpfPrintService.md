# `WpfPrintService` (Windows)

`src/DMEdit.Windows/WpfPrintService.cs` (655 lines)
Tests: none (WPF interop).

Windows print service via WPF's `DocumentPaginator` and
`System.Printing`. Implements `ISystemPrintService`. Hosts
the `PlainTextPaginator` inner class that handles the
monospace GlyphRun fast path (per journal entry 19) and the
legacy `FormattedText` fallback. Recently overhauled for
`PrintResult` plumbing.

## Likely untested

### Printer enumeration
- **`GetPrinters`** — spooler stopped, permissions denied.
  Catch block returns empty list silently.
- **No default printer set** — `LocalPrintServer.GetDefaultPrintQueue()`
  returns null. Handled.

### Paper sizes
- **`GetPaperSizes`** for a queue with no capabilities —
  catches and returns empty.
- **`PageMediaSizeName == Unknown`** — passes through with
  the enum value `0`.
- **`FormatPaperName` for common vs custom sizes.**
- **Unit conversion (1/96 inch → 1/72 inch)** math.

### Print
- **`Print` on STA thread** — runs on a dedicated STA
  thread.
- **`PrintResult` exception path** — wraps
  `RuntimeWrappedException` per journal entry 19.
- **Cancellation** mid-print.
- **Progress reporting** — measure phase vs print phase.
- **GlyphRun vs FormattedText path** based on
  `ticket.UseGlyphRun`.
- **Monospace-detection fallback** when the user picks a
  proportional font.
- **Non-US locale number formatting** (FormattedText).
- **Word-break at row width** — the `NextRow` logic
  mirrored in `MonoLineLayout` per the journal.
- **Pagination for documents with 29K+ pages** — was frozen
  before entry 16; fixed via `LineIndexTree` arithmetic.
- **Concurrent print guard** via `_printTask` in MainWindow
  (not here).

### Error paths
- **Managed-C++ `RuntimeWrappedException` unwrap** —
  per journal, this is how WPF reports print errors.
- **`PrintResult.Failed` with message + details** —
  message shown plain, details only in DevMode.

## Architectural concerns

- **655 lines** — big but purposeful. The STA-thread
  orchestration, progress reporting, and pagination all
  live here.
- **WPF-specific** — can't run on non-Windows.
- **Pagination logic partly duplicated** in
  `PlainTextPaginator.NextRow` vs
  `MonoLineLayout.NextRow`. Journal notes this. Shared
  `PlainTextPaginator` in Core would dedupe.
- **STA thread management** — documented in the comments.
  Sharp corner: any exception on the STA thread that
  escapes must be converted to strings before crossing
  back to the caller thread (per `PrintResult` comment).
- **Reflection discovery** from `WindowsPrintService` in the
  App project — loose coupling. Good.

## Bugs / hazards

- **"Friendly error messages with raw detail in DevMode
  expander only"** (journal entry 19) — the gate is
  `AppSettings.DevModeAllowed`. Both sides must agree on
  what DevMode shows.
- **`RuntimeWrappedException` unwrap** — if a new kind of
  managed-C++ exception shows up, the unwrap path has to
  be extended. One-liner catch.
- **`_printTask` concurrent guard is in MainWindow**, not
  here — so a caller that bypasses MainWindow could start
  two prints. Low risk; the abstraction is "call through
  MainWindow."
