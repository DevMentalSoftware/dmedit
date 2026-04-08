# `CodepointBoundary`

`src/DMEdit.Core/Document/CodepointBoundary.cs`
Tests: `DocumentTests` has dedicated cases
(`CodepointBoundary_StepRight_OverPair_AdvancesTwo`,
`CodepointBoundary_StepLeft_OverPair_RetreatsTwo`,
`CodepointBoundary_WidthAt_BmpAndPair`,
`CodepointBoundary_SnapToBoundary_MidPair_SnapsForward`,
`CodepointBoundary_SnapToBoundary_MidPair_SnapsBackward`,
`CodepointBoundary_SnapToBoundary_OnBoundary_Noop`).

Central helper declared as "single source of truth" for "what is one
user-perceived character" in UTF-16 code-unit offsets. Matters for
every caret move, backspace/delete, and selection expansion.

## Likely untested

### `PieceTable` overloads (the first half of the file)
- **`WidthAt(table, ofs)` at `ofs == Length`** — returns 0. Not
  explicitly asserted.
- **`WidthAt(table, ofs)` at `ofs == Length - 1`** (last char, can't
  be a high surrogate of a pair even if it is one because there's
  no low surrogate after). The "ofs + 1 >= Length" guard at line 22
  returns 1. Untested.
- **`WidthBefore(table, 0)`** — returns 0.
- **`WidthBefore(table, 1)`** — returns 1 (the `ofs < 2` guard).
- **`StepRight`/`StepLeft` at a lone high surrogate without matching
  low.** The "pair" detection depends on `char.IsHighSurrogate(pair[0])
  && char.IsLowSurrogate(pair[1])`, so a lone surrogate yields width=1.
  `SanitizeSurrogates` is supposed to keep these out of the buffer,
  but if one slipped through, behavior is "step by 1." Worth a test
  that constructs a PieceTable with a lone surrogate (via
  `SanitizeSurrogates` round-trip through U+FFFD — which is the
  intended contract) and verifies no special behavior from the
  boundary helper.
- **`SnapToBoundary` at `ofs == 0`** and `ofs == Length` — early
  return. Not explicitly asserted.

### Span overloads (the second half)
- The span overloads (`WidthAt(text, idx)`, `StepRight`, etc.) look
  like simpler re-implementations of the PieceTable overloads but
  have their own test coverage only through `DocumentTests` which
  uses them indirectly via `SelectWord` and `ExpandSelection`. There
  are no direct unit tests for the span overloads.
- **`SnapToBoundary(text, idx, forward)` at `idx == 0` / `idx ==
  text.Length` / `idx == 1` (before a surrogate pair)**.
- **`WidthBefore(text, text.Length)` on a text ending in a
  surrogate pair** — should return 2.

## Architectural concerns

- **Two parallel sets of overloads** (one for `PieceTable`, one for
  `ReadOnlySpan<char>`). Both are load-bearing: the PieceTable ones
  do two-char reads through `GetText`, the span ones read directly
  from memory. The split is justified but the duplication is real;
  a private helper taking a `(PieceTable, long) → (char, char)?`
  delegate wouldn't be worth the abstraction.
- **`SnapToBoundary(pieceTable, …)` reads `table.GetText(ofs - 1, 2)`**
  — note `ofs - 1`, meaning the window straddles the offset. This
  is correct and matches the "is ofs splitting a pair?" semantics,
  but it is subtly different from `StepRight`/`StepLeft` which use
  `GetText(ofs, 2)` / `GetText(ofs - 2, 2)`. Worth a comment stating
  "the snap predicate wants the char on each side of `ofs`, not the
  char at `ofs`."
- **No guard against `ofs` between the high and low surrogate of
  `SnapToBoundary` — it will read across the boundary.** That's
  actually the intended behavior (snap pulls the offset onto a
  boundary), so not a bug, but it is the one place this class
  deliberately accepts a mid-pair offset.

## Simplification opportunities

- **`WidthBefore` and `WidthAt` overloads could be one-liners** using
  the span version if `table.GetText(ofs-1..ofs+1)` were cheap. It
  isn't — `GetText` allocates a string. Keep as-is.
- **`StepLeft` body of `return ofs - w`** at line 63 has the same
  shape as the `w == 0 ? ofs : ofs - w` pattern that would be more
  explicit. Micro.

## Bugs / hazards

- **No bug detected.** This is the file you *want* to be boring,
  and it's appropriately boring. The class comment even says "extend
  this helper instead" of re-introducing the stranded-half bug
  class — honor that comment and watch for PRs that add ad-hoc
  `char.IsHighSurrogate` checks elsewhere in the codebase rather
  than routing through this file.
