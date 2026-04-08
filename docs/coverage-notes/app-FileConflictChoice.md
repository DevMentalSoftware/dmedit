# `FileConflictChoice` (enum)

`src/DMEdit.App/FileConflictChoice.cs` (15 lines)

4-value enum: LoadDiskVersion, LocateFile, KeepMyVersion, Discard.

## Likely untested
- Trivial enum.

## Architectural concerns
- **`LocateFile` only valid for Missing conflicts** — no type
  guard; the consumer has to handle wrong combinations.
