# `FindBarControl`

`src/DMEdit.App/Controls/FindBarControl.axaml.cs` (289 lines)

Find/Replace bar UserControl. Wildcards, regex, match case,
whole word, direction, replace mode toggle, resize grip.
Events are stubs — actual search logic lives in `EditorControl`.

## Likely untested

- **Entire control** — no direct tests.
- **`SearchMode` setter consistency** when none are checked
  (Normal) vs both (invalid state — the setter guards).
- **`IsReplaceMode` toggle** and `ApplyReplaceMode` layout
  changes.
- **Resize drag** (`_resizeStartX`, `_resizeStartWidth`,
  `_resizing`). Coordinate space conversion bug-prone.
- **Direction tracking** (`_lastForward`) updates on left
  vs right arrow clicks.
- **Events fire with correct payloads.**
- **Focus return to SearchBox** after each action.

## Architectural concerns

- **UserControl coupling** — named template parts
  (`SearchBox`, `ReplaceBox`, various buttons). AXAML-driven
  so the names have to stay in sync.
- **Search logic is elsewhere** — this is pure UI. The
  boundary is clean.
- **`MatchCase` / `WholeWord` / `SearchMode` / `IsReplaceMode`**
  are stateful — user's preference should persist across
  sessions. Check whether they're bound to `AppSettings`.
