# `PrintJobTicket`

`src/DMEdit.Core/Printing/PrintJobTicket.cs` (46 lines)

Mutable POCO carrying print job parameters.

## Likely untested
- **`required` modifiers** (PrinterName, Settings) — compiler-
  enforced at construction. No runtime asserts.
- **`UseGlyphRun` toggle** — hidden diagnostic. No test coverage.
- **Default values** (`Copies = 1`, `IndentWidth = 4`,
  `UseGlyphRun = true`) — trivial.

## Architectural concerns
- **Mixed `init` and `set`** — `PrinterName`/`Settings`/`Copies`
  are init-only; `FontFamily`/`FontSizePoints`/`IndentWidth`/
  `UseGlyphRun` are `{ get; set; }`. The split is subtle.
  Either make them all init (require a builder) or all set
  (more flexible).
- **`FontFamily` null means "let the service pick"** —
  overloaded semantics. A sentinel string like "default" would
  be clearer but less idiomatic C#. Leave.
