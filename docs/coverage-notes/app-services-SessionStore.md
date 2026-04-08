# `SessionStore`

`src/DMEdit.App/Services/SessionStore.cs` (440 lines)

Persists open tabs, their contents (via edit replay), SHA-1
checkpoints, and window state. On startup restores all tabs.
Journal entry 10 ("Session Persistence & Memory Safety")
details recent work on this.

## Likely untested

- **Session round-trip** — save N tabs, restart app, load N
  back. Indirect coverage via `EditHistorySerializationTests`
  but no end-to-end.
- **Tab with an unsaved edit** — edit JSON + add-buffer
  binary side-car. Replay.
- **Tab with a file conflict** — SHA-1 mismatch on reload.
  `FileConflict` should be raised.
- **Tab pointing at a deleted file** — `FileConflictKind.Missing`.
- **Corrupted session.json** — should not crash; treat as
  empty session.
- **Concurrent save** — user quits while save is in flight.
- **Schema evolution** — newer app format on older session.
- **Add-buffer side-car file missing or truncated.**
- **Session with hundreds of tabs.**

## Architectural concerns

- **440 lines** — moderate size but dense.
- **Binary side-car + JSON main file** — the add-buffer is
  binary to preserve UTF-8 bytes exactly (entry 12). The
  JSON references offsets into it. Two-file consistency is
  load-bearing.
- **Schema has no version field** — per the `AppSettings`
  note, new fields are added and old ones kept for compat.
  Works with `WhenWritingNull` but is not documented.
- **Edit replay can fail** — the `EditReplayFailed` conflict
  kind exists for this. Handled per journal entry 10.

## Bugs / hazards

1. **Session restore performance** on many-tab sessions —
   each tab's edit replay is O(edits). Cumulative cost could
   slow startup.
2. **Corrupt session = lost work** — worth writing
   `session.json.bak` before overwriting.
