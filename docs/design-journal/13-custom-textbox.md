# 13 — Custom Lightweight TextBox (2026-04-01)

## Motivation

Avalonia's built-in `TextPresenter` has a caret positioning bug (#12809): the
`GetCaretPoints()` method subtracts 1px from the caret X at end-of-line,
causing the caret to overlap the last glyph.  This affects every `TextBox` in
the app at all font sizes.  The bug cannot be patched externally (Harmony
broken on .NET 10, `Render` is sealed, epsilon-nudging `_caretBounds` fails
due to `Math.Floor` interactions).  A PR has been submitted to Avalonia
(`jdmichel/Avalonia#fix/caret-position-end-of-line`) but there is no timeline
for acceptance.

DMEdit already has a complete custom text rendering pipeline in
`EditorControl` — text layout via `TextLayoutEngine`, caret drawing, selection
rendering, keyboard/mouse input.  A lightweight single-line control reusing
that infrastructure gives us full control over caret positioning and
eliminates the dependency on Avalonia's `TextPresenter`.

## Design Goals

1. **Drop-in replacement** — swap into existing `DMTextBox` and
   `DMEditableCombo` templates with minimal AXAML changes.  The wrapper
   controls keep their overlay-button patterns; only the inner `<TextBox>`
   element changes to the new control.
2. **Minimal scope** — single-line text editing only.  No wrapping, no
   multi-line, no password reveal, no IME preedit, no data validation layer.
3. **AXAML-friendly** — expose styled properties for `Text`, `Watermark`,
   `IsReadOnly`, `FontSize`, `FontFamily`, `Foreground`, `Background`,
   `Padding`, `SelectionBrush`, `CaretBrush`.  Themeable via normal Avalonia
   styles.
4. **Reuse existing infrastructure** — `TextLayout` from Avalonia.Media for
   measurement and hit-testing (same as `TextLayoutEngine`), custom `Render`
   override for text/caret/selection drawing (same patterns as
   `EditorControl`).

## Component: `DMInputBox`

A `Control` subclass (not `TemplatedControl`) that handles its own rendering
and input.  Name avoids collision with Avalonia's `TextBox`.

### Styled Properties

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `Text` | `string?` | `""` | TwoWay binding, coerced to non-null |
| `Watermark` | `string?` | `null` | Placeholder text when empty + unfocused |
| `IsReadOnly` | `bool` | `false` | Blocks text input; allows selection/copy |
| `CaretIndex` | `int` | `0` | Caret position within Text |
| `SelectionStart` | `int` | `0` | Selection anchor |
| `SelectionEnd` | `int` | `0` | Selection active end |
| `FontSize` | `double` | inherited | |
| `FontFamily` | `FontFamily` | inherited | |
| `Foreground` | `IBrush?` | inherited | Text color |
| `Background` | `IBrush?` | `null` | Fill behind text |
| `CaretBrush` | `IBrush?` | `null` | Falls back to Foreground |
| `SelectionBrush` | `IBrush?` | system accent | |
| `Watermark­Brush` | `IBrush?` | `null` | Placeholder color |
| `Padding` | `Thickness` | `4,4` | Inner padding around text area |

### Internal State

- `TextLayout _layout` — Avalonia `TextLayout` for the current `Text` +
  font properties.  Recreated on text/font change.
- `double _scrollOffset` — horizontal scroll when text overflows the control.
- `bool _caretVisible` — blink state.
- `DispatcherTimer _caretTimer` — blink timer (500ms default).
- `int _selAnchor`, `int _selEnd` — selection range (anchor = where mouse
  down started or Shift was held; end = current caret position).

### Rendering (`Render` override)

Draw order (matches EditorControl pattern):

1. **Background** — `context.FillRectangle(Background, bounds)`.
2. **Selection highlight** — if `_selAnchor != _selEnd`, use
   `_layout.HitTestTextRange(start, length)` to get rects, fill with
   `SelectionBrush`.
3. **Text** — `_layout.Draw(context, textOrigin)` with horizontal scroll
   offset applied.  Clip to content area (excludes padding).
4. **Watermark** — if `Text` is empty and control is not focused, draw
   watermark string in `WatermarkBrush` at same origin.
5. **Caret** — if focused and `_caretVisible`:
   `var rect = _layout.HitTestTextPosition(CaretIndex)`.
   Draw 1.5px-wide filled rectangle at `rect.X + 1` (the +1 guarantees
   the caret is past the trailing glyph edge — the Avalonia bug fix).
   Apply `_scrollOffset` and clip.

### Text Input

- Override `OnTextInput(TextInputEventArgs)` — insert `e.Text` at
  `CaretIndex`, advance caret, clear selection.  No-op if `IsReadOnly`.
- Override `OnKeyDown(KeyEventArgs)` — handle:
  - **Arrow keys** (Left/Right, +Shift for selection, +Ctrl for word jump)
  - **Home/End** (+Shift for selection)
  - **Backspace/Delete** (+Ctrl for word delete)
  - **Ctrl+A** (select all)
  - **Ctrl+C/X/V** (clipboard)
  - **Escape** (deselect, or bubble for parent handling)

### Mouse Input

- `OnPointerPressed` — set caret via `_layout.HitTestPoint(pos)`, begin
  selection.  Double-click selects word.  Triple-click selects all.
- `OnPointerMoved` — extend selection if pointer is pressed.
- `OnPointerReleased` — finalize selection.

### Horizontal Scrolling

When text is wider than the control, `_scrollOffset` shifts the text left
so the caret stays visible.  Updated after every caret move:

```
var caretX = _layout.HitTestTextPosition(CaretIndex).X;
var visibleWidth = Bounds.Width - Padding.Left - Padding.Right;
if (caretX - _scrollOffset < 0)
    _scrollOffset = caretX;
else if (caretX - _scrollOffset > visibleWidth)
    _scrollOffset = caretX - visibleWidth;
```

### Public API

```csharp
public void Focus();         // Focus + show caret
public void SelectAll();     // Select entire text
public string SelectedText;  // Get/set selected text
```

### Context Menu

Attach the same `DefaultTextBoxContextFlyout` resource from
`TextBoxTheme.axaml` (Cut/Copy/Paste).  Alternatively, define it inline
in the control or as a static resource.

## Integration into Existing Controls

### DMTextBox

**Theme change** (`DMTextBoxTheme.axaml`):
```xml
<!-- Before -->
<TextBox x:Name="PART_TextBox" Grid.ColumnSpan="2"
         Text="{TemplateBinding Text, Mode=TwoWay}" ... />

<!-- After -->
<controls:DMInputBox x:Name="PART_TextBox" Grid.ColumnSpan="2"
                     Text="{TemplateBinding Text, Mode=TwoWay}" ... />
```

**Code-behind** (`DMTextBox.cs`):
- Change `InnerTextBox` type from `TextBox?` to `DMInputBox?`.
- Remove dynamic padding adjustment (clear button space handled by AXAML
  margin/column layout instead).
- `Focus()` and `SelectAll()` delegate to `DMInputBox` directly.

### DMEditableCombo

Same pattern — swap `<TextBox>` for `<controls:DMInputBox>` in the theme,
change `InnerTextBox` type, remove padding manipulation.

### Standalone TextBox Usages

| Location | Current | Migration |
|----------|---------|-----------|
| `GoToLineWindow.cs` | `new TextBox { ... }` | `new DMInputBox { ... }` |
| `ErrorDialog.cs` | `new TextBox { IsReadOnly, TextWrapping }` | Keep Avalonia TextBox — needs multi-line wrapping |
| `SettingRowFactory.cs` | `new TextBox { AcceptsReturn, TextWrapping }` | Keep Avalonia TextBox — needs multi-line |
| `CommandsSettingsSection.axaml` | `<controls:DMTextBox>` | No change needed (DMTextBox wraps DMInputBox) |
| `SettingsControl.axaml` | `<controls:DMTextBox>` | No change needed |
| `FindBarControl.axaml` | `<controls:DMEditableCombo>` | No change needed |
| `CommandPaletteWindow.cs` | `new DMTextBox { ... }` | No change needed |
| `ComboBoxTheme.axaml` | `<TextBox PART_EditableTextBox>` | Keep Avalonia TextBox — Avalonia ComboBox expects it |
| `NumericUpDownTheme.axaml` | `<TextBox PART_TextBox>` | Keep Avalonia TextBox — Avalonia NUD expects it |

### Theme File Changes

- **`TextBoxTheme.axaml`** — keep as-is.  Still needed for Avalonia
  `TextBox` usages (ErrorDialog, SettingRowFactory, ComboBox, NUD).
  `FluentTextBoxButton` also still needed by DMTextBox/DMEditableCombo
  overlay buttons.
- **`DMTextBoxTheme.axaml`** — swap `<TextBox>` → `<controls:DMInputBox>`.
- **`DMEditableComboTheme.axaml`** — swap `<TextBox>` → `<controls:DMInputBox>`.
- No other theme files change.

## File Plan

| File | Action |
|------|--------|
| `src/DMEdit.App/Controls/DMInputBox.cs` | **New** — the custom control (~400 lines) |
| `src/DMEdit.App/Themes/DMTextBoxTheme.axaml` | **Edit** — swap TextBox → DMInputBox |
| `src/DMEdit.App/Themes/DMEditableComboTheme.axaml` | **Edit** — swap TextBox → DMInputBox |
| `src/DMEdit.App/Controls/DMTextBox.cs` | **Edit** — change InnerTextBox type, remove padding hack |
| `src/DMEdit.App/Controls/DMEditableCombo.cs` | **Edit** — change InnerTextBox type, remove padding hack |
| `src/DMEdit.App/GoToLineWindow.cs` | **Edit** — use DMInputBox instead of TextBox |
| `tests/DMEdit.App.Tests/` | **New** — DMInputBox unit tests (text insert, selection, caret movement, clipboard) |

## Implementation Notes (2026-04-01)

### Bugs fixed after initial implementation

1. **Border / hover / focus / background** — DMInputBox initially rendered no
   chrome.  Added `BorderBrush`, `BorderThickness`, `CornerRadius` styled
   properties.  `Render()` draws a `RoundedRect` border+background.  Avalonia
   styles in `App.axaml` target `controls|DMInputBox`, `:pointerover`, and
   `:focus` pseudoclasses using the same `TextControl*` dynamic resources as
   the TextBox theme.

2. **Clear button didn't clear text** — `OnTextInput` used `Text = value`
   (`SetValue`) which created a local value overriding the TemplateBinding
   from DMTextBox.  Fixed: all property mutations inside DMInputBox use
   `SetCurrentValue()` to preserve bindings.

3. **Watermark disappeared when focused** — Removed `&& !IsFocused` condition.
   Watermark now shows whenever text is empty.  Drawn with
   `PushOpacity(0.5)` for muted placeholder appearance.

4. **Crash on typing after clear** — `CaretIndex` could exceed `Text.Length`
   after external text change.  All index operations now clamp via
   `Math.Clamp(..., 0, text.Length)` before string slicing.

5. **Watermark foreground** — Changed fallback brush from hardcoded gray to
   `Foreground ?? Brushes.Gray` so it's theme-aware.

### Caret rendering improvements (both EditorControl and DMInputBox)

- **Width reduced** from 1.5 to 1.0 DIP (configurable via `CaretWidth`
  setting, range 1.0–2.5 in 0.5 increments).
- **DMInputBox +1 offset removed** — the `+1` was a misguided workaround;
  the Avalonia bug is in `TextPresenter.GetCaretPoints()`, not in
  `TextLayout.HitTestTextPosition()`.
- **Device-pixel snapping** — caret X is snapped via
  `Math.Round(caretX * scale) / scale` to prevent anti-aliasing across
  two physical pixels (which made the caret appear faint/thin).

### Row height pixel-snapping (EditorControl)

`GetRowHeight()` now snaps the raw `TextLayout.Height` to the nearest
device pixel multiple via `Math.Ceiling(raw * scale) / scale`.  This
ensures `line.Row * rh` is always pixel-aligned, eliminating the ±1px
inter-line jitter visible during slow pixel-by-pixel scrolling.

### Other fixes in this session

- **FluentTextBoxButton CornerRadius** — added
  `CornerRadius="{TemplateBinding CornerRadius}"` to the ContentPresenter
  in `TextBoxTheme.axaml` so dropdown button rounded corners are visible.
- **Find bar expand button focus** — `ExpandBtn.Click` now refocuses
  `SearchBox.InnerTextBox` after toggling replace mode.
- **Settings row size stability** — reset button uses `Opacity`/
  `IsHitTestVisible` instead of `IsVisible` to preserve layout when
  toggling modified state.
- **SettingDescriptor.Increment** — new parameter (default 0.1) for
  `SettingKind.Double` entries, wired into the NumericUpDown control.
  `CaretWidth` uses `Increment: 0.5`.

## Remaining Work

- **EditorControl caret X offset** — caret appears consistently further
  right than DMInputBox when using the same font and size.  Both use
  `HitTestTextPosition().X` identically.  Root cause still unknown —
  may be related to the editor's `maxWidth` parameter on TextLayout or
  a subtle difference in how the layout engine constructs per-line layouts.
  Needs investigation.
- **DMInputBox unit tests** — planned but not yet written.  Should cover
  text insert, selection, caret movement, clipboard, and boundary clamps.
- **Paged add-buffer eviction** — deferred from prior session, still
  pending.  See [12-utf8-add-buffer](12-utf8-add-buffer.md).

## Verification

1. `dotnet build` — zero errors, zero warnings.
2. `dotnet test` — 599 passed (492 Core + 43 Rendering + 64 App), 1 skipped.
3. Manual testing:
   - Find bar: type search term, verify caret after last character, click
     to reposition, Ctrl+A select all, Ctrl+C copy, arrow keys, Home/End.
   - GoTo Line dialog: type number, Enter to confirm.
   - Settings: DMTextBox search filter, command shortcut capture box.
   - DMEditableCombo: history dropdown selection populates text with correct
     caret, clear button clears and refocuses.
   - Caret width setting: slider steps 1.0/1.5/2.0/2.5, applies to both
     editor and chrome text boxes.
   - Slow scroll: inter-line spacing stays uniform.
