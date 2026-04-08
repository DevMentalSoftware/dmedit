# `FeedbackPayload`

`src/DMEdit.App/Services/FeedbackPayload.cs` (167 lines)

Data class describing a feedback submission (bug report,
feature request). Has `ToJson` and maybe `FromJson`.

## Likely untested

- **Every field roundtripping** through JSON.
- **Required fields** — the payload presumably has some
  mandatory fields (email? description?). No validation
  visible.
- **PII handling** — if the user's name/email is included,
  it should be scrubbed or explicit-consent gated.

## Architectural concerns

- **Shape drift between client and server** — the Azure
  Function that receives this payload needs to stay in sync.
  No shared contract file.
