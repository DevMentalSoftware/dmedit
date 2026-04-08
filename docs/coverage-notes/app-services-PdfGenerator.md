# `PdfGenerator`

`src/DMEdit.App/Services/PdfGenerator.cs` (192 lines)

Generates a PDF from a Document using the same pagination
logic as the print service.

## Likely untested

- **Entire class** — no tests.
- **Page layout with CRLF / LF / mixed content**.
- **Multi-page output pagination** — same `NextRow` / word
  break math as `MonoLineLayout` (per `MonoLineLayout.md`
  note). Duplication.
- **Long lines that wrap** — hanging indent handled?
- **Fonts not installed** — fallback.
- **Output file write errors**.

## Architectural concerns

- **Likely a dependency on PDFsharp or similar** — check
  the imports.
- **Shares pagination logic** with
  `WpfPrintService.PlainTextPaginator.NextRow` — journal
  mentions this mirror. A shared `PlainTextPaginator` class
  in Core would avoid drift.
