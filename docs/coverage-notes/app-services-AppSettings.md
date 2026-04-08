# `AppSettings`

`src/DMEdit.App/Services/AppSettings.cs` (544 lines)
Tests: none direct.

JSON-serialized settings persisted to `%APPDATA%/DMEdit/settings.json`.
~70 public properties, `Load`/`Save`/`ScheduleSave`,
`PushRecentFindTerm`/`PushRecentReplaceTerm` helpers.

## Likely untested

- **`Load` when file doesn't exist** → new AppSettings. Trivial.
- **`Load` when file is corrupted** → swallow, return new.
  Untested and silent — no warning logged. User loses settings
  with no indication.
- **`Load` when DevMode is persisted but `DevModeAllowed` is
  false** → force off (line 488). Good gate; untested.
- **`Save` with no write permission on AppData** → silently
  swallowed. Same concern as Load — no user notice.
- **`ScheduleSave` debounce** — 500ms window. A rapid burst of
  settings changes should coalesce to one save. Untested.
- **`ScheduleSave` timer reuse** — second call changes the
  existing timer's next fire. Thread-safety? `Timer.Change` is
  safe; no explicit lock.
- **`PushRecentFindTerm` / `PushRecentReplaceTerm`** dedup and
  size cap logic. Per-list history.
- **JSON deserialization silently ignores unknown keys** —
  good for forward compat. Untested.
- **Setting a property then calling `Save()` without
  `ScheduleSave()`** — works synchronously.
- **Menu/toolbar override dictionaries** — nullability and
  default fallback semantics. Untested.
- **Window-bounds restore** when `WindowLeft` points to a
  monitor no longer present — should fall back to default.
  No guard visible.

## Architectural concerns

- **544 lines of mostly property declarations** — god-class for
  settings. Could be split into logical sub-settings classes:
  `DisplaySettings`, `EditingSettings`, `SearchSettings`,
  `WindowSettings`, `PrintSettings`, `CommandSettings`. The
  JSON shape could stay flat via `[JsonPropertyName]` if
  backwards compat matters.
- **Static Load factory, instance Save method** — inconsistent.
  A pair of statics `Load()/Save(settings)` would let the class
  be immutable. Would break `ScheduleSave`.
- **Silent exception swallowing** in Load and Save — any I/O or
  permission error just vanishes. User experience degradation
  with no diagnostic trail. At least log to CrashReport.
- **`ScheduleSave`'s `Timer`** — never disposed. Leaks for the
  lifetime of the app. Acceptable since AppSettings is a
  singleton.
- **`ActiveProfile = null` means "Default"** — see KeyBinding
  notes. Documented, but a subtle serialization choice.
- **Recent find/replace lists** are stored here; could live in
  a separate file (`recent.json`) so settings don't grow
  unboundedly.

## Simplification opportunities

- **Remove the "WhenWritingNull" setting** once all properties
  are non-nullable or the null→default semantics are
  audited. The comment at line 28-31 explains the concern
  but it's still risky.
- **Use `[JsonInclude]` + readonly fields** to prevent
  accidental external mutation after Load. Marginal gain.

## Bugs / hazards

1. **Silent Load failures** mask corruption. Log to a sibling
   `settings.backup.json` before overwriting.
2. **No version field** — future incompatible schema changes
   have no migration path.
3. **`Save` is best-effort** but `ScheduleSave` doesn't report
   failures to the user; a failed save looks like success.
   For a critical settings change (e.g. key binding that the
   user just customized), a visible failure would be better.
