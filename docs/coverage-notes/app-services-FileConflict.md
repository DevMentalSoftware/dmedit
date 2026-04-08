# `FileConflict` + `FileConflictKind`

`src/DMEdit.App/Services/FileConflict.cs` (25 lines)

POCO + enum describing a detected conflict between the editor's
version of a file and what's on disk.

## Likely untested
- Trivial data class. Consumer (session restore, file
  watcher) is where the logic lives.

## Architectural concerns
- **`ActualSha1` is nullable**, `ExpectedSha1` is `required`
  but also nullable — mixed semantics. `required T?` is
  legal but confusing. Worth a comment explaining when each
  is null.
