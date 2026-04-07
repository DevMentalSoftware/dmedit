# 22 — TextLayout Slow-Path Crash Hardening

**Date:** 2026-04-07
**Status:** Implemented

---

## Problem

Real user crash report from beta 0.5.231 (portable Windows install): the
dispatcher died while the user was scrolling through a large binary file in
DMEdit.

```
Type:    System.InvalidOperationException
Message: Cannot split: requested length 3 consumes entire run.
   at Avalonia.Media.TextFormatting.ShapedTextRun.Split(Int32 length)
   at Avalonia.Media.TextFormatting.TextFormatterImpl.SplitTextRuns(...)
   at Avalonia.Media.TextFormatting.TextFormatterImpl.PerformTextWrapping(...)
   at Avalonia.Media.TextFormatting.TextLayout.CreateTextLines()
   at Avalonia.Media.TextFormatting.TextLayout..ctor(...)
   at DMEdit.Rendering.Layout.TextLayoutEngine.MakeTextLayout(...)
   at DMEdit.Rendering.Layout.TextLayoutEngine.LayoutLines(...)
   at DMEdit.App.Controls.EditorControl.LayoutWindowed(...)
   at DMEdit.App.Controls.EditorControl.EnsureLayout()
   at DMEdit.App.Controls.EditorControl.Render(DrawingContext context)
```

The crash bubbled out of `Render`, set `_layoutFailed = true`, and was
re-thrown to the dispatcher fatal-error path. End of session.

This is a known class of Avalonia bug where `PerformTextWrapping` computes
a split position that ends up equal to the run length, then asserts in
`ShapedTextRun.Split`. It is reachable on shaped runs that mix complex
clusters near a wrap boundary — exactly the shape of binary file content
once the bytes get decoded into BMP code units.

## Why the existing fast path doesn't help here

After entries 19–21 most lines flow through the `MonoLineLayout` GlyphRun
fast path and never touch `TextLayout`. But:

- `MonoLineLayout.TryBuild` rejects any line containing `c < 32` or any
  codepoint the resolved font has no glyph for. Binary garbage hits both
  conditions on most lines and falls through to the slow path.
- The user can disable the fast path entirely (settings:
  `useFastTextLayout: false` on the `LayoutLines` call) — every line then
  routes through `TextLayout`.
- The reporter was running 0.5.231, which predates the GlyphRun work, so
  every line went through `TextLayout` regardless.

So the slow path still has live traffic, especially on the inputs most
likely to crash.

## Why per-character sanitization isn't enough on its own

Single-pass scrubbing of "obviously bad" code units cannot fully predict
what Avalonia chokes on. Surrogates, combining marks, regional indicators,
and ZWJ sequences interact across multiple code units: dropping or
replacing one unit can change the cluster boundary of the next, sometimes
constructing a *new* problematic cluster. The matching `Document` work for
buffer-side surrogate safety (in-progress journal entry) helps keep input
clean, but the render side still sees historical data and edge cases that
slip through.

The conclusion: scrub when we can, **but the load-bearing fix is a try/catch
at the call site**.

## Fix

Two layers in `TextLayoutEngine.LayoutLines`, slow path only.

### Layer 1: hygiene — `SanitizeForTextLayout`

Length-preserving scrub that runs once per slow-path line before
`MakeTextLayout`. Replaces with U+FFFD:

- C0 control characters other than `\t` (tab is preserved because the
  slow path handles it; `\r`/`\n` never appear inside a line — they are
  the line splitters).
- Lone (unpaired) UTF-16 surrogates.

Length-preserving means caller `CharStart`/`CharLen` arithmetic and
hit-testing still work after sanitization. Returns the same `string`
instance when the input is already clean — zero-allocation common case.

### Layer 2: defense — `MakeTextLayoutSafe`

Wraps `new TextLayout(...)` with two retries on `InvalidOperationException`:

1. Retry with `maxWidth: PositiveInfinity` → forces `TextWrapping.NoWrap`,
   bypassing `PerformTextWrapping` (the buggy code path) entirely. The
   line will overflow horizontally instead of crashing the dispatcher —
   strictly better failure mode, and the editor already supports
   horizontal scroll for non-wrapping content.
2. Last resort: build an empty `TextLayout`. The owning `LayoutLine` keeps
   its original `CharStart` / `CharLen` so document offsets stay
   consistent; the line just renders blank.

Only `InvalidOperationException` is caught — other exception types still
propagate to the existing `_layoutFailed` path so they're not silently
swallowed.

## Files touched

- `src/DMEdit.Rendering/Layout/TextLayoutEngine.cs` — added
  `SanitizeForTextLayout` and `MakeTextLayoutSafe`; the slow-path branch
  in `LayoutLines` now calls both.
- `src/DMEdit.Rendering/DMEdit.Rendering.csproj` — added
  `InternalsVisibleTo` for `DMEdit.Rendering.Tests` so the unit tests can
  exercise `SanitizeForTextLayout` directly.
- `tests/DMEdit.Rendering.Tests/TextLayoutEngineTests.cs` — 16 new tests:
  - sanitizer behavior (clean text instance-equality, tab preservation,
    NUL/lone-high/lone-low replacement, valid surrogate-pair preservation)
  - `LayoutLines` integration with binary garbage and lone surrogates,
    both narrow- and wide-wrap, both default and forced slow path
    (`useFastTextLayout: false`).

## Test baseline

521 Core + 47 Rendering + 89 App + 1 skipped = **657 tests**, all green.
(Previous: 510 + 31 + 89 + 1 = 631.)

## Notes / future

- The crash report format includes a stack trace ending at
  `DispatcherOperation.InvokeCore` — the existing fatal-error dialog
  caught it, but the user still lost their session. With this fix the
  same input renders garbled-but-stable.
- We never reproduced the *exact* code-unit sequence that triggered the
  Avalonia split bug — we don't need to, since the catch covers anything
  that ends up there. The integration tests assert that hostile inputs
  do not throw, which is the actual contract.
- A future improvement would be to teach `MonoLineLayout` to handle
  control chars (render them as a visible substitute glyph), which would
  remove the slow path from binary file viewing entirely. Out of scope
  for this fix.
