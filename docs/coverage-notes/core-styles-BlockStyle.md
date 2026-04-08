# `BlockStyle`

`src/DMEdit.Core/Styles/BlockStyle.cs` (148 lines)
Tests: `StyleSheetTests.cs` (indirect).

Visual styling POCO + `EstimateHeight` helper.

## Likely untested

- **`EstimateHeight(0, wrapWidth)`** — returns one-line + chrome.
  Expected but not asserted directly.
- **`EstimateHeight` with `wrapWidth <= PaddingLeft + PaddingRight`**
  — `effectiveWidth <= 0`, clamped to 1. Pathological but not
  asserted.
- **`MaxVisibleLines` cap** — capped estimates for code/table.
  Should be tested.
- **`AvgCharWidth` feedback loop** — renderer calls
  `StyleSheet.UpdateFontMetrics`, which mutates the BlockStyle's
  `AvgCharWidth`. No test verifies that subsequent
  `EstimateHeight` calls use the updated value.
- **`ComputedLineHeight` = FontSize × LineHeight** — trivial;
  no asserts.
- **`TotalVerticalMargin`/`Padding`** — trivial.

## Architectural concerns

- **Mutable POCO.** Every property has `{ get; set; }`. Style
  changes reach the renderer via shared reference, which means
  thread-safety is the caller's problem. For a user settings
  page that's fine; for concurrent style updates it's not.
- **`ForegroundColor` / `BackgroundColor` are strings** (CSS hex).
  Parsed at render time, presumably. String-based color
  specification means "red" or "#F00" vs "#FF0000" need
  deterministic handling by the renderer. No test here.
- **`[JsonIgnore]` on `AvgCharWidth` and derived props** —
  correct for serialization but not enforced by a test.
- **`IsMonospace` is informational** but `EstimateHeight` ignores
  it (still uses `AvgCharWidth`). The class comment at line 33-34
  says monospace "makes height estimation exact" but
  `EstimateHeight` doesn't branch on it. Either remove the
  promise in the comment or implement the fast path.
