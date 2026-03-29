# DevMentalMD — Claude Session Context

## First steps for every session

1. Read `docs/design-journal.md` — index of all design decisions and architectural
   direction. "In progress" section describes current work; "Recently completed" covers
   the last few milestones. Detail files are in `docs/design-journal/`.
2. Read `docs/csharp-style.md` — K&R braces, naming conventions, modern C# features.
3. Read `docs/project-conventions.md` — abbreviations, reserved names, markdown preferences.

## Project structure

```
src/
  DevMentalMd.Core/        — Document model, PieceTable, IBuffer, blocks, FenwickTree, IO
  DevMentalMd.Rendering/   — TextLayoutEngine (Avalonia TextLayout wrapper)
  DevMentalMd.App/         — Avalonia desktop app: EditorControl, DualZoneScrollBar, MainWindow
  DevMentalMd.Print.Windows/ — Windows printing support
tests/
  DevMentalMd.Core.Tests/
  DevMentalMd.Rendering.Tests/
  DevMentalMd.App.Tests/
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

All tests must pass before and after any change. Current baseline: **534 tests**
(439 Core + 31 Rendering + 64 App).

## Dev mode

Debug builds enable DevMode automatically. In Release builds, set
`DevModeEnabled: true` in `%APPDATA%/DevMentalMD/settings.json`.
DevMode shows: Dev menu, procedural sample documents in Recent menu, performance stats bar.
The