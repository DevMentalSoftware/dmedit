# `TabState`

`src/DMEdit.App/TabState.cs` (152 lines)

Per-tab state for the multi-document interface. Holds the
document, path, dirty flag, SHA-1 baseline, load state,
scroll state, conflict record, and CharWrap mode flag.

## Likely untested

- **`CreateUntitled` numbering** with `[Untitled, Untitled 2,
  Untitled 3]` and a gap — picks the lowest free number.
  Tested?
- **`CreateUntitled` with names that start with "Untitled X"
  but are followed by letters** — e.g. "Untitled abc". The
  `int.TryParse` handles it.
- **`FinishLoading` fires `LoadCompleted` exactly once** —
  idempotency check missing. Calling it twice would fire
  twice.
- **`IsLocked` vs `IsReadOnly` distinction** — locked can't
  be toggled. Tested?
- **SHA-1 / last-write / file size triple** — consistency on
  load/save.

## Architectural concerns

- **19 mutable public properties** — grows as features
  arrive. A sub-struct per concern (LoadState,
  ReloadState, ScrollState, ConflictState) would help
  legibility.
- **`LoadCompleted` event** — no unsubscribe point. If a
  handler captures the tab's UI state and the tab is
  closed mid-load, the handler still fires. Low risk,
  but worth a nullable reset in Dispose (there is no
  Dispose).
- **`TabState` owns `Document` but doesn't dispose its
  `Buffer`** — PieceTable doesn't own the buffer either.
  Who closes the file? Presumably MainWindow on tab close.
  Worth a comment.
