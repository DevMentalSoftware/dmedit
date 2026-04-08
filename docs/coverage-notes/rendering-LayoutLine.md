# `LayoutLine`

`src/DMEdit.Rendering/Layout/LayoutLine.cs` (130 lines)
Tests: indirectly via `TextLayoutEngineTests`.

Sum-type-as-class wrapping either a `TextLayout` (slow path) or
a `MonoLineLayout` (fast path). Exposes a path-agnostic API so
`EditorControl` never branches.

## Likely untested

- **`IsMono` property** — `Mono is not null`. Trivial.
- **Double-dispose safety** — `_disposed` flag guards.
- **`HitTestTextRange(start, 0)`** on either path — zero-length
  range semantics differ between `TextLayout` and
  `MonoLineLayout`. Worth a test to pin symmetry.
- **`HitTestTextPosition(posInLine > CharLen)`** — not clamped
  here; forwarded to the inner layout which may throw or
  return garbage. EditorControl clamps before calling.
- **`Render` with a null `foreground`** — Mono path uses its
  own Context.Foreground, TextLayout path uses whatever was
  baked into the layout. Two different behaviors for the
  same API. Worth a comment.

## Architectural concerns

- **Sum-type as "exactly one of Layout/Mono is non-null"** is
  enforced by the two ctors but not by the type system. Could
  be a `record` discriminated union in C# 12+, but .NET
  doesn't have first-class DUs yet. Current approach is fine.
- **`Render`, `HitTestTextPosition`, `HitTestTextRange`,
  `HitTestPoint`** each do the same `if (Mono is { } mono)`
  dispatch. Would be slightly cleaner with a virtual dispatch
  via an internal abstract base, but that would require two
  inner classes. Current explicit dispatch is readable enough.
- **`CharEnd` is `CharStart + CharLen`** — a property not a
  field. Fine.
