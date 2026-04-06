# 17 — Editing Polish

**Date:** 2026-04-06
**Status:** Implemented

---

## Problem

Several small editing behaviors didn't match the conventions established by
VS Code, Visual Studio, and Notepad++.  Individually each was minor, but
together they made indented editing feel subtly "off."

## Changes

### Auto-indent on Enter

Pressing Enter now copies the leading whitespace from the current line onto the
new line.  The literal whitespace characters are preserved (tabs stay tabs,
spaces stay spaces) so the behavior is correct regardless of indent style.

Applies to all three newline-creating commands:
- **EditNewline** (Enter) — new line inherits current line's indent.
- **InsertLineBelow** — blank line below inherits indent.
- **InsertLineAbove** — blank line above inherits indent.

Helper: `GetLeadingWhitespace(string)` extracts the leading whitespace
substring, built on the existing `LeadingWhitespaceLength`.

### Smart deindent on Backspace

When the caret is inside leading whitespace on a spaces-indent document,
Backspace deletes back to the previous indent stop (e.g., column 7 to 4 with
indent width 4) instead of deleting a single space.

Conditions: selection must be empty, caret must be in leading whitespace, all
characters before the caret must be spaces (mixed tabs fall through to normal
backspace), and the document's dominant indent style must be Spaces.  Tabs
already represent one indent level per character, so they don't need this.

Helper: `TrySmartDeindent(Document)` returns true if it handled the delete.

### Smart Home

Home key now toggles between the first non-whitespace character and column 0.
First press goes to first non-whitespace; if already there, goes to column 0.
Works with Shift+Home for selection extension.

Implemented in `MoveCaretToLineEdge` — the `toStart` branch now computes the
first non-whitespace offset and toggles against it.

### Trailing whitespace cleanup on Enter

When pressing Enter, any trailing whitespace between the last non-whitespace
character and the caret is stripped from the current line.  This covers both
the general case (`"    code   |"` becomes `"    code"`) and the whitespace-only
line case (`"    |"` becomes empty).

### Other changes (manual)

- **LICENSE** — Updated copyright to 2026 DevMental Software LLC.
- **AboutDialog** — Disabled minimize button (`CanMinimize = false`).
- **Removed `docs/release-notes.md`** — Redundant with GitHub Releases.
  Changelogs are now maintained on
  [GitHub Releases](https://github.com/DevMentalSoftware/dmedit/releases)
  going forward; the design journal remains the detailed internal record.
