# `ChordGesture`

`src/DMEdit.App/Commands/ChordGesture.cs` (58 lines)
Tests: `KeyBindingServiceTests` chord cases hit it indirectly.

Sealed class wrapping a single or two-keystroke gesture. Implicit
conversion from `KeyGesture`. `Parse` accepts both single and
comma-separated chord strings.

## Likely untested

- **`Parse("Ctrl+K")`** — single key, works. Tested via
  `KeyBindingService` round-trip? Not directly.
- **`Parse("Ctrl+K, Ctrl+S")`** — chord. Tested indirectly.
- **`Parse("")` / `Parse(null-ish)`** — returns null via
  KeyGesture.Parse exception. Not pinned.
- **`Parse("Ctrl+K,")` / `Parse(",Ctrl+K")`** — malformed.
  Current code catches exceptions and falls through to
  single-key parse, which also fails, returning null.
- **`Parse("0")` rejection** — the comment at line 51 explains
  that `"0"` would parse as `Key.None` and should be rejected.
  Not explicitly tested.
- **`Parse("D0")`** — the "0" digit key. Should produce a
  valid gesture. Tested?
- **`ToString` for chord vs single** — tested?
- **Implicit conversion from `KeyGesture`** — tested
  implicitly by construction sites.

## Architectural concerns

- **Exception-driven flow control** in `Parse` — two nested
  try/catches. Works but not pretty. `KeyGesture.TryParse`
  doesn't exist in Avalonia, so catching is the only option.
- **No validation that the two halves of a chord are different
  modifiers.** A chord of "Ctrl+K, Ctrl+K" is legal.
