# `MonoLayoutContext`

`src/DMEdit.Rendering/Layout/MonoLayoutContext.cs` (104 lines)
Tests: indirectly via `TextLayoutEngineTests` tests that use a
monospace font.

Shared monospace fast-path state. Caches glyph indices for the
printable ASCII range and a dictionary for the rest. Holds
computed metrics (CharWidth, Baseline, HangingIndentPx).

## Likely untested

- **`IsMonospace(glyphTypeface)`** — one-line delegation to
  `Metrics.IsFixedPitch`. Trivial.
- **`TryGetGlyph` fallback path** for chars the font doesn't
  cover — returns false with `glyph = fallbackGlyph`. Wait —
  looking closer: `if (GlyphTypeface.TryGetGlyph(c, out glyph))
  { _extraGlyphs[c] = glyph; return true; }` and then `return
  false`. So on a miss, `glyph` is zero (default), not the
  fallback. The `_fallbackGlyph` field is only used in the
  ASCII prefill at line 77. Inconsistent. Worth a test.
- **`TryGetGlyph` for a low-BMP non-printable** (`c < 32`) —
  returns false with `glyph = 0`. Tested indirectly via
  `MonoLineLayout.TryBuild`'s control-char rejection.
- **Dictionary cache stability** — adding the same char twice
  should only look up once. Not asserted.
- **Font metrics with `Ascent == 0`** — baseline computation
  `Math.Abs((double)Ascent) / emHeight * fontSize` would give
  `0`. Some pathological fonts. Not pinned.

## Architectural concerns

- **`_fallbackGlyph` is only used during ASCII prefill** (line
  77), yet the field is stored. The per-char `TryGetGlyph`
  does not fall back to it. Either use it consistently
  (return `_fallbackGlyph` on miss, semantically "show a box
  instead of nothing") or remove the field.
- **`_extraGlyphs` is a `Dictionary<int, ushort>`** — a BCL
  Dictionary per LayoutResult. Fine; the total lookups per
  scroll frame are small.
- **No per-context disposal** — this class doesn't implement
  `IDisposable`. The `IGlyphTypeface` it holds is owned by
  Avalonia and not released here. Correct.
- **`HangingIndentPx = HangingIndentChars * CharWidth`**
  computed once in the ctor. If the user changes font size
  while scrolling, a new context is built per frame, so the
  value stays fresh. OK.

## Simplification opportunities

- **Use `frozendict` or a small preallocated slot** for common
  Latin-1 above 128. Micro-opt.
- **Fallback glyph** logic cleanup per above.
