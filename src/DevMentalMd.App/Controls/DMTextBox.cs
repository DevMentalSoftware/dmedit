using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace DevMentalMd.App.Controls;

/// <summary>
/// TextBox with a built-in auto-hiding clear button.  The clear button sits
/// outside the TextBox visual tree (overlay Grid pattern) so hovering it
/// does not propagate :pointerover to the TextBox.
/// </summary>
public class DMTextBox : TemplatedControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DMTextBox, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<DMTextBox, string?>(nameof(Watermark));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DMTextBox, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> ShowClearButtonProperty =
        AvaloniaProperty.Register<DMTextBox, bool>(nameof(ShowClearButton), defaultValue: true);

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool IsReadOnly {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool ShowClearButton {
        get => GetValue(ShowClearButtonProperty);
        set => SetValue(ShowClearButtonProperty, value);
    }

    /// <summary>Inner TextBox for attaching key handlers, CaretIndex, SelectAll() etc.</summary>
    public TextBox? InnerTextBox { get; private set; }

    private Button? _clearButton;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e) {
        base.OnApplyTemplate(e);

        InnerTextBox = e.NameScope.Find<TextBox>("PART_TextBox");
        _clearButton = e.NameScope.Find<Button>("PART_ClearButton");

        if (_clearButton != null) {
            _clearButton.Click += (_, _) => {
                Text = "";
                InnerTextBox?.Focus();
            };
        }

        UpdateClearButtonVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty) {
            UpdateClearButtonVisibility();
        }
    }

    private void UpdateClearButtonVisibility() {
        if (_clearButton != null) {
            _clearButton.IsVisible = ShowClearButton && !string.IsNullOrEmpty(Text);
        }
    }

    /// <summary>Forward focus to the inner TextBox.</summary>
    protected override void OnGotFocus(Avalonia.Input.GotFocusEventArgs e) {
        base.OnGotFocus(e);
        InnerTextBox?.Focus();
    }
}
