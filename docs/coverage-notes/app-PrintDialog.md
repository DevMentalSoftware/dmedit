# `PrintDialog`

`src/DMEdit.App/PrintDialog.cs` (313 lines)

Modal Print dialog. Printer list, paper sizes, orientation,
margins, page range, font size. Builds a `PrintJobTicket`.

## Likely untested

- **Printer list population** via `ISystemPrintService`.
- **Paper size list** per selected printer.
- **Common/All paper sizes toggle**.
- **Margins in inches/mm conversion**.
- **Page range parsing** — "1,3,5-9" style input.
- **Font size and family picker**.
- **PrintResult handling** on the caller side.
- **Printer missing from the list on reopen** (was available
  last session, now unplugged).
- **No printers at all** — graceful fallback.

## Architectural concerns
- **Duplicates some paper-size / margin math** from
  `PrintSettings`.
- **Hard-coded unit conversions** (1 inch = 72pt = 96px).
