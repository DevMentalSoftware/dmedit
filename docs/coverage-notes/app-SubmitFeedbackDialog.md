# `SubmitFeedbackDialog`

`src/DMEdit.App/SubmitFeedbackDialog.cs` (279 lines)

Dialog for submitting feedback via `FeedbackClient` to the
Azure Function endpoint. Falls back to opening a pre-filled
GitHub issue URL if the network call fails.

## Likely untested

- **Submission success path** — dismiss with confirmation.
- **Network error** → fallback to GitHub URL (via
  `GitHubIssueHelper`).
- **Empty body validation** — should prevent submission.
- **Email field validation** — if optional, no check; if
  required, format check.
- **PII scrubbing** — is any identifying info sent? Should
  be user-controlled.
- **Attach stack trace** option (if this is the "report
  crash" entry point).

## Architectural concerns

- **Direct dependency on both `FeedbackClient` and
  `GitHubIssueHelper`** — tight coupling. A single
  `IFeedbackSubmitter` abstraction with primary + fallback
  implementations would be cleaner.
- **Modal network call** — should not block the UI thread.
  Uses async/await presumably.
