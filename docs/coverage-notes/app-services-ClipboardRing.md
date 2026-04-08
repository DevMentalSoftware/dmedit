# `ClipboardRing`

`src/DMEdit.App/Services/ClipboardRing.cs` (47 lines)

Fixed-capacity LRU list of clipboard strings.

## Likely untested

- **`Push` moves existing entry to front** — tested?
- **`Push` trims to `MaxSize`** — tested?
- **`Push` rejects entries larger than `MaxEntryChars` (500)**
  — silent drop. Untested.
- **`Push` rejects null/empty** — early return. Untested.
- **`MaxSize` decreased at runtime** — doesn't trim the list.
  Would leave extra entries until next `Push`. Minor.
- **`Get(index)` out of range** — returns null.

## Architectural concerns

- **`MaxEntryChars = 500` silently drops large entries.** A
  user who copies a 600-char paragraph expects it in the
  ring but it vanishes. No visible indication. Either log or
  show a status-bar message.
- **O(n) `_entries.Remove(text)` on push.** Fine at size 10.
