# `Command`

`src/DMEdit.App/Commands/Command.cs` (101 lines)
Tests: `tests/DMEdit.App.Tests/CommandRegistryTests.cs`.

Single command object: identity, menu/toolbar metadata, runtime
execute delegate. Constructed as static readonly fields in
`Commands.cs` and later `Wire()`d with runtime actions.

## Likely untested

- **`Wire` called twice** — second call replaces the delegate.
  Safe but surprising. No assertion.
- **`Run()` on a never-wired command** — `_execute?.Invoke()`
  returns null, `Run` returns `true` because `IsEnabled`
  defaults to true. A silent no-op. Worth pinning or changing
  to `return false` when `_execute is null`.
- **`IsEnabled` with a throwing `_canExecute`** — the exception
  propagates out of `Run`. Not pinned.
- **Menu access-key stripping** (`DisplayName = dn.Replace("_", "")`)
  — tests? "Save _As\u2026" should yield "Save As…".
- **`MenuDisplayName`** preserving the `_` — tests?
- **Flags' defaults** — `RequiresEditor = false`,
  `IsAdvanced = false`, etc. Trivial.

## Architectural concerns

- **Menu is `internal set`** and set by `Commands.DefineMenus`.
  That's fine, but it means between construction and
  `DefineMenus` the property is default. A caller reading
  `Menu` before init gets wrong data.
- **`Run` returns bool "executed"** but doesn't distinguish
  "disabled" from "no-op" (unwired). A tri-state would be
  clearer; in practice the caller doesn't care.
- **Mutable runtime slots** (`_execute`, `_canExecute`) —
  internal. Thread-safety: commands are wired once at
  startup. Assumed single-threaded access, not enforced.
