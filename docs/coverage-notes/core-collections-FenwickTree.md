# `FenwickTree` (double-valued)

`src/DMEdit.Core/Collections/FenwickTree.cs`
Tests: `tests/DMEdit.Core.Tests/FenwickTreeTests.cs`

## Likely untested

- **`new FenwickTree(size)` without `FromValues`.** Every positive test
  uses the `FromValues` factory. The bare constructor + `Update` path is
  only hit by the `new FenwickTree(0)` edge cases. Spot check: construct
  with `size = 5`, `Update(i, v)` for each i, verify `PrefixSum`.
- **Bounds exceptions.** `Update(-1, …)`, `Update(_n, …)`, `PrefixSum(-1)`,
  `PrefixSum(_n)` all call `ArgumentOutOfRangeException.ThrowIfNegative` /
  `ThrowIfGreaterThanOrEqual`. None of those throws are asserted.
- **`Count` property.** No direct assertion — only observed transitively
  via `Rebuild`.
- **`Rebuild`'s two branches.** Tests hit the "reuse existing array" path
  (grow from 3→4, shrink from 5→2). The "need a larger array" path
  (`_tree.Length < _n + 1`) is not explicitly exercised — grow from 3 to
  8 would do it.
- **`FindByPrefixSum` with target equal to a mid-prefix boundary after
  many updates.** Randomized property test would catch sign/rounding
  issues with non-integer doubles (current tests all use integer doubles).

## Architectural concerns

- **Near-duplicate of `IntFenwickTree`.** 152 lines here vs 170 there,
  differing only in storage type (`double` vs `int`/`long`) and the
  `ExtractValues` helper. A generic `FenwickTree<T> where T : INumber<T>`
  would dedupe cleanly under .NET 7+. Would need a microbenchmark to
  confirm `INumber<T>` generic math doesn't regress the hot path.
- **`_n` is almost redundant** with `_tree.Length - 1`. It only matters
  when `Rebuild` shrinks below capacity (the array is kept, `_n` tracks
  the live prefix). Worth a comment there.

## Simplification opportunities

- **`HighestOneBit`** can be `1 << BitOperations.Log2((uint)n)` or
  `(int)BitOperations.RoundUpToPowerOf2((uint)n + 1) >> 1`. Same answer,
  one line, no loop. Both Fenwicks have an identical copy.
- The walk in `FindByPrefixSum` has a trailing `return pos < _n ? pos : -1;`
  — the guard at the top already handles `target <= 0` and `_n == 0`, so
  the only way `pos == _n` is when the target exceeds the total. Works,
  but the code would be clearer as an explicit `if (target > TotalSum()) return -1;`
  precheck. (The cost of that precheck is `O(log n)`, so the current
  integrated walk is actually preferable — leave as-is, just add a comment.)
