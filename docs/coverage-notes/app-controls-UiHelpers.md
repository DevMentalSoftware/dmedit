# `UiHelpers`

`src/DMEdit.App/Controls/UiHelpers.cs` (43 lines)

Internal static helpers. Point-to-pixel conversion,
`SetPathToolTip` that wraps long paths at directory separators.

## Likely untested
- **`ToPixels(int)`** — 9 → 12 exactly. Trivial.
- **`SetPathToolTip` with null path** — clears the tooltip.
- **`SetPathToolTip` with a path containing no separators**
  — no zero-width spaces inserted. Not asserted.
- **`SetPathToolTip` with mixed `/` and `\`** — both
  generate break opportunities. Not asserted.

## Architectural concerns
- **`ToPixels` is an extension on `int` only.** A `double`
  overload would cover non-integer points.
- **`SetPathToolTip` hard-codes FontSize = 9pt** and
  TextWrapping.Wrap. No override. Fine for its one job.
