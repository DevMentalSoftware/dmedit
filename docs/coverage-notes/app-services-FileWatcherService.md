# `FileWatcherService`

`src/DMEdit.App/Services/FileWatcherService.cs` (272 lines)

Watches open file paths for external modifications. Raises
events so the UI can prompt the user to reload. Notes in the
journal mention "file watching notes" under 08-status-bar.

## Likely untested

- **Whole class** — no tests.
- **`FileSystemWatcher` event coalescing** — multiple rapid
  changes should produce one notification after a debounce.
- **File renamed to a different path** — the watcher should
  follow or drop tracking.
- **File deleted then re-created** — event sequence.
- **Tail mode** — continuous polling as lines are appended.
- **Cooldown between reloads** (`TailReloadCooldownMs` in
  AppSettings).
- **Dispose while events pending** — race.
- **Path casing differences** on Windows.
- **Network share disconnection** — `FileSystemWatcher`
  throws `InternalBufferOverflowException`.

## Architectural concerns

- **`FileSystemWatcher` is notoriously flaky** on Windows
  across network shares and mapped drives. Worth a
  polling-based fallback.
- **Per-file watcher vs per-directory watcher** — 10 open
  files would mean 10 watchers unless coalesced by parent
  directory.
- **UI thread marshaling** — the `Changed` event fires on a
  threadpool thread; the handler must dispatch to UI.
