# `GitHubIssueHelper`

`src/DMEdit.App/Services/GitHubIssueHelper.cs` (117 lines)

Opens a browser tab with a pre-filled GitHub issue URL for
bug reports or feedback.

## Likely untested

- **URL construction with special characters** — title and
  body must be URL-encoded.
- **`OpenFeedbackIssue` with a very long body** — GitHub
  truncates URLs past ~8KB. Worth a length check.
- **Cross-platform browser launch** — `Process.Start(url)`
  on Windows vs Linux vs macOS.
- **Template content** — pre-filled bug report template.
  Drift between this and the actual issue template on
  GitHub is possible.

## Architectural concerns

- **Hardcoded repo URL** — same as `UpdateService`. Could
  share a constant.
- **Best-effort launch** — failure mode is silent (no
  browser opens). Worth a try/catch with user feedback.
