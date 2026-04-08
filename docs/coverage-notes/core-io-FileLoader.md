# `FileLoader`

`src/DMEdit.Core/IO/FileLoader.cs` (174 lines)
Tests: `ZipFileTests.cs` covers zip path; `LargeDocumentTests.cs`
has one round-trip test.

`LoadAsync` dispatches to `LoadZip` (single-entry zip) or
`LoadPagedAsync` (everything else). Both wire up a `LoadComplete`
event that populates `LineEndingInfo`, `IndentInfo`, `EncodingInfo`,
and `BaseSha1`.

## Likely untested

- **`LoadAsync` with `CancellationToken` already cancelled** —
  behavior is to still construct the buffer and start loading,
  relying on the scan worker to check `ct`. Not asserted.
- **`LoadAsync` with a path that doesn't exist** — `new FileInfo`
  throws? Actually `new FileInfo(path).Length` throws
  `FileNotFoundException` lazily — yes on `.Length`. Untested.
- **`LoadAsync` on a directory path** — same error path.
- **`LoadAsync` on a file with no read permission.**
- **`LoadAsync` returns before the scan completes** — the `Task`
  is immediate; only `result.Loaded` awaits the scan. No test
  asserts this ordering or behavior of accessing `result.BaseSha1`
  before `Loaded` completes (should be null).
- **`LoadPagedAsync`'s `TrySetResult()`** — only fires once. If
  `LoadComplete` is somehow fired twice, the second set is a
  no-op. Untested contract.
- **`LoadZip` with a zip containing an entry whose `Length == 0`**
  — an empty entry. `uncompressedLen > 0` check falls through to
  `fs.Length * 4` estimate. Works, but worth a test.
- **`OpenZipEntry` cleanup path** — the `try/catch` at 150-153
  disposes the zip if construction throws after the `ZipArchive`
  is built but before ownership transfer. Untested.
- **`IsZipFile` on a file with exactly 3 bytes** — returns false
  (guard `read >= 4`). Tested.
- **`IsZipFile` on a file that starts `PK\x03\x05`** — false (zip
  local file header is specifically `03 04`). Not tested.

## Architectural concerns

- **`LoadResult.BaseSha1` is `{ get; set; }`** on a record. Mutable
  state on a record is a code smell — it exists so the
  `LoadComplete` handler can populate it after the result is
  returned to the caller. A cleaner design would return the
  SHA-1 via a property on the awaitable (e.g.
  `Task<string?> BaseSha1Loaded`). Low priority.
- **`LoadResult.Loaded` default is `Task.CompletedTask`** — the
  "already loaded" case. Currently neither code path uses it
  (both create a `TaskCompletionSource`). Dead default; keep
  for symmetry with future synchronous paths.
- **`FileLoader` is `static`** — no way to inject alternate
  file systems for testing. `System.IO.Abstractions` isn't used.
  Acceptable for a text editor; would be a problem for a cloud
  storage backend.
- **Zip handling refuses multi-entry archives.** This is
  explicit and documented, but surprising the first time a user
  hits it. The error message is clear.
- **`LoadAsync` wraps synchronous `LoadZip`/`LoadPagedAsync` in
  `Task.FromResult`.** The returned `Task<LoadResult>` is
  completed synchronously; the "Async" suffix is misleading.
  The caller then awaits `result.Loaded` for the real async
  part. Consider splitting into `StartLoad(path)` (synchronous,
  returns `LoadResult`) and letting the caller await
  `.Loaded` explicitly.

## Simplification opportunities

- **`LoadPagedAsync` and `LoadZip` are 95% identical.** Both
  create a buffer, wrap it in a Document, wire up `LoadComplete`,
  start loading. Could be unified with a buffer factory delegate
  + shared wire-up. Would reduce ~30 lines to ~15.
