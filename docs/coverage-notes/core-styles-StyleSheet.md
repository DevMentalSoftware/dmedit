# `StyleSheet`

`src/DMEdit.Core/Styles/StyleSheet.cs` (240 lines)
Tests: `StyleSheetTests.cs`.

Maps BlockType/InlineSpanType to BlockStyle/InlineStyle. Holds
`WrapWidth`, default styles, and `CreateDefault()` factory.

## Likely untested

- **`GetBlockStyle` / `GetInlineStyle` fallback to default** when
  no style is registered. Tested?
- **`UpdateFontMetrics` on a type not explicitly registered** —
  mutates the `_defaultBlockStyle` (via `GetBlockStyle`), which
  is a shared singleton. **Surprise: updating font metrics for
  one un-registered block type mutates the default for all
  un-registered types.** Probably a bug. Worth a test.
- **JSON serialization round-trip** — `StyleSheet` appears to
  be designed for persistence but I don't see `LoadFromJson`/
  `SaveToJson` methods (the truncated view stopped at line 240).
  Check whether it's implemented and tested.
- **`CreateDefault()`** — 140 lines of style setup. Tested?

## Architectural concerns

- **The default style is a single shared instance** (`_defaultBlockStyle`).
  `GetBlockStyle(unknownType)` returns this same instance, so
  any mutation affects *all* callers. Dangerous for
  `UpdateFontMetrics`. Fix: return a copy, or throw if the
  caller tries to update metrics for an unregistered type.
- **Huge `CreateDefault()` method** — 140 lines of hand-tuned
  styles. Consider loading from a JSON resource so designers
  can iterate on the defaults without a recompile. Future work.
- **`WrapWidth` is on the StyleSheet**, not a rendering parameter.
  A single document can only have one wrap width. Fine for now.
