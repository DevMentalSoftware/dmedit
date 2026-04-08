# `Block`

`src/DMEdit.Core/Blocks/Block.cs` (397 lines)
Tests: `BlockTests.cs` (~55 tests — good coverage).

Single block in the future WYSIWYG editor model. Pristine vs dirty
state, inline span management, split/merge. **Not wired into the
production editor yet** (see journal key-deferred items).

## Likely untested

- **`ApplySpan` with a zero-length existing span.** Edge case — a
  `merged` result that overlaps only at a single point.
- **`RemoveSpan` on a range that doesn't intersect any span.** Early
  return is implied but not asserted (`affected.Count == 0 →
  no Changed event`).
- **`RemoveSpan` with a link span** — the `Url` on the trimmed
  left/right remnants is preserved. Tested for split?
- **`InsertText` at `offset == Length`** — boundary of the
  `ThrowIfGreaterThan`. Tested as `InsertText_AtEnd`.
- **`DeleteText` with `offset + length == Length`** — boundary.
  Tested as `DeleteText_FromEnd`.
- **`SetText(newText)` on a CodeBlock that contains `\r`** — the
  `AllowsNewlines` check only strips `\n`/`\r` on non-code blocks.
  A CodeBlock keeps the `\r`. Tested?
- **`MergeFrom` when `other` is empty** — concatenation of `Text + ""`
  works, no spans shifted. Not asserted.
- **`WriteTo` on a dirty block** — uses `_materializedText`. On a
  pristine block — uses `_textMemory.Span`. Both tested.
- **`HasSpanAt(type, offset)` at `offset == span.End`** — returns
  false (half-open range). Not explicitly asserted.

## Architectural concerns

- **Span storage is a `List<InlineSpan>`**. Every `ApplySpan` /
  `RemoveSpan` is O(spans) — fine for the hand-curated inline
  formatting scale (dozens per block), too slow for a document
  with millions of spans. Not a current concern.
- **`AdjustSpansForDelete` has 5 branches** (line 291-316). The
  partially-overlapping-start branch (`s.Start < offset`) trims
  end; the start-overlapping-from-the-left branch (`s.Start >=
  offset`) trims start. This is subtle and hard to read. A
  tabular test for each of 6 possible relative positions
  (before, touching-before, overlap-start, inside, contains,
  after) would make maintenance easier.
- **`SplitAt` creates a pristine right-half** even if the left
  was already dirty. The right block's `_isDirty` is the default
  `false` from construction. That means a dirty block can split
  into a dirty left + pristine right. Surprising invariant.
  Check whether any tests assert the right's pristine state.
- **`MergeFrom` promotes via `SetTextInternal(Text + other.Text)`**
  — always allocates, even if the left was pristine. Comment says
  so. If this becomes a hot path, worth revisiting.
- **`Text` getter caches the materialized string** but the
  comment says "Reading `Text` caches a string but does not make
  the block dirty" (line 113). So `IsPristine` can be true while
  `_materializedText != null`. Verified.

## Simplification opportunities

- **`AdjustSpansForInsert`** and `AdjustSpansForDelete` could
  share a single state-machine helper, but the branching is
  different enough that the refactor is marginal.
- **`ApplySpan` LINQ usage** (`_spans.Where(…).ToList()`) is mild
  allocation pressure on a hot-ish path. Could inline the loop.
  Low priority — this class isn't on the hot path anyway.
