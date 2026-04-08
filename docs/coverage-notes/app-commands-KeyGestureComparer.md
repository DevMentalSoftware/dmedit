# `KeyGestureComparer`

`src/DMEdit.App/Commands/KeyGestureComparer.cs` (23 lines)

Tiny equality comparer for `KeyGesture`. Singleton instance.

## Likely untested
- **`Equals(null, null) == true`** — `ReferenceEquals` early
  return. Trivial.
- **`Equals(x, null)` or `Equals(null, y)`** — returns false.
- **`GetHashCode` consistency** with `Equals` — unasserted but
  easy to check.

## Architectural concerns
- **Exists only because `KeyGesture` doesn't override
  Equals/GetHashCode.** If Avalonia fixes that, this class
  becomes redundant.
