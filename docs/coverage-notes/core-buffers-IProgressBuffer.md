# `IProgressBuffer`

`src/DMEdit.Core/Buffers/IProgressBuffer.cs`
Tests: none direct.

Ten-line interface extending `IBuffer` with `ProgressChanged`,
`LoadComplete`, and `DetectedLineEnding`.

## Likely untested

- **Contract of `ProgressChanged`/`LoadComplete` ordering.** Implementors
  should fire `ProgressChanged` multiple times during load and
  `LoadComplete` exactly once afterward, and both from the background
  thread. This contract is assumed by `EditorControl`/`FileLoader` but
  not enforced anywhere. An interface-level test helper that subscribes
  to both and asserts `LoadComplete` comes last would catch regressions
  in any future implementor.

## Architectural concerns

- **`DetectedLineEnding` lives here but `DetectedIndent` and
  `DetectedEncoding` only exist on the concrete classes** (`PagedFileBuffer`,
  `StreamingFileBuffer`). The three are conceptually siblings — all
  metadata about the file content scanned in the background. Either
  hoist all three to the interface, or drop `DetectedLineEnding` out of
  it and make callers cast. Current split is a pattern drift.
- **The events have no `CancellationToken`-equivalent or unsubscribe
  guidance.** If a caller subscribes and the buffer is disposed mid-load,
  the handler leaks until the buffer is GC'd. Worth a `<remarks>`
  guiding callers to unsubscribe in their own teardown.
