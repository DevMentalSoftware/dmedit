using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

/// <summary>
/// Lightweight single-line text input control that replaces Avalonia's TextBox
/// for all DMEdit UI chrome.  Renders text, caret, and selection directly via
/// <see cref="TextLayout"/> — same approach as <see cref="EditorControl"/> —
/// to work around Avalonia's caret-positioning bug (#12809).
/// </summary>
public class DMInputBox : Control {
    // ---------------------------------------------------------------
    // Styled properties
    // ---------------------------------------------------------------

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DMInputBox, string?>(nameof(Text), "",
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (_, v) => v ?? "");

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<DMInputBox, string?>(nameof(Watermark));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DMInputBox, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<int> CaretIndexProperty =
        AvaloniaProperty.Register<DMInputBox, int>(nameof(CaretIndex));

    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<DMInputBox, int>(nameof(SelectionStart));

    public static readonly StyledProperty<int> SelectionEndProperty =
        AvaloniaProperty.Register<DMInputBox, int>(nameof(SelectionEnd));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        Border.BorderThicknessProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<DMInputBox, IBrush?>(nameof(CaretBrush));

    public static readonly StyledProperty<double> CaretWidthProperty =
        AvaloniaProperty.Register<DMInputBox, double>(nameof(CaretWidth), 1.0);

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<DMInputBox, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<IBrush?> WatermarkBrushProperty =
        AvaloniaProperty.Register<DMInputBox, IBrush?>(nameof(WatermarkBrush));

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<DMInputBox>();

    public static readonly StyledProperty<Thickness> PaddingProperty =
        Decorator.PaddingProperty.AddOwner<DMInputBox>();

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

    public int CaretIndex {
        get => GetValue(CaretIndexProperty);
        set => SetValue(CaretIndexProperty, value);
    }

    public int SelectionStart {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public IBrush? Foreground {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Background {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public CornerRadius CornerRadius {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IBrush? CaretBrush {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public double CaretWidth {
        get => GetValue(CaretWidthProperty);
        set => SetValue(CaretWidthProperty, value);
    }

    public IBrush? SelectionBrush {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush? WatermarkBrush {
        get => GetValue(WatermarkBrushProperty);
        set => SetValue(WatermarkBrushProperty, value);
    }

    public double FontSize {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public Thickness Padding {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    // ---------------------------------------------------------------
    // Internal state
    // ---------------------------------------------------------------

    private TextLayout? _layout;
    private TextLayout? _watermarkLayout;
    private double _scrollOffset;
    private bool _caretVisible;
    private readonly DispatcherTimer _caretTimer;
    private bool _isPointerPressed;
    private int _clickCount;

    static DMInputBox() {
        FocusableProperty.OverrideDefaultValue<DMInputBox>(true);
        CursorProperty.OverrideDefaultValue<DMInputBox>(new Cursor(StandardCursorType.Ibeam));
        PaddingProperty.OverrideDefaultValue<DMInputBox>(new Thickness(4));
    }

    public DMInputBox() {
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _caretTimer.Tick += (_, _) => {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
    }

    // ---------------------------------------------------------------
    // Public helpers
    // ---------------------------------------------------------------

    public void SelectAll() {
        var text = Text ?? "";
        SetCurrentValue(SelectionStartProperty, 0);
        SetCurrentValue(SelectionEndProperty, text.Length);
        SetCurrentValue(CaretIndexProperty, text.Length);
        InvalidateVisual();
    }

    public string SelectedText {
        get {
            var text = Text ?? "";
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, 0, text.Length);
            return start < end ? text[start..end] : "";
        }
    }

    // ---------------------------------------------------------------
    // Layout invalidation
    // ---------------------------------------------------------------

    private TextLayout CreateLayout(string text) {
        var typeface = new Typeface(FontFamily);
        return new TextLayout(
            text,
            typeface,
            FontSize,
            Foreground ?? Brushes.Black,
            textWrapping: TextWrapping.NoWrap);
    }

    private TextLayout CreateWatermarkLayout(string text) {
        var typeface = new Typeface(FontFamily);
        return new TextLayout(
            text,
            typeface,
            FontSize,
            WatermarkBrush ?? Foreground ?? Brushes.Gray,
            textWrapping: TextWrapping.NoWrap);
    }

    private void InvalidateLayout() {
        _layout = null;
        _watermarkLayout = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private TextLayout GetLayout() {
        return _layout ??= CreateLayout(Text ?? "");
    }

    private TextLayout GetWatermarkLayout() {
        return _watermarkLayout ??= CreateWatermarkLayout(Watermark ?? "");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty
            || change.Property == FontSizeProperty
            || change.Property == FontFamilyProperty
            || change.Property == ForegroundProperty) {
            InvalidateLayout();
            // Clamp caret/selection to new text length.
            var len = (Text ?? "").Length;
            if (CaretIndex > len) { SetCurrentValue(CaretIndexProperty, len); }
            if (SelectionStart > len) { SetCurrentValue(SelectionStartProperty, len); }
            if (SelectionEnd > len) { SetCurrentValue(SelectionEndProperty, len); }
            EnsureCaretVisible();
        }

        if (change.Property == WatermarkProperty
            || change.Property == WatermarkBrushProperty) {
            _watermarkLayout = null;
            InvalidateVisual();
        }

        if (change.Property == CaretIndexProperty) {
            EnsureCaretVisible();
            ResetCaretBlink();
            InvalidateVisual();
        }

        if (change.Property == PaddingProperty
            || change.Property == BackgroundProperty
            || change.Property == BorderBrushProperty
            || change.Property == BorderThicknessProperty
            || change.Property == CornerRadiusProperty
            || change.Property == CaretBrushProperty
            || change.Property == SelectionBrushProperty
            || change.Property == SelectionStartProperty
            || change.Property == SelectionEndProperty) {
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------
    // Measure / Arrange
    // ---------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize) {
        var layout = GetLayout();
        var pad = Padding;
        var bt = BorderThickness;
        var h = Math.Max(layout.Height, FontSize * 1.4) + pad.Top + pad.Bottom + bt.Top + bt.Bottom;
        return new Size(0, h); // width: fill available
    }

    // ---------------------------------------------------------------
    // Render
    // ---------------------------------------------------------------

    public override void Render(DrawingContext context) {
        var bounds = new Rect(Bounds.Size);
        var pad = Padding;
        var bt = BorderThickness;
        var borderThick = bt.Top; // uniform thickness

        // 1. Border + background
        var cr = CornerRadius;
        if (borderThick > 0 && BorderBrush is { } borderBrush) {
            var pen = new Pen(borderBrush, borderThick);
            var half = borderThick / 2;
            var borderRect = new RoundedRect(
                bounds.Deflate(new Thickness(half)),
                cr.TopLeft, cr.TopRight, cr.BottomRight, cr.BottomLeft);
            context.DrawRectangle(Background, pen, borderRect);
        } else if (Background is { } bg) {
            var bgRect = new RoundedRect(
                bounds,
                cr.TopLeft, cr.TopRight, cr.BottomRight, cr.BottomLeft);
            context.DrawRectangle(bg, null, bgRect);
        }

        // Content area: inset by border + padding
        var contentLeft = bt.Left + pad.Left;
        var contentTop = bt.Top + pad.Top;
        var contentWidth = bounds.Width - bt.Left - bt.Right - pad.Left - pad.Right;
        var contentHeight = bounds.Height - bt.Top - bt.Bottom - pad.Top - pad.Bottom;
        if (contentWidth <= 0) return;

        var layout = GetLayout();
        var textY = contentTop + (contentHeight - layout.Height) / 2;
        var textOrigin = new Point(contentLeft - _scrollOffset, textY);

        // Clip to content area
        using (context.PushClip(new Rect(contentLeft, 0, contentWidth, bounds.Height))) {
            // 2. Selection highlight
            var selStart = Math.Min(SelectionStart, SelectionEnd);
            var selEnd = Math.Max(SelectionStart, SelectionEnd);
            if (selStart != selEnd) {
                var selBrush = SelectionBrush ?? new SolidColorBrush(Color.FromArgb(100, 0, 120, 215));
                var selRects = layout.HitTestTextRange(selStart, selEnd - selStart);
                foreach (var r in selRects) {
                    var rect = new Rect(
                        r.Left + textOrigin.X,
                        textY,
                        r.Width,
                        layout.Height);
                    context.FillRectangle(selBrush, rect);
                }
            }

            // 3. Text or watermark
            var text = Text ?? "";
            if (text.Length > 0) {
                layout.Draw(context, textOrigin);
            } else if (!string.IsNullOrEmpty(Watermark)) {
                // Show watermark when text is empty, even when focused.
                var wmLayout = GetWatermarkLayout();
                var wmY = contentTop + (contentHeight - wmLayout.Height) / 2;
                using (context.PushOpacity(0.5)) {
                    wmLayout.Draw(context, new Point(contentLeft, wmY));
                }
            }

            // 4. Caret
            if (IsFocused && _caretVisible) {
                var caretHit = layout.HitTestTextPosition(CaretIndex);
                var caretX = caretHit.X + textOrigin.X;
                var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
                caretX = Math.Round(caretX * scale) / scale;
                var caretBrush = CaretBrush ?? Foreground ?? Brushes.Black;
                context.FillRectangle(caretBrush, new Rect(caretX, textY, CaretWidth, layout.Height));
            }
        }
    }

    // ---------------------------------------------------------------
    // Horizontal scrolling
    // ---------------------------------------------------------------

    private void EnsureCaretVisible() {
        var layout = GetLayout();
        var caretHit = layout.HitTestTextPosition(CaretIndex);
        var caretX = caretHit.X;
        var pad = Padding;
        var bt = BorderThickness;
        var visibleWidth = Bounds.Width - bt.Left - bt.Right - pad.Left - pad.Right;
        if (visibleWidth <= 0) return;

        if (caretX - _scrollOffset < 0) {
            _scrollOffset = caretX;
        } else if (caretX - _scrollOffset > visibleWidth) {
            _scrollOffset = caretX - visibleWidth;
        }
    }

    // ---------------------------------------------------------------
    // Caret blink
    // ---------------------------------------------------------------

    private void ResetCaretBlink() {
        _caretVisible = true;
        _caretTimer.Stop();
        _caretTimer.Start();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) {
        base.OnGotFocus(e);
        ResetCaretBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e) {
        base.OnLostFocus(e);
        _caretTimer.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------
    // Text input
    // ---------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        if (IsReadOnly || string.IsNullOrEmpty(e.Text)) return;

        var text = Text ?? "";
        var caretIdx = Math.Clamp(CaretIndex, 0, text.Length);
        var selStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);

        if (selStart != selEnd) {
            text = text[..selStart] + e.Text + text[selEnd..];
        } else {
            text = text[..caretIdx] + e.Text + text[caretIdx..];
            selStart = caretIdx;
        }

        SetCurrentValue(TextProperty, text);
        var newCaret = selStart + e.Text.Length;
        SetCurrentValue(CaretIndexProperty, newCaret);
        SetCurrentValue(SelectionStartProperty, newCaret);
        SetCurrentValue(SelectionEndProperty, newCaret);
        e.Handled = true;
    }

    // ---------------------------------------------------------------
    // Keyboard input
    // ---------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var text = Text ?? "";
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;

        switch (e.Key) {
            case Key.Left:
                MoveCaret(ctrl ? PrevWordBoundary(text, CaretIndex) : CodepointBoundary.StepLeft(text, Math.Min(CaretIndex, text.Length)), shift);
                e.Handled = true;
                break;

            case Key.Right:
                MoveCaret(ctrl ? NextWordBoundary(text, CaretIndex) : CodepointBoundary.StepRight(text, Math.Min(CaretIndex, text.Length)), shift);
                e.Handled = true;
                break;

            case Key.Home:
                MoveCaret(0, shift);
                e.Handled = true;
                break;

            case Key.End:
                MoveCaret(text.Length, shift);
                e.Handled = true;
                break;

            case Key.Back:
                if (!IsReadOnly) { HandleBackspace(text, ctrl); }
                e.Handled = true;
                break;

            case Key.Delete:
                if (!IsReadOnly) { HandleDelete(text, ctrl); }
                e.Handled = true;
                break;

            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopySelection();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                if (!IsReadOnly) { CutSelection(); }
                e.Handled = true;
                break;

            case Key.V when ctrl:
                if (!IsReadOnly) { _ = PasteAsync(); }
                e.Handled = true;
                break;

            case Key.Escape:
                if (SelectionStart != SelectionEnd) {
                    SetCurrentValue(SelectionStartProperty, CaretIndex);
                    SetCurrentValue(SelectionEndProperty, CaretIndex);
                    e.Handled = true;
                }
                // else: let it bubble for parent handling
                break;
        }
    }

    private void MoveCaret(int pos, bool extend) {
        SetCurrentValue(CaretIndexProperty, pos);
        if (extend) {
            SetCurrentValue(SelectionEndProperty, pos);
        } else {
            SetCurrentValue(SelectionStartProperty, pos);
            SetCurrentValue(SelectionEndProperty, pos);
        }
    }

    private void HandleBackspace(string text, bool wordMode) {
        var selStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);
        if (selStart != selEnd) {
            DeleteRange(text, selStart, selEnd);
            return;
        }
        var caretIdx = Math.Clamp(CaretIndex, 0, text.Length);
        if (caretIdx == 0) return;
        var target = wordMode
            ? PrevWordBoundary(text, caretIdx)
            : CodepointBoundary.StepLeft(text, caretIdx);
        DeleteRange(text, target, caretIdx);
    }

    private void HandleDelete(string text, bool wordMode) {
        var selStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);
        if (selStart != selEnd) {
            DeleteRange(text, selStart, selEnd);
            return;
        }
        var caretIdx = Math.Clamp(CaretIndex, 0, text.Length);
        if (caretIdx >= text.Length) return;
        var target = wordMode
            ? NextWordBoundary(text, caretIdx)
            : CodepointBoundary.StepRight(text, caretIdx);
        DeleteRange(text, caretIdx, target);
    }

    private void DeleteRange(string text, int start, int end) {
        SetCurrentValue(TextProperty, text[..start] + text[end..]);
        SetCurrentValue(CaretIndexProperty, start);
        SetCurrentValue(SelectionStartProperty, start);
        SetCurrentValue(SelectionEndProperty, start);
    }

    // ---------------------------------------------------------------
    // Clipboard
    // ---------------------------------------------------------------

    private void CopySelection() {
        var sel = SelectedText;
        if (sel.Length > 0) {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(sel);
        }
    }

    private void CutSelection() {
        var sel = SelectedText;
        if (sel.Length == 0) return;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(sel);
        var text = Text ?? "";
        var start = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);
        DeleteRange(text, start, end);
    }

    private async System.Threading.Tasks.Task PasteAsync() {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var clip = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(clip)) return;

        // Strip newlines — single line only.
        clip = clip.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        var text = Text ?? "";
        var selStart = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), 0, text.Length);

        if (selStart != selEnd) {
            text = text[..selStart] + clip + text[selEnd..];
        } else {
            selStart = Math.Clamp(CaretIndex, 0, text.Length);
            text = text[..selStart] + clip + text[selStart..];
        }

        SetCurrentValue(TextProperty, text);
        var newPos = selStart + clip.Length;
        SetCurrentValue(CaretIndexProperty, newPos);
        SetCurrentValue(SelectionStartProperty, newPos);
        SetCurrentValue(SelectionEndProperty, newPos);
    }

    // ---------------------------------------------------------------
    // Word boundary helpers
    // ---------------------------------------------------------------

    internal static int PrevWordBoundary(string text, int pos) {
        if (pos <= 0 || text.Length == 0) return 0;
        pos = Math.Min(pos - 1, text.Length - 1);
        while (pos > 0 && char.IsWhiteSpace(text[pos])) { pos--; }
        while (pos > 0 && !char.IsWhiteSpace(text[pos - 1])) { pos--; }
        return pos;
    }

    internal static int NextWordBoundary(string text, int pos) {
        if (pos >= text.Length) return text.Length;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos])) { pos++; }
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) { pos++; }
        return pos;
    }

    // ---------------------------------------------------------------
    // Mouse input
    // ---------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Focus();
        e.Pointer.Capture(this);
        _isPointerPressed = true;
        _clickCount = e.ClickCount;

        var pos = HitTestToIndex(e.GetPosition(this));

        if (_clickCount >= 3) {
            // Triple-click: select all
            SelectAll();
        } else if (_clickCount == 2) {
            // Double-click: select word
            var text = Text ?? "";
            var wordStart = PrevWordBoundary(text, pos + 1);
            var wordEnd = NextWordBoundary(text, pos);
            SetCurrentValue(SelectionStartProperty, wordStart);
            SetCurrentValue(SelectionEndProperty, wordEnd);
            SetCurrentValue(CaretIndexProperty, wordEnd);
        } else {
            // Single click
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0) {
                SetCurrentValue(SelectionEndProperty, pos);
                SetCurrentValue(CaretIndexProperty, pos);
            } else {
                SetCurrentValue(CaretIndexProperty, pos);
                SetCurrentValue(SelectionStartProperty, pos);
                SetCurrentValue(SelectionEndProperty, pos);
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (!_isPointerPressed) return;

        var pos = HitTestToIndex(e.GetPosition(this));
        SetCurrentValue(SelectionEndProperty, pos);
        SetCurrentValue(CaretIndexProperty, pos);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (_isPointerPressed) {
            _isPointerPressed = false;
            e.Pointer.Capture(null);
        }
    }

    private int HitTestToIndex(Point pos) {
        var layout = GetLayout();
        var pad = Padding;
        var bt = BorderThickness;
        var contentHeight = Bounds.Height - bt.Top - bt.Bottom - pad.Top - pad.Bottom;
        var textY = bt.Top + pad.Top + (contentHeight - layout.Height) / 2;
        var localPoint = new Point(pos.X - bt.Left - pad.Left + _scrollOffset, pos.Y - textY);
        var hit = layout.HitTestPoint(localPoint);
        return hit.TextPosition;
    }
}
