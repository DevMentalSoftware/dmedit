# `Commands`

`src/DMEdit.App/Commands/Commands.cs` (508 lines)
Tests: `CommandRegistryTests.cs` (basic existence + DefineMenus).

Central registry of all ~103 application commands. Hand-authored
static readonly fields plus `DefineMenus` that assigns each to a
menu/submenu and builds the `All` list.

## Likely untested

- **`All` list completeness.** A test that iterates `All` and
  asserts every field of `Commands` is present would catch
  future additions that forget to register in `DefineMenus`.
- **Unique `Id` invariant.** No test asserts all command IDs
  are unique. A reasonable invariant: `Commands.All.Select(c
  => c.Id).Distinct().Count() == Commands.All.Count`.
- **Every menu command has a non-empty `MenuDisplayName`.**
  Safety net.
- **Commands with `DefaultInToolbar = true` all have a
  non-null `ToolbarGlyph`.** Otherwise they show as blank
  toolbar buttons.
- **Commands that claim to be toolbar toggles
  (`IsToolbarToggle`) have a `CanExecute` pair** that reports
  current state. Harder to check automatically.
- **Each command is defined in exactly one category** matching
  its menu. Drift check: `FileSave.Menu == CommandMenu.File`
  etc.

## Architectural concerns

- **500+ lines of static field declarations** is error-prone.
  A YAML/JSON resource file driving `Commands.All` would let
  the registry be data rather than code. Would also let
  profiles define new commands without recompiling. Bigger
  refactor.
- **`DefineMenus` is called once at startup** from somewhere
  (Program? MainWindow?). No test asserts it's called before
  `KeyBindingService` constructs (which enumerates `All`).
  Order of operations matters.
- **103 commands, no grouping** beyond the `Category` string.
  Some groups (scroll, caret navigation) could be subclasses
  or tagged with a "group" enum for better discoverability in
  the command palette.
- **Command `Id`s are stable strings** used in settings and
  profile JSON. Renaming one is a breaking change. Worth a
  "deprecated aliases" map for migration.

## Bugs / hazards

- **Silent bugs in `DefineMenus`** that assign the wrong
  `CommandMenu` value wouldn't be caught by tests. Worth a
  smoke test asserting "all commands in `File` submenu have
  `Category == "File"`" (or similar).
