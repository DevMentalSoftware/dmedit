using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace DevMentalMd.App.Controls;

/// <summary>
/// Editable combo box: TextBox + auto-hiding clear button + always-visible
/// dropdown button + history popup with auto-suggest.  The buttons sit outside
/// the TextBox visual tree (overlay Grid) so hovering them does not propagate
/// :pointerover to the TextBox.
/// </summary>
public class DMEditableCombo : TemplatedControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DMEditableCombo, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<DMEditableCombo, string?>(nameof(Watermark));

    public static readonly StyledProperty<IList<string>?> ItemsSourceProperty =
        AvaloniaProperty.Register<DMEditableCombo, IList<string>?>(nameof(ItemsSource));

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>History/completion items shown in the dropdown.</summary>
    public IList<string>? ItemsSource {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Inner TextBox for attaching key handlers, CaretIndex, SelectAll() etc.</summary>
    public TextBox? InnerTextBox { get; private set; }

    private Button? _clearButton;
    private Button? _dropDownButton;
    private Popup? _popup;
    private ListBox? _listBox;
    private Border? _popupBorder;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e) {
        base.OnApplyTemplate(e);

        InnerTextBox = e.NameScope.Find<TextBox>("PART_TextBox");
        _clearButton = e.NameScope.Find<Button>("PART_ClearButton");
        _dropDownButton = e.NameScope.Find<Button>("PART_DropDownButton");
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        _listBox = e.NameScope.Find<ListBox>("PART_ListBox");
        _popupBorder = e.NameScope.Find<Border>("PART_PopupBorder");

        if (_clearButton != null) {
            _clearButton.Click += (_, _) => {
                Text = "";
                InnerTextBox?.Focus();
            };
        }

        if (_dropDownButton != null) {
            _dropDownButton.Click += (_, _) => {
                ShowPopup(filter: Text);
                InnerTextBox?.Focus();
            };
        }

        if (_listBox != null) {
            _listBox.PointerReleased += (_, _) => {
                if (_listBox.SelectedItem is string s) {
                    ApplySelection(s);
                }
            };
        }

        if (InnerTextBox != null) {
            InnerTextBox.KeyDown += OnInnerKeyDown;
        }

        UpdateClearButtonVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty) {
            UpdateClearButtonVisibility();
            // Re-filter the popup while it's open.
            if (_popup?.IsOpen == true) {
                ShowPopup(filter: Text);
            }
        }
    }

    // -----------------------------------------------------------------
    // Clear button
    // -----------------------------------------------------------------

    private void UpdateClearButtonVisibility() {
        if (_clearButton != null) {
            _clearButton.IsVisible = !string.IsNullOrEmpty(Text);
        }
    }

    // -----------------------------------------------------------------
    // History popup
    // -----------------------------------------------------------------

    private void ShowPopup(string? filter) {
        if (_popup == null || _listBox == null) return;
        var items = ItemsSource;
        if (items == null || items.Count == 0) return;

        IEnumerable<string> filtered = items;
        if (!string.IsNullOrEmpty(filter)) {
            filtered = items.Where(h => h.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        if (list.Count == 0) {
            _popup.IsOpen = false;
            return;
        }

        _listBox.ItemsSource = list;
        _listBox.SelectedIndex = -1;
        // Match the popup width to the control's actual width.
        if (_popupBorder != null && Bounds.Width > 0) {
            _popupBorder.Width = Bounds.Width;
        }
        _popup.IsOpen = true;
    }

    private void ApplySelection(string value) {
        Text = value;
        if (_popup != null) {
            _popup.IsOpen = false;
        }
        InnerTextBox?.Focus();
        InnerTextBox?.SelectAll();
    }

    // -----------------------------------------------------------------
    // Keyboard handling for history popup
    // -----------------------------------------------------------------

    private void OnInnerKeyDown(object? sender, KeyEventArgs e) {
        if (_popup?.IsOpen == true && HandleHistoryKeyDown(e)) {
            return;
        }

        // Escape closes popup if open; otherwise bubbles up for parent to handle.
        if (e.Key == Key.Escape && _popup?.IsOpen == true) {
            _popup.IsOpen = false;
            e.Handled = true;
            return;
        }

        // Up/Down arrows open the history popup when it's closed,
        // filtered by the current text.
        if (e.Key is Key.Down or Key.Up && _popup?.IsOpen != true) {
            ShowPopup(filter: Text);
            // Select the first item so Down immediately highlights a row.
            if (_listBox is { ItemCount: > 0 }) {
                _listBox.SelectedIndex = 0;
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Arrow-key navigation and Enter selection in the history popup.
    /// Returns true if the key was consumed.
    /// </summary>
    private bool HandleHistoryKeyDown(KeyEventArgs e) {
        if (_listBox == null) return false;

        switch (e.Key) {
            case Key.Down:
                if (_listBox.ItemCount > 0) {
                    _listBox.SelectedIndex = Math.Min(_listBox.SelectedIndex + 1, _listBox.ItemCount - 1);
                }
                e.Handled = true;
                return true;
            case Key.Up:
                if (_listBox.ItemCount > 0) {
                    _listBox.SelectedIndex = Math.Max(_listBox.SelectedIndex - 1, 0);
                }
                e.Handled = true;
                return true;
            case Key.Return:
                if (_listBox.SelectedItem is string s) {
                    ApplySelection(s);
                    e.Handled = true;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>Forward focus to the inner TextBox.</summary>
    protected override void OnGotFocus(GotFocusEventArgs e) {
        base.OnGotFocus(e);
        InnerTextBox?.Focus();
    }

    /// <summary>Close popup when the control loses focus entirely.</summary>
    public void ClosePopup() {
        if (_popup != null) {
            _popup.IsOpen = false;
        }
    }

    /// <summary>Whether the popup is currently open.</summary>
    public bool IsPopupOpen => _popup?.IsOpen == true;
}
