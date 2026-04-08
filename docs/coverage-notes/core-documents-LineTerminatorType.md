# `LineTerminatorType` enum + extension

`src/DMEdit.Core/Documents/LineTerminatorType.cs` (35 lines)
Tests: used extensively by `LineScannerTests` and
`PagedFileBufferTests.TerminatorType_*`.

## Likely untested

- **`DeadZoneWidth` default branch** (`_ => 0`) — unreachable
  unless a bogus enum value is passed in. Dead code, kept for
  compiler completeness.
- **Direct DeadZoneWidth asserts** — tests consume it
  transitively via `LineContentLength`; no direct `Assert.Equal`
  against the extension.

## Architectural concerns

- **`None` means "last line of document."** That's a specific
  semantic, not "this line has no terminator for any reason."
  If a future use case needs "unknown terminator," it would
  collide.
- **`byte`-backed enum** — fine for the tight packing in
  `(long, LineTerminatorType)` terminator runs. Worth a comment
  at the enum stating the storage concern.

## Simplification opportunities

- None; this is correctly tiny.
