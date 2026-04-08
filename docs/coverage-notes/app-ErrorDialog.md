# `ErrorDialog`

`src/DMEdit.App/ErrorDialog.cs` (162 lines)

General-purpose themed error dialog. Configurable title, body,
detail expander, buttons array. Per journal: resizable, DockPanel
layout, used for all global crashes.

## Likely untested

- **Buttons array** — each button returns a distinct result
  code. Tested?
- **Detail expander** — raw exception shown in DevMode only.
- **Background-thread invocation** — per the journal, the
  "fatal error dialog deadlock" was fixed by using `Show()`
  + manual modality instead of `ShowDialog()` on the
  background thread. Sharp corner.
- **DockPanel layout** — content vs buttons regions.
- **`Report a Bug` button** — journal lists this as a
  deferred item. Wire `GitHubIssueHelper.OpenFeedbackIssue`
  for reportable errors.

## Architectural concerns

- **Single dialog for all errors** — good consolidation.
- **DevMode gated detail display** via
  `AppSettings.DevModeAllowed` — correct.
- **Modal vs non-modal flow**: the fatal-error path uses
  `Show()` + manual wait on a ManualResetEventSlim.
  Journal lists this as the "hard watchdog" open item —
  if the event is never set, the process hangs forever.
