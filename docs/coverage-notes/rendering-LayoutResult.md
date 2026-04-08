# `LayoutResult`

`src/DMEdit.Rendering/Layout/LayoutResult.cs` (55 lines)

Owns a list of `LayoutLine`s plus row-height metadata. `IDisposable`.

## Likely untested

- **Constructor with empty lines list** — `TotalRows = 0`,
  `TotalHeight = 0`. Not pinned.
- **`Dispose` called twice** — second call no-ops via
  `_disposed`. Not asserted.
- **`TopLine` setter** — public setter used by the gutter.
  Documented as avoiding a `LineFromOfs` lookup. Setter has
  no validation. If set to a value inconsistent with the
  actual first line, the gutter draws wrong numbers but
  doesn't crash.

## Architectural concerns

- **`TopLine` is a mutable property** on an otherwise-readonly
  result. Needed because the engine doesn't know which line
  index is visually first without an extra lookup. Could be
  a constructor param that the engine computes, removing the
  mutability. Minor.
- **`ViewportBase` is always 0 today** (comment at line 26).
  Placeholder for windowed rendering. Dead until that ships.
