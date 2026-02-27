using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DevMentalMd.Core.Documents;
using DevMentalMd.Rendering.Layout;

namespace DevMentalMd.App.Controls;

/// <summary>
/// Custom plain-text editing control built directly on Avalonia's DrawingContext.
/// Implements <see cref="ILogicalScrollable"/> so a parent ScrollViewer cooperates
/// with windowed layout for large documents.
/// </summary>
public sealed class EditorControl : Control, ILogicalScrollable {
    // -------------------------------------------------------------------------
    // Avalonia properties
    // -------------------------------------------------------------------------

    public static readonly StyledProperty<Document?> DocumentProperty =
        AvaloniaProperty.Register<EditorControl, Document?>(nameof(Document));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<EditorControl, FontFamily>(
            nameof(FontFamily), new FontFamily("Cascadia Code, Consolas, Courier New"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<EditorControl, double>(nameof(FontSize), 14.0);

    public static readonly StyledProperty<IBrush> ForegroundBrushProperty =
        AvaloniaProperty.Register<EditorControl, IBrush>(nameof(ForegroundBrush), Brushes.Black);

    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<EditorControl, IBrush>(
            nameof(SelectionBrush), new SolidColorBrush(Color.FromArgb(80, 0, 120, 215)));

    public static readonly StyledProperty<IBrush> CaretBrushProperty =
        AvaloniaProperty.Register<EditorControl, IBrush>(nameof(CaretBrush), Brushes.Black);

    // -------------------------------------------------------------------------
    // CLR wrappers
    // -------------------------------------------------------------------------

    public Document? Document {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public FontFamily FontFamily {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush ForegroundBrush {
        get => GetValue(ForegroundBrushProperty);
        set => SetValue(ForegroundBrushProperty, value);
    }

    public IBrush SelectionBrush {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush CaretBrush {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private readonly TextLayoutEngine _layoutEngine = new();
    private LayoutResult? _layout;
    private bool _caretVisible = true;
    private readonly DispatcherTimer _caretTimer;
    private bool _pointerDown;

    // Scroll state
    private Size _extent;
    private Size _viewport;
    private Vector _scrollOffset;
    private double _lineHeight;
    private double _renderOffsetY;
    private EventHandler? _scrollInvalidated;

    /// <summary>
    /// Documents with more logical lines than this use windowed layout —
    /// only the visible portion of the text is laid out and rendered.
    /// </summary>
    private const long WindowedLayoutThreshold = 500;

    // -------------------------------------------------------------------------
    // Public scroll API (used by DualZoneScrollBar)
    // -------------------------------------------------------------------------

    /// <summary>Fired when scroll state changes (offset, extent, or viewport).</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Maximum scroll offset (extent height − viewport height). Always ≥ 0.</summary>
    public double ScrollMaximum => Math.Max(0, _extent.Height - _viewport.Height);

    /// <summary>Current vertical scroll offset (0 .. ScrollMaximum).</summary>
    public double ScrollValue {
        get => _scrollOffset.Y;
        set {
            var clamped = Math.Clamp(value, 0, ScrollMaximum);
            var newOffset = new Vector(_scrollOffset.X, clamped);
            if (_scrollOffset != newOffset) {
                _scrollOffset = newOffset;
                _layout?.Dispose();
                _layout = null;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Viewport height in pixels.</summary>
    public double ScrollViewportHeight => _viewport.Height;

    /// <summary>Total content extent height in pixels.</summary>
    public double ScrollExtentHeight => _extent.Height;

    /// <summary>Height of a single line in pixels.</summary>
    public double LineHeightValue => GetLineHeight();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public EditorControl() {
        Focusable = true;
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += OnCaretTick;
        _caretTimer.Start();
    }

    // -------------------------------------------------------------------------
    // ILogicalScrollable
    // -------------------------------------------------------------------------

    bool ILogicalScrollable.IsLogicalScrollEnabled => true;
    bool ILogicalScrollable.CanHorizontallyScroll { get; set; }
    bool ILogicalScrollable.CanVerticallyScroll { get; set; }

    Size IScrollable.Extent => _extent;
    Size IScrollable.Viewport => _viewport;

    Vector IScrollable.Offset {
        get => _scrollOffset;
        set {
            if (_scrollOffset != value) {
                _scrollOffset = value;
                // Scroll changed — dispose the layout so it rebuilds from the new offset.
                // Do NOT call InvalidateMeasure() here (extent/viewport haven't changed).
                _layout?.Dispose();
                _layout = null;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    Size ILogicalScrollable.ScrollSize =>
        new(10, _lineHeight > 0 ? _lineHeight : 20);

    Size ILogicalScrollable.PageScrollSize =>
        new(0, Math.Max(_viewport.Height - GetLineHeight(), GetLineHeight()));

    event EventHandler? ILogicalScrollable.ScrollInvalidated {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    bool ILogicalScrollable.BringIntoView(Control target, Rect targetRect) => false;

    Control? ILogicalScrollable.GetControlInDirection(NavigationDirection direction, Control? from) => null;

    void ILogicalScrollable.RaiseScrollInvalidated(EventArgs e) =>
        _scrollInvalidated?.Invoke(this, e);

    private void RaiseScrollInvalidated() =>
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);

    // -------------------------------------------------------------------------
    // Property change hooks
    // -------------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
        if (e.Property == DocumentProperty) {
            if (e.OldValue is Document old) {
                old.Changed -= OnDocumentChanged;
            }
            if (e.NewValue is Document doc) {
                doc.Changed += OnDocumentChanged;
            }
            _scrollOffset = default; // reset scroll to top
            _lineHeight = 0;         // force line-height recomputation
            InvalidateLayout();
        } else if (e.Property == FontFamilyProperty
                   || e.Property == FontSizeProperty
                   || e.Property == ForegroundBrushProperty) {
            _lineHeight = 0;
            InvalidateLayout();
        }
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    private void InvalidateLayout() {
        _layout?.Dispose();
        _layout = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double GetLineHeight() {
        if (_lineHeight > 0) {
            return _lineHeight;
        }
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            " ", typeface, FontSize, ForegroundBrush,
            maxWidth: double.PositiveInfinity, maxHeight: double.PositiveInfinity);
        _lineHeight = tl.Height > 0 ? tl.Height : 20.0;
        return _lineHeight;
    }

    /// <summary>
    /// Builds or retrieves the current layout.
    /// For small documents (&lt; <see cref="WindowedLayoutThreshold"/> lines), the entire text is laid out.
    /// For large documents, only the visible window of text is fetched and laid out.
    /// </summary>
    private LayoutResult EnsureLayout() {
        if (_layout != null) {
            return _layout;
        }
        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var lh = GetLineHeight();
        var maxW = Bounds.Width > 0 ? Bounds.Width : 900;
        var lineCount = doc?.Table.LineCount ?? 0;

        if (lineCount > WindowedLayoutThreshold) {
            LayoutWindowed(doc!, lineCount, typeface, maxW);
        } else {
            var text = doc != null ? doc.Table.GetText() : string.Empty;
            _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, maxW);
            _extent = new Size(maxW, _layout.TotalHeight);
            _renderOffsetY = -_scrollOffset.Y;
        }
        return _layout!;
    }

    protected override Size MeasureOverride(Size availableSize) {
        _layout?.Dispose();
        _layout = null;

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var lh = GetLineHeight();
        var maxW = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        _viewport = availableSize;

        var lineCount = doc?.Table.LineCount ?? 0;

        if (lineCount > WindowedLayoutThreshold) {
            LayoutWindowed(doc!, lineCount, typeface, maxW);
        } else {
            var text = doc != null ? doc.Table.GetText() : string.Empty;
            _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, maxW);
            _extent = new Size(maxW, _layout.TotalHeight);
            _renderOffsetY = -_scrollOffset.Y;
        }

        RaiseScrollInvalidated();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
        return availableSize;
    }

    /// <summary>
    /// Fetches only the text visible in the current scroll viewport and lays it out.
    /// Sets <see cref="_layout"/>, <see cref="_extent"/>, and <see cref="_renderOffsetY"/>.
    /// </summary>
    private void LayoutWindowed(Document doc, long lineCount, Typeface typeface, double maxWidth) {
        var lh = GetLineHeight();
        var topLine = Math.Clamp((long)(_scrollOffset.Y / lh), 0, Math.Max(0, lineCount - 1));
        var visibleLines = (int)(_viewport.Height / lh) + 4; // a few extra for partial lines
        var bottomLine = Math.Min(lineCount, topLine + visibleLines);

        var startOfs = topLine > 0 ? doc.Table.LineStartOfs(topLine) : 0L;
        long endOfs;
        if (bottomLine >= lineCount) {
            // At the very bottom — need document length (expensive first time, cached after).
            endOfs = doc.Table.Length;
        } else {
            endOfs = doc.Table.LineStartOfs(bottomLine);
        }

        var len = (int)(endOfs - startOfs);
        var text = len > 0 ? doc.Table.GetText(startOfs, len) : string.Empty;

        _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, maxWidth, startOfs);
        _extent = new Size(maxWidth, lineCount * lh);
        _renderOffsetY = topLine * lh - _scrollOffset.Y;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext context) {
        var layout = EnsureLayout();
        var doc = Document;

        // Background
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

        // Draw selection rectangles behind text
        if (doc != null && !doc.Selection.IsEmpty) {
            DrawSelection(context, layout, doc.Selection);
        }

        // Draw each visible line's text
        foreach (var line in layout.Lines) {
            var y = line.Y + _renderOffsetY;
            if (y + line.Height < 0) {
                continue; // above viewport
            }
            if (y > Bounds.Height) {
                break; // below viewport
            }
            line.Layout.Draw(context, new Point(0, y));
        }

        // Draw caret
        if (doc != null && _caretVisible && IsFocused) {
            DrawCaret(context, layout, doc.Selection.Caret);
        }
    }

    private void DrawSelection(DrawingContext context, LayoutResult layout, Selection sel) {
        var localStart = (int)(sel.Start - layout.ViewportBase);
        var localEnd = (int)(sel.End - layout.ViewportBase);

        // Clamp to the visible range
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (localEnd < 0 || localStart > totalChars) {
            return;
        }
        localStart = Math.Clamp(localStart, 0, totalChars);
        localEnd = Math.Clamp(localEnd, 0, totalChars);
        if (localStart == localEnd) {
            return;
        }

        var startRects = _layoutEngine.GetCaretBounds(localStart, layout);
        var endRects = _layoutEngine.GetCaretBounds(localEnd, layout);

        if (Math.Abs(startRects.Y - endRects.Y) < 1.0) {
            var rect = new Rect(
                startRects.X,
                startRects.Y + _renderOffsetY,
                endRects.X - startRects.X,
                startRects.Height);
            context.FillRectangle(SelectionBrush, rect);
        } else {
            foreach (var line in layout.Lines) {
                var lineY = line.Y + _renderOffsetY;
                if (lineY + line.Height < 0) {
                    continue;
                }
                if (lineY > Bounds.Height) {
                    break;
                }
                if (line.Y + line.Height <= startRects.Y) {
                    continue;
                }
                if (line.Y >= endRects.Y + endRects.Height) {
                    break;
                }
                var lineStart = line.CharStart >= localStart ? 0.0 : startRects.X;
                var lineEnd = line.CharEnd <= localEnd ? Bounds.Width : endRects.X;
                var rect = new Rect(lineStart, lineY, lineEnd - lineStart, line.Height);
                context.FillRectangle(SelectionBrush, rect);
            }
        }
    }

    private void DrawCaret(DrawingContext context, LayoutResult layout, long caretOfs) {
        var localCaret = (int)(caretOfs - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        if (localCaret < 0 || localCaret > totalChars) {
            return; // caret is outside the visible window
        }
        var rect = _layoutEngine.GetCaretBounds(localCaret, layout);
        var y = rect.Y + _renderOffsetY;
        if (y + rect.Height < 0 || y > Bounds.Height) {
            return;
        }
        context.FillRectangle(CaretBrush, new Rect(rect.X, y, 1.5, rect.Height));
    }

    // -------------------------------------------------------------------------
    // Caret blink
    // -------------------------------------------------------------------------

    private void OnCaretTick(object? sender, EventArgs e) {
        _caretVisible = !_caretVisible;
        InvalidateVisual();
    }

    private void ResetCaretBlink() {
        _caretVisible = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // Keyboard input
    // -------------------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        var doc = Document;
        if (doc == null || string.IsNullOrEmpty(e.Text)) {
            return;
        }
        doc.Insert(e.Text);
        e.Handled = true;
        InvalidateLayout();
        ResetCaretBlink();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        var doc = Document;
        if (doc == null) {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key) {
            case Key.Back:
                doc.DeleteBackward();
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Delete:
                doc.DeleteForward();
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Left:
                MoveCaretHorizontal(doc, -1, ctrl, shift);
                e.Handled = true;
                break;

            case Key.Right:
                MoveCaretHorizontal(doc, +1, ctrl, shift);
                e.Handled = true;
                break;

            case Key.Up:
                MoveCaretVertical(doc, -1, shift);
                e.Handled = true;
                break;

            case Key.Down:
                MoveCaretVertical(doc, +1, shift);
                e.Handled = true;
                break;

            case Key.Home:
                MoveCaretToLineEdge(doc, toStart: true, shift);
                e.Handled = true;
                break;

            case Key.End:
                MoveCaretToLineEdge(doc, toStart: false, shift);
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                doc.Undo();
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                doc.Redo();
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.A when ctrl:
                doc.Selection = new Selection(0L, doc.Table.Length);
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Return:
                doc.Insert("\n");
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Tab:
                doc.Insert("    ");
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.PageUp:
                ScrollByPage(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
                ScrollByPage(+1);
                e.Handled = true;
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Caret movement helpers
    // -------------------------------------------------------------------------

    private void MoveCaretHorizontal(Document doc, int delta, bool byWord, bool extend) {
        var caret = doc.Selection.Caret;
        var len = doc.Table.Length;
        long newCaret;

        if (!byWord) {
            newCaret = Math.Clamp(caret + delta, 0L, len);
        } else {
            newCaret = delta < 0
                ? FindWordBoundaryLeft(doc, caret)
                : FindWordBoundaryRight(doc, caret);
        }

        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void MoveCaretVertical(Document doc, int lineDelta, bool extend) {
        var layout = EnsureLayout();
        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;

        // If the caret is outside the visible window, skip the movement.
        if (localCaret < 0 || localCaret > totalChars) {
            return;
        }

        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout);
        var targetY = caretRect.Y + caretRect.Height / 2 + lineDelta * caretRect.Height;
        var localNewCaret = _layoutEngine.HitTest(new Point(caretRect.X, targetY), layout);
        var newCaret = layout.ViewportBase + localNewCaret;
        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void MoveCaretToLineEdge(Document doc, bool toStart, bool extend) {
        var layout = EnsureLayout();
        var lineIdx = FindLineIndexForOfs(doc.Selection.Caret, layout);
        if (lineIdx < 0) {
            return;
        }
        var line = layout.Lines[lineIdx];
        var newCaret = toStart
            ? layout.ViewportBase + line.CharStart
            : layout.ViewportBase + line.CharEnd;
        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        InvalidateVisual();
        ResetCaretBlink();
    }

    private static long FindWordBoundaryLeft(Document doc, long caret) {
        if (caret == 0L) {
            return 0L;
        }
        // Use a 1 KB window around the caret — avoids materializing the full document.
        var windowStart = Math.Max(0L, caret - 1024);
        var windowLen = (int)(caret - windowStart);
        var text = doc.Table.GetText(windowStart, windowLen);
        var pos = windowLen; // position within the window
        // Skip whitespace going left, then skip non-whitespace
        while (pos > 0 && char.IsWhiteSpace(text[pos - 1])) {
            pos--;
        }
        while (pos > 0 && !char.IsWhiteSpace(text[pos - 1])) {
            pos--;
        }
        return windowStart + pos;
    }

    private static long FindWordBoundaryRight(Document doc, long caret) {
        var len = doc.Table.Length;
        if (caret >= len) {
            return len;
        }
        var windowLen = (int)Math.Min(1024L, len - caret);
        var text = doc.Table.GetText(caret, windowLen);
        var pos = 0;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) {
            pos++;
        }
        while (pos < text.Length && !char.IsWhiteSpace(text[pos])) {
            pos++;
        }
        return caret + pos;
    }

    private static int FindLineIndexForOfs(long charOfs, LayoutResult layout) {
        var localOfs = (int)(charOfs - layout.ViewportBase);
        var lines = layout.Lines;
        // If the caret is outside the visible window, return -1.
        if (lines.Count == 0 || localOfs < 0) {
            return -1;
        }
        for (var i = lines.Count - 1; i >= 0; i--) {
            if (lines[i].CharStart <= localOfs) {
                return i;
            }
        }
        return 0;
    }

    // -------------------------------------------------------------------------
    // Mouse / pointer input
    // -------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        // Force caret visible (pause blinking) while any button is held.
        _caretVisible = true;
        _caretTimer.Stop();
        InvalidateVisual();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed) {
            return; // middle-click is reserved for scrollbar drag
        }
        Focus();
        var doc = Document;
        if (doc == null) {
            return;
        }
        var layout = EnsureLayout();
        var pt = e.GetPosition(this);
        var layoutPt = new Point(pt.X, pt.Y - _renderOffsetY);
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;
        var isLeft = props.IsLeftButtonPressed;
        // Left-click: place caret or extend selection (with Shift).
        // Right-click: place caret only (no Shift-extend, no drag-select).
        if (isLeft) {
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            doc.Selection = shift
                ? doc.Selection.ExtendTo(ofs)
                : Selection.Collapsed(ofs);
            _pointerDown = true;
            e.Pointer.Capture(this);
        } else {
            doc.Selection = Selection.Collapsed(ofs);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (!_pointerDown) {
            return; // only left-drag extends selection
        }
        var doc = Document;
        if (doc == null) {
            return;
        }
        var layout = EnsureLayout();
        var pt = e.GetPosition(this);
        var layoutPt = new Point(pt.X, pt.Y - _renderOffsetY);
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;
        doc.Selection = doc.Selection.ExtendTo(ofs);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        _pointerDown = false;
        e.Pointer.Capture(null);
        // Hide caret immediately; the blink timer will show it on its next tick.
        _caretVisible = false;
        _caretTimer.Stop();
        _caretTimer.Start();
        InvalidateVisual();
    }

    protected override void OnGotFocus(GotFocusEventArgs e) {
        base.OnGotFocus(e);
        ResetCaretBlink();
    }

    protected override void OnLostFocus(RoutedEventArgs e) {
        base.OnLostFocus(e);
        _caretVisible = false;
        InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // Mouse wheel scrolling (replaces ScrollViewer)
    // -------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        var lh = GetLineHeight();
        var delta = -e.Delta.Y * lh * 3; // 3 lines per wheel notch
        ScrollValue += delta;
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls by one page (viewport minus one line, rounded down to whole lines).
    /// </summary>
    private void ScrollByPage(int direction) {
        var lh = GetLineHeight();
        var pageSize = _viewport.Height - lh;
        if (pageSize < lh) {
            pageSize = lh;
        }
        pageSize = Math.Floor(pageSize / lh) * lh;
        ScrollValue += direction * pageSize;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) {
        InvalidateLayout();
    }
}
