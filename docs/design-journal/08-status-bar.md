# 08 — Status Bar Buttons & File Handling

## Interactive status bar segments (2026-03-14)

Made four status bar segments interactive — clickable with hover highlights and popup
menus/dialogs.

### Segments

| Button | Display | Click action |
|--------|---------|--------------|
| **BtnLineCol** | `Ln X Ch Y` | Opens GoTo Line dialog (line or line:col) |
| **BtnEncoding** | `UTF-8` | Opens encoding menu (UI scaffold only — items disabled) |
| **BtnLineEnding** | `CRLF`/`LF`/`CR` | Opens line ending conversion menu |
| **BtnIndent** | `Spaces`/`Tabs` | Opens indentation conversion menu |

### Hover style

Uses same pattern as GearButton — `TabInactiveHoverBg` tint on `PointerEntered`,
`Transparent` on `PointerExited`. Each segment is a `Border` wrapping a `TextBlock`
with `Cursor="Hand"`.

### Indentation detection

Added indent detection piggy-backing on the existing newline scan loops in both
`PagedFileBuffer` and `StreamingFileBuffer`. At each line start (after a newline),
the first character is checked: space → `_spaceIndentCount++`, tab → `_tabIndentCount++`.
Exposed via `DetectedIndent` property using `IndentInfo.FromCounts(...)`.

New type `IndentInfo` (record struct) mirrors `LineEndingInfo` pattern: `FromCounts()`,
`Dominant`, `IsMixed`, `Label`.

### Indentation conversion

`Document.ConvertIndentation(IndentStyle target, int tabSize)` — replaces leading
whitespace per line in a single compound edit (undoable). Same pattern as
`ConvertLineEndings`.

### Line ending tooltip

Added count fields (`LfCount`, `CrlfCount`, `CrCount`) to `LineEndingInfo` so the
status bar can show a tooltip like "Mixed: 42 LF, 3 CRLF" when `IsMixed`.

### GoTo Line dialog

`GoToLineWindow` — lightweight popup (300×70, `BorderOnly`, centered on owner). Single
`TextBox` accepting "line" or "line:col". Uses `EditorControl.GoToPosition()` to scroll
the caret into view.

### Encoding menu (scaffold)

Shows common encodings (UTF-8, UTF-8 BOM, UTF-16 LE/BE, Windows-1252, ASCII) but all
items are disabled. Current encoding has a checkmark. UI scaffold for future work.

### File locking fix

`PagedFileBuffer` previously held a persistent `FileStream` (`_fs`) open for the
lifetime of the buffer. This caused `IOException` on save because `FileSaver` couldn't
get exclusive write access.

**Decision:** Remove persistent `_fs` entirely. `LoadPageFromDisk()` now opens a
short-lived `FileStream`, reads the page, and closes immediately. This is cleaner and
also enables future file watching — we no longer hold locks on files the user considers
"open."

`FileSaver` also changed from `FileShare.None` to `FileShare.Read`.

---

## File change detection — future work (2026-03-14)

### Problem

Files that appear "open" in the editor are not actually held open (especially after the
`_fs` removal above). External processes can modify them at any time. We need to detect
this for two scenarios:

1. **Conflict detection** — if the on-disk file changes while the user has unsaved edits,
   we need to warn about the conflict (similar to VS Code's "file has been changed on
   disk" dialog). This applies not just at session restore but continuously while the
   file is open.

2. **Tail feature** — for log files, the user may want the editor to follow appended
   content automatically (like `tail -f`). This requires detecting that the file has
   grown.

### Design considerations (not yet decided)

- `FileSystemWatcher` is the obvious .NET mechanism but has well-known reliability
  issues (missed events, duplicate events, platform differences).
- Polling on a timer is more reliable but less responsive.
- Hybrid approach: `FileSystemWatcher` for responsiveness + periodic poll as a fallback.
- Need to compare file hash (SHA-1, since we already compute it on save) to detect
  actual content changes vs. metadata-only changes.
- For tail mode, need to detect append-only growth and stream just the new bytes.
- Session persistence already stores `BaseSha1` — this is the natural comparison point
  for conflict detection.
