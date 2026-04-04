# 16 — Print Progress Dialog

**Date:** 2026-04-03
**Status:** Implemented

---

## Problem

Printing large documents had no visual feedback.  The user couldn't tell
anything was happening after clicking Print.  They could even close the
document while printing was in progress, risking a crash since the paginator
reads lines live from the PieceTable.  For large files (29K+ pages), the
measurement pass (`ComputePageBreaks`) was extremely slow due to creating a
WPF `FormattedText` object per line.

## Solution

### Progress dialog

Modal `ProgressDialog` shown during printing (reusing the existing dialog
from ReplaceAll).  Two-phase progress:

1. **Measurement phase**: "Measuring line X of Y..." — throttled to 200ms
2. **Rendering phase**: "Page X of Y — N pages/sec, ~Xm Xs remaining"

Dialog uses monospace font (`Consolas`) for stable number layout and
throttles visual updates to 200ms intervals.

### Measurement optimization

`ComputePageBreaks` rewritten to use `LineIndexTree` arithmetic instead of
`GetLine()` + `FormattedText` per line:

- Line lengths computed via `LineStartOfs(i+1) - LineStartOfs(i)` — no text
  read needed
- Wrapped row count = `ceil(lineLen / charsPerRow)` using monospace char width
- 29,207 pages: went from "frozen on Preparing..." (many seconds) to 331ms

### Rendering optimization

`GetPage` row splitting rewritten for monospace:

- Fixed-width split at `charsPerRow` intervals — pure arithmetic
- Removed `WrapLine` (FormattedText + height measurement)
- Removed `FindBreakPosition` (binary search with repeated FormattedText)
- Removed `CountWrappedLines` (FormattedText per line)
- Still uses `MakeFormattedText` for actual drawing (WPF requirement)

### Cancellation

- During measurement: `CancellationToken.ThrowIfCancellationRequested()`
  checked every 200ms
- During rendering: `GetPage()` returns blank pages on cancellation so
  `writer.Write()` finishes quickly
- Dialog closes immediately on cancel via `CancellationToken.Register`
  callback; print thread continues in background
- Guard against concurrent prints via `_printTask` field
- OS print queue job may linger — WPF provides no safe way to cancel just
  our job without risking other programs' jobs

### Error handling

`ISystemPrintService.Print` returns `bool` instead of throwing:
- `true` = success
- `false` = error or cancelled
- No exceptions cross thread boundaries

### ProgressDialog enhancements

- `showCancelButton` parameter (default true) to optionally hide cancel
- Monospace font on message TextBlock
- 200ms throttle on `Update()` to reduce flicker

## Performance (29,207-page document)

| Phase | Before | After |
|-------|--------|-------|
| ComputePageBreaks | Many seconds (frozen) | 331ms |
| GetPage rendering | ~53 pages/sec | ~53 pages/sec (still FormattedText-bound) |

## Future

Replace `MakeFormattedText` in `GetPage` with `GlyphRun` for monospace
rendering — would skip WPF's text layout engine entirely and could
dramatically improve pages/sec.

## Files modified

- `src/DMEdit.Core/Printing/ISystemPrintService.cs` — `Print` returns bool,
  accepts `IProgress` and `CancellationToken`
- `src/DMEdit.Windows/WpfPrintService.cs` — progress threading, monospace
  pagination, cancellation, removed WrapLine/FindBreakPosition
- `src/DMEdit.App/MainWindow.axaml.cs` — ProgressDialog in PrintAsync,
  cancel handling, concurrent print guard
- `src/DMEdit.App/ProgressDialog.cs` — monospace font, update throttle,
  showCancelButton parameter
