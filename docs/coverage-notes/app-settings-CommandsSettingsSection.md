# `CommandsSettingsSection`

`src/DMEdit.App/Settings/CommandsSettingsSection.cs` (1 116 lines)

The Commands tab of the Settings page. Custom-drawn list of
every command with its gesture(s), profile selector,
menu/toolbar inclusion checkboxes, conflict detection UI,
reset buttons.

## Likely untested

- **Whole custom control** — no tests.
- **Profile dropdown population** — calls `ProfileLoader`
  for each profile.
- **Gesture edit modal** — user clicks a gesture cell and
  a capture dialog opens. Input capture, chord support.
- **Conflict detection** — when user binds to a gesture
  already owned by another command, show a warning.
- **Reset single binding** — returns to profile default.
- **Reset all bindings** — for all commands.
- **Menu/Toolbar override checkboxes** — bind to the
  Dictionaries on AppSettings.
- **Search/filter commands** — typing in a filter box.
- **Advanced filter** — show/hide advanced commands.
- **Scroll state preservation** across rebuilds.

## Architectural concerns

- **1 116 lines for a single settings tab** is a ton. This
  is comparable in density to `EditorControl`. Similar
  partial-split treatment applies.
- **Hand-built DataTemplate-like rendering** instead of
  Avalonia ItemsControl. Probably for perf (~100 rows each
  with 3 gesture cells). Worth a comment.
- **Command rows are re-created on profile change** — O(N)
  per switch. Acceptable.
- **Conflict detection is O(N²) naive?** — probably indexed
  via `KeyBindingService.FindConflict`.

## Recommendation

**Review this file carefully** — it's large and its UI
correctness is load-bearing (users depend on key bindings
being stable). Worth its own dedicated testing effort.
