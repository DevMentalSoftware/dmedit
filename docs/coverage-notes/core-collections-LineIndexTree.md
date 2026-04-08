# `LineIndexTree` (implicit treap with sum + max + size)

`src/DMEdit.Core/Collections/LineIndexTree.cs`
Tests: `tests/DMEdit.Core.Tests/LineIndexTreeTests.cs`

The most complex collection in the codebase. Split/Merge treap, subtree
sum, subtree max, free-list allocator, bulk Cartesian-tree build. The
existing tests are substantial (a 5 000-op randomized property test plus
a 100 000-element stress test), but several specific code paths are
untouched.

## Likely untested

- **`MaxValue()` is never called by any test.** The `_max` array, the
  bottom-up recompute in `UpdateNode`, and the `LeftMax`/`RightMax`
  helpers have zero coverage. This is load-bearing — `PieceTable.MaxLineLength`
  returns `_lineTree.MaxValue()` (PieceTable.cs:149), which the CharWrap
  trigger depends on, which is tied to the real-user crash path fixed in
  entry 22. Any regression in `_max` maintenance would be silent.
  **Priority: high.** Add at least:
  - `MaxValue` on empty tree returns 0
  - `MaxValue` after `FromValues([10,20,30])` returns 30
  - `MaxValue` after `Update(i, delta)` makes the max larger, same,
    smaller
  - `MaxValue` after removing the unique max
  - `MaxValue` after `InsertAt` with a new largest value
  - `MaxValue` as a branch of the 5 000-op property test
- **`InsertRange` with a range larger than the `stackalloc` threshold
  (256 for `BuildBalanced`, 1 024 for `BulkBuild`).** Current tests only
  insert 2-element ranges. The `new int[span.Length]` heap-allocation
  branch of both builders is not exercised.
- **`InsertRange` with an empty span** (early return at line 161).
- **`RemoveRange` with `count <= 0`** (early return at line 169).
- **`RemoveRange` that removes the entire tree** — subtle because the
  free list grows large in one shot.
- **`InsertRange` at the very start / very end** of a non-empty tree.
- **Free-list reuse path.** `AllocNode`'s `_freeHead != Nil` branch is
  only hit after a `RemoveAt`/`RemoveRange`, and only if a later
  `InsertAt` follows. The randomized property test probably exercises
  this, but there is no direct assertion that a freed node gets reused
  (and therefore node capacity doesn't grow unbounded). A deterministic
  loop — `for (var i = 0; i < 1000; i++) { tree.InsertAt(0, 1); tree.RemoveAt(0); }`
  — should not grow `_val.Length` indefinitely. Without a way to
  introspect capacity this is hard to assert; consider exposing
  `DebugCapacity` as `internal`.
- **`Rebuild` from non-empty to empty** — resets `_root`, `_count`,
  `_allocated`, `_freeHead`. Exists in source (lines 192-199) but no
  explicit test.
- **`Update` OOR** (delta pushes prefix negative, or index out of range
  → `throw new ArgumentOutOfRangeException`).
- **`ValueAt` OOR.**
- **Treap degeneration under adversarial priorities.** `Random.Shared`
  gives good average case, but the recursive `Split`/`Merge` are not
  `MethodImpl` guarded against deep stacks. For a 100 000-element tree
  with bad luck the depth is ~34 on average — safe — but there is no
  upper bound assertion. Low priority.

## Architectural concerns

- **`BulkBuild` and `BuildBalanced` are ~95% the same code.** Both build
  a Cartesian tree from a span; they differ only in that `BulkBuild`
  inlines allocation (because it runs during `Rebuild` before `_count`
  is meaningful) while `BuildBalanced` calls `AllocNode` (which bumps
  `_count` and wires the free list). They could be merged with a small
  refactor: have `BulkBuild` also call `AllocNode`, and have `Rebuild`
  reset `_count` before calling it. Risk of introducing a bug against
  the randomized test is low.
- **Struct-of-arrays vs single struct[]:** seven parallel `int`/`long`
  arrays (`_val`, `_sum`, `_max`, `_sz`, `_pri`, `_left`, `_right`).
  SoA is almost certainly faster for cache, but it doubles the blast
  radius of any allocation bug. Leave as-is; worth a comment at the
  top explaining the choice.
- **`UpdateNode` is recursive.** Depth O(log n) in expectation; an
  iterative version is straightforward but not worth the code churn
  unless profiling shows a hit.
- **`_count` is maintained in two places** — `AllocNode` and `BulkBuild`
  both do `_count++`. Easy to drift. If the two allocators are merged
  per the point above, this goes away.

## Simplification opportunities

- **Pull the Cartesian-tree stack into a helper** so the duplication in
  `BulkBuild`/`BuildBalanced` is obvious. Even if they can't fully
  merge, the stack-driven loop could be a single method.
- **`FreeSubtree`'s iterative stack.** Fine as-is, but `_freeHead` linked
  through `_left` (line 310) is clever and fragile. Worth a comment at
  `AllocNode` explaining that the `_left[id] = Nil` at line 303 is what
  erases the free-list pointer; without it the first query would crash.
- The `Nil = -1` constant could be `private const int Nil = -1;`
  (already is). Fine.

## Bugs / hazards

- **No obvious bugs in the source**, but the complexity density is high
  enough that it really deserves a property test that also verifies
  `MaxValue()` on every op, not just `PrefixSum`. Adding that one assertion
  inside the existing `RandomOperations_ConsistentWithListReference` loop
  would cover most of the max-tracking gap in one change.
