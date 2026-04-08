# `ISystemPrintService` + `PrintResult`

`src/DMEdit.Core/Printing/ISystemPrintService.cs` (50 lines)

Platform print abstraction. Discovered via reflection. Implemented
by `WpfPrintService` in `DMEdit.Windows`.

## Likely untested
- **`PrintResult` factory methods** — `Ok`, `CancelledResult`,
  `Failed`. Trivial. No asserts.
- **No mock implementation for tests** — print path is exercised
  only by the real Windows implementation.

## Architectural concerns
- **Error info as strings** (the comment at line 9 explains: avoid
  crossing the STA print thread). Fine, but asymmetric with
  how every other error path in the codebase carries `Exception`
  objects.
- **`CancelledResult` is the only factory named with a `Result`
  suffix** — because `Cancelled` is already a field. A bit
  awkward. `CancelSuccess` or just `Canceled` would be odd for
  different reasons. Leave.
- **Reflection-based discovery** of the impl means a missing
  DLL produces a null service. Consumers have to handle the
  null case. No test.
