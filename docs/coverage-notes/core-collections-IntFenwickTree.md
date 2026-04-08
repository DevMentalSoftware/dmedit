# `IntFenwickTree` (int-valued, long prefix sums)

`src/DMEdit.Core/Collections/IntFenwickTree.cs`
Tests: `tests/DMEdit.Core.Tests/IntFenwickTreeTests.cs`

## Likely untested

- **`ExtractValues` has no direct test at all.** This is the only method
  on `IntFenwickTree` that has no counterpart on `FenwickTree`, and it
  implements a tricky 3-pass in-place undo/read/redo. The core of it:
  ```csharp
  for (var i = _n; i >= 1; i--) {
      var parent = i + (i & -i);
      if (parent <= _n) _tree[parent] -= _tree[i];
  }
  ```
  If the reverse loop order or the parent subtract is wrong, nothing
  would currently catch it (the re-propagate step would be masked). The
  similarly-named `LineIndexTree.ExtractValues` is tested, but it uses a
  completely different implementation — in-order traversal — so those
  tests provide no coverage for this method.
  **Priority: add a direct test.** Round-trip: `FromValues(v).ExtractValues()`
  must equal `v`; after some updates, should reflect them; the tree
  should still answer `PrefixSum`/`FindByPrefixSum` queries correctly
  after `ExtractValues` runs (i.e. the restore step really restored it).
- **Bounds exceptions, `Count` property, `Rebuild`'s grow-the-array branch**
  — all same gaps as `FenwickTree.md`, same shape.
- **Per-element int overflow in `ValueAt`.** `ValueAt` casts a long
  difference back to int. Document-sized line lengths can't reach int
  range in practice, but the cast is unchecked. Not a bug today, worth a
  property test or an `Assert`.

## Architectural concerns

- **Heavy duplication with `FenwickTree`.** See the note there. If
  dedup'd to a generic, `ExtractValues` would need an `INumber`-friendly
  subtract/add which already exists.
- **`ExtractValues` is named like a test-only helper but lives on the
  production type.** If it's only ever used by tests, move it to an
  `internal` or test-only helper. If it's used by persistence/snapshots,
  document the one caller — I couldn't tell from a quick read whether
  anyone calls it.

## Simplification opportunities

- Same `HighestOneBit` shortening as the double version.
- `TotalSum() => _n > 0 ? PrefixSum(_n - 1) : 0` — at a small cost
  (O(log n)) this could be cached or computed via a single traversal,
  but it's already cheap. Ignore.
