# `RecentFilesStore`

`src/DMEdit.App/Services/RecentFilesStore.cs` (118 lines)

Persists the list of recently opened file paths to disk.

## Likely untested

- **Push/trim to max size.**
- **Deduplication** — same path pushed twice.
- **Path case differences** on Windows (`C:\foo` vs `c:\foo`).
- **Non-existent files** — when the user opens a recent file
  that's since been deleted. Should the store auto-remove?
- **UNC paths, forward-slash paths, relative paths.**
- **File locking during concurrent saves.**
- **Corrupted persistence file** — recover gracefully.

## Architectural concerns

- **Separate persistence file from AppSettings?** — likely,
  to avoid loading the full recent list eagerly. Check.
- **No file existence check** — returning stale entries
  that point to deleted files is a UX wart.
- **`DevMode` procedural samples** are added to the recent
  list per the journal. Special markers?
