# `Document`

`src/DMEdit.Core/Document/Document.cs` (1 278 lines)
Tests: `DocumentTests.cs` (~50 tests), plus integration through most
other test files.

Wraps a `PieceTable` with undo/redo history, selection, column
selection, line-ending detection, encoding, indent info, print
settings, and a bag of editing commands. The "god class" of the
editor core — a lot of orthogonal features share mutable state here.

## Likely untested

### Core edit path
- **`Insert(text)` with `text = ""`** — guarded at line 169, no-op.
  Not explicitly asserted.
- **`Insert(text)` with a selection that contains a surrogate pair
  that gets deleted as part of the replace.** The `DeleteRange` call
  uses `CapturePieces` directly, without snapping the selection to
  boundaries first. If `Selection.Start` lands mid-pair, the
  `DeleteRange` will fail in the line-tree-splice math. Source
  relies on the UI never producing such a selection; no assertion
  pins this.
- **`DeleteBackward` at `ofs == 1` with a surrogate pair at position
  0..1** — the `StepLeft` correctly returns 0, so this is fine.
  Tested (`DeleteBackward_AtSurrogatePair_RemovesBothHalves`).
- **`DeleteForward` at `ofs == Length - 2` with a pair at the end**
  — tested.

### Column selection
- **`InsertAtCursors` with text containing a newline.** The source
  just calls `PushInsert` per line with the raw text; a newline in
  the text would create an extra physical line per cursor. Behavior
  is probably surprising; worth pinning with a test (or forbidding
  newlines in `InsertAtCursors`).
- **`InsertAtCursors` when one of the lines needs padding and
  another doesn't.** The padding changes the target offset for
  subsequent operations. Tested? Probably, via `ColumnSelectionTests`.
- **`DeleteBackwardAtCursors` when `caret == 0` on one of the lines**
  (shorter line at the top). The `continue` at line 337 skips only
  that cursor. Worth a test asserting the other cursors still
  delete.
- **`DeleteForwardAtCursors` at end-of-document.**
- **`PasteAtCursors` with `lines.Length != colSel.LineCount`** —
  silent no-op at line 471. Worth asserting the no-op (or
  throwing) so callers can't accidentally lose data.
- **`PasteAtCursors` where one of the `lines[]` contains a
  newline.** Again, probably surprising behavior.
- **`GetColumnSelectedText` with CR-only line endings** — the
  `switch` at line 449 handles `CRLF`, `CR`, and default `LF`. All
  three branches should be exercised.

### Line ops
- **`MoveLineUp`/`MoveLineDown` with a selection at the beginning or
  end that extends into an adjacent line** — `GetSelectedLineRange`
  has the subtle "if selection ends at column 0 of a line, that line
  is excluded" rule at line 1190. Tested? Needs verification.
- **`MoveLineDown` when the adjacent line has no trailing newline**
  (last line of doc). The `selHasNl` / `adjHasNl` mismatch logic at
  lines 1221-1232 handles the swap. Tested by
  `MoveLineDown_FirstLine_HandlesNoTrailingNewline`.
- **`DeleteLine` with `caret` at column 0 of the line after a
  blank line** — should still delete the caret's line, not the
  previous blank.
- **`DeleteLine` with `caret` at position 0 of a single-line
  document** — tested by `DeleteLine_OnlyLine_EmptiesDocument`.
- **`DeleteLine` on the last line when `lineIdx == 0`** (empty doc
  or one-liner) — the `deleteStart > 0` guard at line 540 correctly
  avoids the "eat preceding newline" branch.

### Word / selection expansion
- **`SelectWord`** window is bounded to 1 024 chars around the
  selection (line 611). A selection at the very start of a long
  line, expanding left, cannot cross `winStart`. Source
  acknowledges this via the clamp. Worth a test that constructs a
  selection 1 025 chars into a word and verifies the word actually
  expands to the left boundary of the word (the word will be
  truncated at `winStart`, which is a silent error — the selection
  won't include the full word). This is a real correctness issue
  for very long lines.
- **`ExpandSelection(SubwordFirst)` on an identifier with a
  surrogate pair mid-word** — tested
  (`ExpandSelection_SubwordFirst_NeverStopsMidPair`).
- **`ExpandSelection(Word)`** (plain mode) has no dedicated test
  that calls it with `ExpandSelectionMode.Word`. All
  `ExpandSelection_*` tests use the default argument or
  `SubwordFirst`. Add at least one that uses `.Word`.
- **`SelectLine`** — tested implicitly by `DocumentTests.DeleteLine*`
  and `SelectWord_*`. Add a direct test.

### Case transform
- **`TransformCase(CaseTransform.Proper)` on text with mixed
  digits and letters.** `ToProperCase` treats digits as "continue
  word, lowercase" — the first letter of a digit-starting word stays
  lowercase. May be a bug; definitely untested.
- **`TransformCase` on a selection exceeding `PieceTable.MaxGetTextLength`**
  — the `sb.Append` loop at line 871 uses `ForEachPiece`, which is
  unbounded. The selection size itself is only bounded by `MaxCopyLength`
  indirectly via the UI; no direct guard here. A 10 MB selection
  would try to allocate a 10 MB `StringBuilder`. Worth a guard.

### Bulk replace / indentation
- **`ConvertIndentation` with an empty document** — no matches, no
  replacement, sets `IndentInfo` and returns. Untested.
- **`ConvertIndentation` with a document that is entirely whitespace**
  — the trailing `if (atLineStart) FlushLeading();` handles it.
  Untested.
- **`ConvertIndentation` from tabs to spaces where a line has
  mid-line tabs** — only leading whitespace is touched. Tested?
  Needs verification.
- **`BulkReplaceUniform`/`BulkReplaceVarying` with `matches.Length
  == 0`** — early return. Tested.

### Undo / redo
- **`Undo` on an empty history** — returns `null`. Tested.
- **`Redo` on an empty history** — returns `null`. Tested.
- **`CaretAfterRedo` branches** — all tested indirectly via
  `UndoThenRedo`, but the `VaryingBulkReplaceEdit`/`UniformBulkReplaceEdit`
  branches return `0L` (a somewhat arbitrary choice). This means
  redoing a bulk replace drops the caret to document start, which
  is UX-surprising. Worth a note.

### Events
- **`SuppressChangedEvents` nesting** — tested?  A nested scope
  should only fire `Changed` once when the outermost disposes.
- **`SuppressChangedEvents` when no mutation happens inside** — the
  outer dispose still fires `Changed`. Arguably wrong (shouldn't
  fire when nothing changed). Low priority.

### SanitizeSurrogates
- **Well-tested.** Lone high, lone low, trailing lone high, interior
  lone — all pinned. Good.
- **Very long string with all valid pairs** — confirms the fast-scan
  path returns the original instance without allocation. The source
  code does this, tests don't assert the no-alloc contract (can't in
  a unit test without reflection).

## Architectural concerns

### God-class sprawl
- **1 278 lines, ~60 public methods.** Document owns: edit commands,
  column-mode commands, line operations, word/selection expansion,
  case transform, bulk replace, line-ending conversion, indentation
  conversion, undo/redo, event subscription, print settings, encoding
  info, indent info. Each concern averages ~20 lines. A reasonable
  split:
  - `Document` retains: fields, selection, history, simple Insert/
    Delete primitives, events.
  - `DocumentEditCommands` (extension methods or partial): word/line/
    case/column commands, expand-selection, transform.
  - `DocumentFormatting` (extension methods or partial):
    `ConvertIndentation`, `ConvertLineEndings`, `GetColumnSelectedText`.
  - Bulk replace already has separate edit types; the Document-level
    wrappers are thin and can stay.
  This split is cosmetic; no behavior change. Makes the file less
  daunting to navigate.
- **Several "helper" privates are really their own domains:**
  `SwapLines` (60 lines), `ToProperCase` (16 lines), `IsSubwordBoundary`
  (45 lines), `IsWordRune` (10 lines). The latter two are pure text
  classification helpers; they'd live more naturally in a
  `WordClassifier` static class.

### `PushInsert` vs `_table.Insert`
- `PushInsert` wraps `AppendToAddBuffer` + `SpanInsertEdit` + history
  push. Called from 8 sites. The more direct `_table.Insert` is
  called from 0 sites — it was originally the entry point but has
  been superseded. Worth a comment at `PieceTable.Insert` explaining
  "prefer PushInsert in Document; this is retained for direct tests."

### `RecordBackgroundPaste`
- 20-line method that reconstructs a Compound edit from already-applied
  pieces. Complex because it has to handle both the "replace selection"
  and "bare insert" cases. Used by Linux clipboard background paste.
  Single caller. The tuple decompose `deleteLineInfo is var (sl, ll)`
  is tricky. Worth a targeted test (currently exercised only via
  `LinuxClipboardService` integration which has no direct tests).

### `GetSelectedText` return type
- **Returns `string?` where `null` means "selection too big."** The
  caller has to distinguish null from empty-string. This is a real
  API wart — the caller writes `if (selectedText is null) { error }
  else if (selectedText == "") { do nothing } else { use }` at every
  call site. Consider a `CopyResult` enum `{ Empty, TooLarge,
  Text(string) }` or throwing on too-large.

### Line-ending state
- **`LineEndingInfo` is set by the loader, mutated by
  `ConvertLineEndings`, and read at save time.** No event fires when
  it changes, even though the UI status bar shows it. The status bar
  re-reads on every `Changed` event, which works because
  `ConvertLineEndings` doesn't raise `Changed`. Subtle.
- **`ConvertLineEndings` is a no-op at the document level** (it only
  sets `LineEndingInfo`; actual rewriting happens at save). This is
  non-obvious from the method name. Consider renaming to
  `SetLineEndingStyle` or adding a big comment.

## Simplification opportunities

- **`GetLineContentRange` has a subtle bug risk:** `Math.Max(lineStart,
  lineEnd - 2)` + `GetText(..., Math.Min(2, lineEnd - lineStart))` is
  correct but hard to read. A helper `(long lineStart, long lineEnd)
  GetPhysicalLineBounds(ofs)` that does the `\r\n` strip without the
  inline math would be clearer.
- **`ToProperCase` is 16 lines of manual char-by-char state machine.**
  Could use `CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant())`,
  but that has different semantics for "McDonald"-style names — the
  current behavior may be deliberately simpler. Keep, but comment why.
- **`IsWordChar` (private, BMP-only) and `IsWordRune` (private,
  rune-aware) both exist.** The former is used by `SelectWord`
  (since replaced by `IsWordRune`) — check if `IsWordChar` is dead
  code.

## Bugs / hazards (prioritized)

1. **`InsertAtCursors` with a multi-line `text`** — undefined
   behavior. Either forbid it or document that each cursor gets
   the full text verbatim. Probably worth a guard.
2. **`PasteAtCursors` with `lines.Length != colSel.LineCount`** —
   silent no-op. Callers should be told to enforce the length at
   the UI layer. Worth an `ArgumentException`.
3. **`SelectWord` with a selection that starts > 1 024 chars into
   a long line** — the window clamp silently truncates the word
   at `winStart`, so the selection is wrong. Fix: expand the
   window dynamically or scan the buffer directly via
   `ForEachPiece`.
4. **`TransformCase` on very large selections** — allocates a
   `StringBuilder` sized to the full selection. Could OOM.
5. **`CaretAfterRedo` of a bulk replace drops the caret to 0.**
   UX issue; low priority.
6. **`Document` mutable public properties** (`Selection`,
   `ColumnSel`, `LineEndingInfo`, `IndentInfo`, `EncodingInfo`,
   `PrintSettings`) have no event wiring. Writing to them doesn't
   notify the UI. Works because the UI re-reads on `Changed`, but
   `Changed` only fires from inside the edit commands. Setting
   `Selection` manually from outside will not fire `Changed`.
   Probably intended, but worth a comment at each setter.
