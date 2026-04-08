# `ProfileLoader`

`src/DMEdit.App/Commands/ProfileLoader.cs` (46 lines)
Tests: `tests/DMEdit.App.Tests/ProfileLoaderTests.cs`.

Static loader for embedded JSON profiles. Uses reflection to
find manifest resources named `DMEdit.App.Commands.Profiles.{id}.json`.

## Likely untested

- **`Load("NonexistentProfile")`** throws
  `InvalidOperationException`. Tested?
- **Malformed JSON resource** — `JsonSerializer.Deserialize`
  throws; the method wraps? No, it lets the exception
  propagate. Worth a test.
- **`GetDisplayName` with a profile whose `Name` is empty**
  — returns the profileId. Tested?
- **`ProfileIds` completeness** — the six listed must all have
  corresponding embedded resources. A smoke test that loops
  `Load(id)` over each would catch a missing resource.
- **`ProfileLoader` loads from the executing assembly** —
  `Assembly.GetExecutingAssembly()`. In test harnesses that
  load `DMEdit.App.dll` dynamically, this should still find
  the resources. Worth verifying.

## Architectural concerns

- **Static, no caching.** `GetDisplayName` calls `Load` —
  which re-reads and re-parses the JSON — every time. The
  profile dropdown calls `GetDisplayName` for all six
  profiles every time it opens. Tiny cost; worth a dictionary
  cache anyway.
- **No way to inject test profiles** — the static reflection
  loader is hard to mock. For integration tests this is OK;
  for unit tests of `KeyBindingService`, there's no seam.
