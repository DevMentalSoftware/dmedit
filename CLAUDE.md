# DMEdit — Claude Session Context

Important! Never chain Bash commands together with &&, because this essentially bypasses the settings rules we've configured. 

## First steps for every session

1. Read `docs/design-journal.md` — index of all design decisions and architectural
   direction. "In progress" section describes current work; "Recently completed" covers
   the last few milestones. Detail files are in `docs/design-journal/`.
2. Read `docs/csharp-style.md` — K&R braces, naming conventions, modern C# features.
3. Read `docs/project-conventions.md` — abbreviations, reserved names, markdown preferences.

## Project structure

```
src/
  DMEdit.Core/             — Document model, PieceTable, IBuffer, LineIndexTree, blocks, IO
  DMEdit.Rendering/        — TextLayoutEngine (Avalonia TextLayout wrapper)
  DMEdit.App/              — Avalonia desktop app: EditorControl, DualZoneScrollBar, MainWindow
  DMEdit.Windows/          — Windows-specific features (printing, native clipboard)
tests/
  DMEdit.Core.Tests/
  DMEdit.Rendering.Tests/
  DMEdit.App.Tests/
docs/
  design-journal.md        — append-only design history (READ THIS FIRST)
  csharp-style.md          — coding style guide
  project-conventions.md   — naming, abbreviations, reserved names
```

## Build and test

```
dotnet build
dotnet test
```

All tests must pass before and after any change. Current baseline: **11,108 tests**
(1269 Core + 60 Rendering + 9779 App).

## Dev mode

Debug builds enable DevMode automatically. In Release builds, set
`DevModeEnabled: true` in `%APPDATA%/DMEdit/settings.json`.
DevMode shows: Dev menu, procedural sample documents in Recent menu, performance stats bar.
The