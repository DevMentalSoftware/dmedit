# `PrinterInfo`

`src/DMEdit.Core/Printing/PrinterInfo.cs` (9 lines)

Two-field POCO. `Name` + `IsDefault`.

## Likely untested
- Trivial.

## Architectural concerns
- **Only one "IsDefault" can exist** — no invariant enforces
  this, but the list consumer should treat multiple IsDefault
  as an error.
