# `DeleteEdit`

`src/DMEdit.Core/Document/History/DeleteEdit.cs`
Tests: indirectly via `PieceTableTests` Insert/Undo/Redo and
`EditHistorySerializationTests`.

95-line delete edit with four(!) constructors: full (pieces +
line info), pieces-only (no line info), small (text only), and
oversized-session (explicit length + possibly empty text).

## Likely untested

- **Four constructor overloads, only two are routinely exercised
  by tests** (full + pieces-only). The small-delete and
  session-restore constructors need targeted tests.
- **`Apply` path where `_pieces.Length == 0 && _text is { Length: 0 }`**
  (the "capture pieces on first apply" branch at line 72). This is
  the oversized-delete recovery path: session deserialized a
  delete with no pieces and no text (to save disk), Apply captures
  them at the moment the table is in the pre-delete state. Critical
  correctness path with no direct test.
  - Also: if `CaptureLineInfo` returns `null` (single-line delete),
    `_lineInfoStart` stays at `-1` from the ctor and `_lineInfoLengths`
    stays null. On Revert, the pieces-only path is used. Works but
    subtle.
- **`Revert` when `_pieces.Length == 0`** — uses `table.Insert(Ofs,
  _text!)`. The `_text!` null-forgiving is load-bearing: if a
  deserialized DeleteEdit has neither pieces nor text, this
  NullReferenceException's at Revert. No guard.
- **`MaterializeText` on a DeleteEdit that was constructed with
  pieces only** — it lazily reads through `table.ReadPieces`, then
  caches in `_text`. If the pieces reference a paged buffer
  that's since been evicted, `ReadPieces` will do I/O. Works, but
  a long-deferred materialization on an async session save could
  block on disk.

## Architectural concerns

- **Four constructors are hard to remember.** Consider a factory
  style: `DeleteEdit.WithPieces(ofs, len, pieces, lineInfo?)`,
  `DeleteEdit.WithText(ofs, text)`, `DeleteEdit.FromSession(ofs,
  len, text)`. Same number of lines but clearer intent.
- **`_pieces` is mutable** (reassigned in `Apply`'s recovery path).
  Marking it `private Piece[]` (as it is) is fine, but the mutation
  from `Apply` is surprising — a reader expects `Apply` to read
  the edit's state, not patch it. Worth a comment at line 72.
- **`_lineInfoStart = -1` as "no line info" sentinel.** `null` is
  clearer; `_lineInfoLengths == null` already serves that role.
  `_lineInfoStart` could be `int?`.
- **`DeletedText` getter is nullable** (`string?`), while
  `MaterializeText` returns non-null. Call sites need to pick.
  Could merge into a single `GetText(PieceTable)` that always
  returns non-null.

## Bugs / hazards

- **`Revert` with a null `_text` and empty pieces** throws NRE
  (null-forgiving `_text!`). Can only happen if the constructor
  contract is violated. Worth a `Debug.Assert` or an explicit
  guard that throws a clearer exception.
- **`Apply` re-captures on null text** — the `(_text is null or
  { Length: 0 })` predicate is the recovery trigger. But a real
  empty-string delete (zero-length range) would also satisfy it.
  The `Len > 0` guard prevents that. Good.
