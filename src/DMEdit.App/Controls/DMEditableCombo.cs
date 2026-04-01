using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DMEdit.App.Controls;

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

    public static readonly StyledProperty<string?> HighlightItemProperty =
        AvaloniaProperty.Register<DMEditableCombo, string?>(nameof(HighlightItem));

    public static readonly StyledProperty<bool> ShowClearButtonProperty =
        AvaloniaProperty.Register<DMEditableCombo, bool>(nameof(ShowClearButton), defaultValue: true);



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

    /// <summary>
    /// Item to highlight with a star (\u2605) in the dropdown.
    /// The star is display-only — selecting the item produces the
    /// clean string without the star.
    /// </summary>
    public string? HighlightItem {
        get => GetValue(HighlightItemProperty);
        set => SetValue(HighlightItemProperty, value);
    }

    /// <summary>Whether the clear button is shown when text is non-empty. Default true.</summary>
    public bool ShowClearButton {
        get => GetValue(ShowClearButtonProperty);
        set => SetValue(ShowClearButtonProperty, value);
    }

    /// <summary>Inner DMInputBox for attaching key handlers, CaretIndex, SelectAll() etc.</summary>
    public DMInputBox? InnerTextBox { get; private set; }

    private Button? _clearButton;
    private Button? _dropDownButton;
    private Popup? _popup;
    private ListBox? _listBox;
    private Border? _popupBorder;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e) {
        base.OnApplyTemplate(e);

        InnerTextBox = e.NameScope.Find<DMInputBox>("PART_TextBox");
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
            // Prevent wheel events from leaking to the parent scroll
            // viewer when the list reaches its scroll bounds.  Bubble
            // phase so the ListBox's internal ScrollViewer scrolls first.
            _listBox.AddHandler(PointerWheelChangedEvent, (_, e) => e.Handled = true,
                Avalonia.Interactivity.RoutingStrategies.Bubble);

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
        }
        if (change.Property == ShowClearButtonProperty) {
            UpdateClearButtonVisibility();
        }
    }

    // -----------------------------------------------------------------
    // Clear button
    // -----------------------------------------------------------------

    private void UpdateClearButtonVisibility() {
        if (_clearButton != null) {
            _clearButton.IsVisible = ShowClearButton && !string.IsNullOrEmpty(Text);
        }
    }

    // -----------------------------------------------------------------
    // History popup
    // -----------------------------------------------------------------

    private void ShowPopup(string? filter) {
        if (_popup == null || _listBox == null) return;
        var items = ItemsSource;
        if (items == null || items.Count == 0) return;

        var list = items.ToList();
        if (list.Count == 0) {
            _popup.IsOpen = false;
            return;
        }

        // Set an item template that appends a star glyph for the
        // highlighted item.  The data stays clean — star is visual only.
        var highlight = HighlightItem;
        _listBox.ItemTemplate = new FuncDataTemplate<string>((item, _) => {
            if (item == null) return new TextBlock();
            var panel = new StackPanel {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
            };
            panel.Children.Add(new TextBlock { Text = item });
            if (highlight != null
                && item.Equals(highlight, StringComparison.OrdinalIgnoreCase)) {
                panel.Children.Add(new TextBlock {
                    Text = "\u2605",
                    Foreground = Brushes.Gold,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            return panel;
        }, supportsRecycling: false);

        _listBox.ItemsSource = list;

        // Pre-select the item matching the current text.
        var preSelectIdx = -1;
        if (filter != null) {
            preSelectIdx = list.FindIndex(s =>
                s.Equals(filter, StringComparison.OrdinalIgnoreCase));
        }
        _listBox.SelectedIndex = preSelectIdx;

        // Match the popup width to the control's actual width.
        if (_popupBorder != null && Bounds.Width > 0) {
            _popupBorder.Width = Bounds.Width;
        }
        _popup.IsOpen = true;

        // Scroll the pre-selected item into view.
        if (preSelectIdx >= 0) {
            _listBox.ScrollIntoView(preSelectIdx);
        }
    }

    private void ApplySelection(string value) {
        // Close the popup before setting Text so that PropertyChanged
        // observers see IsPopupOpen == false and can apply the change
        // immediately (e.g. the font picker updates the editor font).
        if (_popup != null) {
            _popup.IsOpen = false;
        }
        Text = value;
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
