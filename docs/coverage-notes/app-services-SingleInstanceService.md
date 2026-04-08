# `SingleInstanceService`

`src/DMEdit.App/Services/SingleInstanceService.cs` (96 lines)

Uses a named mutex + named pipe to ensure a single editor
process handles all file-open requests. Second instance sends
its file path to the first and exits.

## Likely untested

- **Second instance hand-off** — no tests.
- **Mutex abandoned** (first instance crashed without
  releasing) — `new Mutex(true, name, out created)` is
  documented as racy. Per journal key-deferred items, this
  exact pattern is called out as needing a fix:
  `new Mutex(false, name)` + `WaitOne(0)` + catch
  `AbandonedMutexException`.
- **Pipe not available / owner hung** — second instance
  fails silently. Per journal, a liveness ping via pipe
  with ~500ms timeout is the proposed fix.
- **Pipe error → retry loop** — `await Task.Delay(100, ct)`
  keeps looping. If the pipe is permanently broken, infinite
  retries.
- **Rapid multi-instance startup** — race at pipe
  creation.
- **Non-owning instance calling `StartListening`** — early
  return.

## Architectural concerns

- **Per journal**, three open items point at this file:
  1. Mutex pattern racy.
  2. Pipe-ping liveness check missing.
  3. Visible feedback when a launch is consumed (flash
     taskbar / focus existing window).
- **Single-writer pipe** — only one file at a time can be
  handed off. For the "open 10 files from Explorer" case,
  the user's file manager sends 10 separate launches; the
  sender side is fine, but the receiver processes them one
  at a time.
- **No cross-platform story** — named mutex doesn't exist on
  Linux; the class silently fails on non-Windows? Or does
  Avalonia have a cross-platform equivalent?
