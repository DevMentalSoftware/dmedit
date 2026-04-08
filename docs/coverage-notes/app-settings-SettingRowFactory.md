# `SettingRowFactory`

`src/DMEdit.App/Settings/SettingRowFactory.cs` (752 lines)

Builds Avalonia control rows for each settings descriptor via
reflection into `AppSettings`. One method per `SettingKind`.

## Likely untested

- **Every `SettingKind` branch** — Bool, Int, Long, Double,
  Enum. No tests touch the generated controls.
- **Reflection throws when `desc.Key` doesn't match a
  property** — early throw at line 34. Guards against
  registry drift.
- **Two-way binding** between control and `AppSettings` —
  every edit should call `ScheduleSave` and invoke
  `onChanged`.
- **`EnabledWhenKey` dependency** — a row whose enabled-when
  key is false should be disabled. Who re-evaluates on
  dependency change? The border's `Opacity` + `IsEnabled`
  toggling is somewhere in this file.
- **`Increment` for doubles** on up/down arrows.
- **`Min`/`Max` clamping** — user typing out-of-range.
- **Enum rows** with many values.

## Architectural concerns

- **752 lines of mostly boilerplate per-kind row builder.**
  Could be generic over `T` per kind — would cut
  significantly. Reflection complicates it.
- **No shared style** — each row type has its own theme
  handling. CurrentTheme is a mutable static. Thread-safety
  assumed single-threaded UI.
- **No way to add custom/compound settings** — e.g. a color
  picker. New kinds require editing this file + the enum.
