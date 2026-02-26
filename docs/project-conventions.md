# Project Conventions

Shared decisions about naming, abbreviations, and reserved names.
Update this file whenever a new convention is established.

---

## Abbreviations

| Full word / phrase | Abbreviation | Notes |
|---|---|---|
| buffer | `buf` | |
| count | `cnt` | |
| index | `idx` | |
| length | `len` | |
| offset | `ofs` | use `offset` in public API params for clarity |
| minimum | `min` | |
| maximum | `max` | |
| character | `char` | also the C# keyword; use `ch` for local variables |
| line | `line` | short enough, don't abbreviate |
| column | `col` | |
| position | `pos` | |
| text | `text` | short enough, don't abbreviate |
| document | `doc` | for locals; `Document` for type/field names |
| start | `start` | short enough |
| end | `end` | short enough |
| formatted | `fmt` | for `FormattedText` locals |

---

## Reserved Names (avoid using these as type or member names)

These names conflict with BCL types or commonly used library types we may import.

### System / BCL

| Name | Conflicts with |
|---|---|
| `Path` | `System.IO.Path` |
| `File` | `System.IO.File` |
| `Directory` | `System.IO.Directory` |
| `Stream` | `System.IO.Stream` |
| `Task` | `System.Threading.Tasks.Task` |
| `Timer` | `System.Threading.Timer` |
| `Action` | `System.Action` |
| `Func` | `System.Func` |
| `Type` | `System.Type` |
| `String` | `System.String` |
| `List` | `System.Collections.Generic.List<T>` |
| `Dictionary` | `System.Collections.Generic.Dictionary<K,V>` |
| `Point` | `Avalonia.Point` |
| `Rect` | `Avalonia.Rect` |
| `Size` | `Avalonia.Size` |
| `Color` | `Avalonia.Media.Color` |
| `Brush` | `Avalonia.Media.Brush` |
| `Typeface` | `Avalonia.Media.Typeface` |
| `FontFamily` | `Avalonia.Media.FontFamily` |
| `Control` | `Avalonia.Controls.Control` |
| `Window` | `Avalonia.Controls.Window` |
| `Application` | `Avalonia.Application` |

### Notes

- Libraries confirmed not in use (reserved names from these can be reused if needed): none yet.
- When a name conflict seems unavoidable, prefer a more specific name (e.g., `DocHistory` instead of `History`).

---

## Markdown Output Preferences

When generating or normalizing Markdown:

- Unordered lists: use `-`
- Bold / italic: use `*` (not `_`)
- Code blocks: use ` ``` `
- Horizontal rules: use `***`
