# `IndentInfo` / `IndentStyle`

`src/DMEdit.Core/Documents/IndentInfo.cs`
Tests: `IndentInfoTests.cs`.

40-line record struct. `IndentStyle { Spaces, Tabs }` + detection
counts + dominant + IsMixed.

## Likely untested

- **`Label`** defaults to "Spaces" for any non-Tabs value (via the
  `_ => "Spaces"` switch arm). A future `Mixed` enum member would
  silently display as "Spaces". Not reachable today.
- **`FromCounts(0, 0)`** — returns `Default`. Tested.
- **`FromCounts` with `spaces == tabs`** — tie goes to Spaces
  (the `tabs > spaces` predicate). No test pins this.
- **`FromCounts(1, 0)` → not mixed, dominant Spaces** — trivial.
- **`IsMixed` math** — `spaces > 0 && tabs > 0`. Covered.

## Architectural concerns

- **Default stores `SpaceCount = 0, TabCount = 0`** — equivalent
  to "no detection performed yet." Can't distinguish from
  "detected but no indentation in document."

## Simplification opportunities

- **`Label`** could be `Dominant == IndentStyle.Tabs ? "Tabs" :
  "Spaces"`. Fine either way.
