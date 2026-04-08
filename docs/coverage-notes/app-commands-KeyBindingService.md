# `KeyBindingService`

`src/DMEdit.App/Commands/KeyBindingService.cs` (357 lines)
Tests: `tests/DMEdit.App.Tests/KeyBindingServiceTests.cs`
(~40 tests). Strong coverage.

Maps keyboard gestures to command IDs. Loads profile + overlays
user overrides. Supports primary/secondary slots, single keys,
and two-keystroke chords. Dispatch fast path via two lookup
dictionaries.

## Likely untested

- **Menu access-key reservation (`Pass 0`)** — tests cover
  individual bindings but not the "Alt+F is always Menu.File"
  invariant. If a user profile tries to bind Alt+F to something
  else, the override should either win or be rejected. Unclear
  from the source which happens.
- **Override conflicting with a profile default** — the pass-2
  user override should replace the profile binding. Tested for
  single key; tested for chord?
- **`SetProfile` does NOT clear user overrides** (per the
  source comment). Tested?
- **`Rebuild` called while dispatch is in flight** — no locking.
  Rebuild happens on settings changes, which should be UI-thread.
  Worth a comment.
- **`Chord prefix` collision with a single-key binding** — e.g.
  if `Ctrl+K` is a chord prefix but also bound directly. The
  dispatch code has to disambiguate. Source has some logic for
  this but the exact semantics aren't pinned by a test.
- **Empty override string (`""`)** unbinds — tested
  (`EmptyOverrideStringUnbinds`).
- **Invalid override string** — doesn't bind at all, or logs
  and skips? Not pinned.
- **`FindConflict` across slots** — covered
  (`ConflictDetectedAcrossSlots`).
- **`ResolveChord` when first key matches a chord prefix but
  second key doesn't** — tested
  (`ResolveChordReturnsNullForWrongSecondKey`).

## Architectural concerns

- **Six dictionaries of state** (`_gestureToCommand`,
  `_commandToGesture`, `_commandToGesture2`,
  `_chordPrefixes`, `_chordToCommand`, and the profile data).
  All rebuilt atomically in `Rebuild`. Correct but dense.
- **`ChordTupleComparer`** — internal custom comparer for
  `(KeyGesture, KeyGesture)` lookups. Worth a test of its
  equality rules.
- **`KeyBindingService` depends on `AppSettings`** for overrides
  and on `ProfileLoader` (static) for profiles. A test seam
  would let binding tests not touch disk.
- **`ActiveProfile` is persisted as null for "Default"** — a
  subtle serialization choice; the save on-disk doesn't carry
  a "Default" string. Worth a comment.
- **Gesture text formatting** lives on the service
  (`GetGestureText`), coupled to Avalonia's KeyGesture. Could
  move to a helper.
