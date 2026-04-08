# `FeedbackClient`

`src/DMEdit.App/Services/FeedbackClient.cs` (42 lines)

Posts `FeedbackPayload` to a hardcoded Azure Function URL.
Returns null on success, error string on failure.

## Likely untested

- **Every error path** — timeout, network, non-success status,
  generic exception. Four branches.
- **Success** — returns null. Untested (requires real endpoint).
- **Retry logic** — none. Single attempt.

## Architectural concerns

- **Hardcoded endpoint URL including the function key** — that
  key is effectively public. If leaked, abuse is possible.
  The exposure is already in source, but worth knowing.
- **Static `HttpClient`** with 15s timeout — good.
- **No rate limiting** — a user spamming feedback would
  succeed. Server side should throttle.
- **Error strings are user-facing** — formatted for display
  in a dialog. OK.
