# `AboutDialog`

`src/DMEdit.App/AboutDialog.cs` (129 lines)

Modal About dialog with version, credits, link to GitHub.

## Likely untested
- Simple dialog, no tests.
- **Version read from assembly** — fallback when null.
- **Link click** opens browser.

## Architectural concerns
- **Hardcoded URL and credit strings** — any future rebranding
  requires code change.
