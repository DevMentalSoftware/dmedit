# `UpdateService`

`src/DMEdit.App/Services/UpdateService.cs` (72 lines)

Velopack-based auto-updater. Checks GitHub Releases, downloads,
and applies updates.

## Likely untested

- **`CheckAsync` with autoDownload = true** — e2e.
- **`CheckAsync` on a dev build (`IsInstalled == false`)**
  — early return. Not pinned.
- **`DownloadAsync` without a pending update** — early
  return. OK.
- **`ApplyAndRestart` without downloaded update** — early
  return.
- **Network failure during check** — Velopack handles?
  Probably propagates.

## Architectural concerns

- **Swallows "not installed" silently** — a dev user running
  the debug build sees no indication of update status. A
  log line would help.
- **Hardcoded `RepoUrl` constant** — shared with
  `GitHubIssueHelper`. Consolidate.
- **Per journal key-deferred** — Velopack setup is recent;
  "non-responsive after crash" report from byron suggests
  Velopack-emitted Portable.zip has its own startup path.
  Not this class's concern directly.
