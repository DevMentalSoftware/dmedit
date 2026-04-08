# `SettingDescriptor` + `SettingKind`

`src/DMEdit.App/Settings/SettingDescriptor.cs` (23 lines)

Record describing one settings entry (name, kind, default,
constraints, hidden flag, enabled-when dependency).

## Likely untested
- Trivial record.

## Architectural concerns
- **`object DefaultValue`** — boxed. Could be generic
  `SettingDescriptor<T>` but then the registry list couldn't
  be a single `IReadOnlyList`.
- **`Min`/`Max` as `object?`** — same concern.
- **`EnabledWhenKey` is a string reference to another key** —
  no compile-time check. Typo → silently disabled condition.
