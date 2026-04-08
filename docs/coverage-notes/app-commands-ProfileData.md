# `ProfileData`

`src/DMEdit.App/Commands/ProfileData.cs` (18 lines)

JSON deserialization target for key mapping profiles. `Name`,
`Bindings`, `Bindings2`.

## Likely untested
- **Missing `Name` field** in a JSON file — property default
  is `""`. Untested.
- **Missing `Bindings` / `Bindings2`** — both nullable. Tested
  implicitly via the six embedded profiles.

## Architectural concerns
- **POCO with public setters** — required for JSON deser.
  After load, mutating these properties would desync the
  KeyBindingService's lookup tables. Document the "treat as
  immutable after load" convention.
