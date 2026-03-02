using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DevMentalMd.Core.Buffers;
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
    private bool _middleDrag;
    private double _middleDragStartY;

    /// <summary>Optional reference to the scrollbar for middle-drag visual feedback.</summary>
    public DualZoneScrollBar? ScrollBar { get; set; }

    // Scroll state
    private Size _extent;
    private Size _viewport;
    private Vector _scrollOffset;
    private double _rowHeight;
    private double _charWidth;
    private double _renderOffsetY;
    private EventHandler? _scrollInvalidated;

    // Incremental scroll tracking — used by LayoutWindowed to produce
    // pixel-perfect smooth scrolling even when topLine changes.
    private long _winTopLine = -1;
    private double _winScrollOffset;
    private double _winRenderOffsetY;
    private double _winFirstLineHeight;

    // -------------------------------------------------------------------------
    // Performance stats
    // -------------------------------------------------------------------------

    /// <summary>
    /// Tracks timing statistics with exponential moving average, min, and max.
    /// </summary>
    public sealed class TimingStat {
        private const double Alpha = 0.1; // EMA smoothing factor
        public double Avg { get; private set; }
        public double Min { get; private set; } = double.MaxValue;
        public double Max { get; private set; }
        public int Count { get; private set; }

        public void Record(double ms) {
            Count++;
            if (Count == 1) {
                Avg = ms;
            } else {
                Avg = Alpha * ms + (1 - Alpha) * Avg;
            }
            if (ms < Min) Min = ms;
            if (ms > Max) Max = ms;
        }

        public void Reset() {
            Avg = 0;
            Min = double.MaxValue;
            Max = 0;
            Count = 0;
        }

        public string Format() =>
            Count == 0 ? "—" : $"{Avg:F2}ms ({Min:F2}–{Max:F2})";
    }

    /// <summary>Performance statistics exposed for the stats panel.</summary>
    public sealed class PerfStatsData {
        public TimingStat Layout { get; } = new();
        public TimingStat Render { get; } = new();
        public TimingStat Edit { get; } = new();
        public long LogicalLines { get; set; }
        public int ViewportLines { get; set; }
        public int ViewportRows { get; set; }
        public double ScrollPercent { get; set; }
        /// <summary>Time from open to first renderable chunk (streaming loads only).</summary>
        public double FirstChunkTimeMs { get; set; }
        /// <summary>Total time from open to fully loaded.</summary>
        public double LoadTimeMs { get; set; }
        public double SaveTimeMs { get; set; }
        /// <summary>Current GC memory in MB.</summary>
        public double MemoryMb { get; set; }
        /// <summary>Peak GC memory seen this session in MB.</summary>
        public double PeakMemoryMb { get; set; }

        /// <summary>Samples current GC memory and updates peak.</summary>
        public void SampleMemory() {
            MemoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            if (MemoryMb > PeakMemoryMb) {
                PeakMemoryMb = MemoryMb;
            }
        }

        public void Reset() {
            Layout.Reset();
            Render.Reset();
            Edit.Reset();
        }
    }

    private readonly Stopwatch _perfSw = new();
    private readonly Stopwatch _editSw = new();

    /// <summary>Live performance stats. Updated after each render.</summary>
    public PerfStatsData PerfStats { get; } = new();

    /// <summary>Fired after each render with updated stats.</summary>
    public event Action? StatsUpdated;

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
                // Hide caret during scroll. For drags the !scrollDrag render
                // guard suppresses drawing; InteractionEnded shows it on
                // release. For wheel/arrow the timer naturally recovers.
                _caretVisible = false;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Viewport height in pixels.</summary>
    public double ScrollViewportHeight => _viewport.Height;

    /// <summary>Total content extent height in pixels.</summary>
    public double ScrollExtentHeight => _extent.Height;

    /// <summary>Height of a single visual row in pixels.</summary>
    public double RowHeightValue => GetRowHeight();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public EditorControl() {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
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
        new(10, _rowHeight > 0 ? _rowHeight : 20);

    Size ILogicalScrollable.PageScrollSize =>
        new(0, Math.Max(_viewport.Height - GetRowHeight(), GetRowHeight()));

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
            _rowHeight = 0;         // force line-height recomputation
            _charWidth = 0;
            PerfStats.Reset();
            InvalidateLayout();
        } else if (e.Property == FontFamilyProperty
                     || e.Property == FontSizeProperty
                     || e.Property == ForegroundBrushProperty) {
            _rowHeight = 0;
            _charWidth = 0;
            InvalidateLayout();
        }
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invalidates the cached layout, forcing a rebuild on the next render pass.
    /// Public so that <c>MainWindow</c> can trigger re-layout when a streaming buffer
    /// reports progress.
    /// </summary>
    public void InvalidateLayout() {
        _layout?.Dispose();
        _layout = null;
        _winTopLine = -1; // reset incremental scroll (content changed)
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double GetRowHeight() {
        if (_rowHeight > 0) {
            return _rowHeight;
        }
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            " ", typeface, FontSize, ForegroundBrush,
            maxWidth: double.PositiveInfinity, maxHeight: double.PositiveInfinity);
        _rowHeight = tl.Height > 0 ? tl.Height : 20.0;
        return _rowHeight;
    }

    private double GetCharWidth() {
        if (_charWidth > 0) {
            return _charWidth;
        }
        var typeface = new Typeface(FontFamily);
        using var tl = new TextLayout(
            "0", typeface, FontSize, ForegroundBrush,
            maxWidth: double.PositiveInfinity, maxHeight: double.PositiveInfinity);
        _charWidth = tl.WidthIncludingTrailingWhitespace > 0 ? tl.WidthIncludingTrailingWhitespace : 8.0;
        return _charWidth;
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
        _perfSw.Restart();

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
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

        _perfSw.Stop();
        PerfStats.Layout.Record(_perfSw.Elapsed.TotalMilliseconds);
        PerfStats.LogicalLines = lineCount;
        PerfStats.ViewportLines = _layout?.Lines.Count ?? 0;
        PerfStats.ViewportRows = CountVisualRows(_layout);
        PerfStats.ScrollPercent = _extent.Height > _viewport.Height
            ? _scrollOffset.Y / (_extent.Height - _viewport.Height) * 100
            : 0;

        return _layout!;
    }

    private static int CountVisualRows(LayoutResult? layout) {
        if (layout == null) return 0;
        var rh = 0.0;
        var rows = 0;
        foreach (var line in layout.Lines) {
            // First line establishes the single-row height
            if (rh <= 0 && line.Height > 0) rh = line.Layout.Height > 0 ? GetSingleRowHeight(line) : line.Height;
            rows += rh > 0 ? Math.Max(1, (int)Math.Round(line.Height / rh)) : 1;
        }
        return rows;
    }

    private static double GetSingleRowHeight(LayoutLine line) {
        // TextLayout.Height for a single unwrapped line is the row height.
        // For a wrapped line, we need the height of one row.
        // TextLayout stores line metrics — approximate by dividing total height
        // by the number of visual lines it produces.
        var textLines = line.Layout.TextLines;
        return textLines.Count > 0 ? line.Height / textLines.Count : line.Height;
    }

    protected override Size MeasureOverride(Size availableSize) {
        _layout?.Dispose();
        _layout = null;

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
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
    /// <remarks>
    /// The scroll math estimates total visual rows from character count and character width,
    /// so that the extent accounts for word-wrap and the scroll-to-line mapping works in
    /// visual-row units. For monospace fonts this is exact; for proportional fonts it's a
    /// close approximation.
    /// </remarks>
    private void LayoutWindowed(Document doc, long lineCount, Typeface typeface, double maxWidth) {
        var rh = GetRowHeight();
        var cw = GetCharWidth();
        var charsPerRow = Math.Max(1, (int)(maxWidth / cw));

        // Estimate total visual rows from document character count
        var totalChars = doc.Table.Length;
        var totalVisualRows = Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow));

        // Average visual rows per logical line → average height per logical line
        var avgRowsPerLine = Math.Max(1.0, (double)totalVisualRows / lineCount);
        var avgLineHeight = avgRowsPerLine * rh;

        // Map scroll offset → top logical line using the average height
        var topLine = Math.Clamp((long)(_scrollOffset.Y / avgLineHeight), 0, Math.Max(0, lineCount - 1));

        // For single-row scrolls (arrow buttons), constrain topLine to change
        // by at most ±1 from the previous frame.  This lets the incremental
        // render-offset logic use the actual cached line height instead of
        // avgLineHeight, giving pixel-perfect smooth scrolling.
        // Everything else (wheel, drag, page-down) uses the formula-based
        // topLine directly — the threshold of 2*rh cleanly separates arrow
        // clicks (ds = rh) from wheel notches (ds = 3*rh).
        var isSmallScroll = _winTopLine >= 0
            && Math.Abs(_scrollOffset.Y - _winScrollOffset) < 2 * rh;

        if (isSmallScroll) {
            var ds = _scrollOffset.Y - _winScrollOffset;

            if (topLine > _winTopLine) {
                // Would advance.  Only allow +1, and only when the first
                // line is fully scrolled above the viewport.
                var firstLineBottom = _winRenderOffsetY - ds + _winFirstLineHeight;
                if (firstLineBottom > 0) {
                    topLine = _winTopLine;      // not fully off-screen yet
                } else {
                    topLine = _winTopLine + 1;  // advance by exactly 1
                }
            } else if (topLine < _winTopLine) {
                topLine = _winTopLine - 1;      // retreat by exactly 1
            }

            // If topLine stayed the same but scrolling up would leave a gap
            // (render offset goes positive), retreat to include the previous line.
            if (topLine == _winTopLine) {
                var wouldBeOffset = _winRenderOffsetY - ds;
                if (wouldBeOffset > 0 && topLine > 0) {
                    topLine--;
                }
            }
        }

        // Fetch enough lines to fill the viewport (+ buffer for partial rows)
        var visibleRows = (int)(_viewport.Height / rh) + 4;
        var bottomLine = Math.Min(lineCount, topLine + visibleRows);

        var startOfs = topLine > 0 ? doc.Table.LineStartOfs(topLine) : 0L;
        long endOfs;
        if (bottomLine >= lineCount) {
            endOfs = doc.Table.Length;
        } else {
            endOfs = doc.Table.LineStartOfs(bottomLine);
        }

        // During streaming/paged loads, LineStartOfs returns -1 when the
        // required page isn't in memory yet. Layout empty text — the next
        // ProgressChanged event will trigger re-layout once data is available.
        if (startOfs < 0 || endOfs < 0) {
            _layout = _layoutEngine.Layout(string.Empty, typeface, FontSize, ForegroundBrush, maxWidth, 0);
            _extent = new Size(maxWidth, totalVisualRows * rh);
            _renderOffsetY = 0;
            return;
        }

        var len = (int)(endOfs - startOfs);

        // If the underlying buffer has evicted pages for this range, kick off
        // async page loads and layout empty text.  ProgressChanged will trigger
        // a re-layout once the data arrives.
        if (len > 0 && doc.Table.OrigBuffer is { } origBuf && !origBuf.IsLoaded(startOfs, len)) {
            origBuf.EnsureLoaded(startOfs, len);
            _layout = _layoutEngine.Layout(string.Empty, typeface, FontSize, ForegroundBrush, maxWidth, 0);
            _extent = new Size(maxWidth, totalVisualRows * rh);
            _renderOffsetY = 0;
            return;
        }

        var text = len > 0 ? doc.Table.GetText(startOfs, len) : string.Empty;

        _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, maxWidth, startOfs);
        _extent = new Size(maxWidth, totalVisualRows * rh);

        // Compute render offset.  For small topLine changes (arrow keys, wheel)
        // use an incremental offset based on the actual cached line height so
        // each rh of scroll produces exactly rh of visual movement.
        // For large jumps (thumb drag, page-down) use the formula estimate.
        var deltaTop = topLine - _winTopLine;

        if (_winTopLine >= 0 && deltaTop == 0) {
            // topLine unchanged — pure scroll, trivially smooth.
            _renderOffsetY = _winRenderOffsetY - (_scrollOffset.Y - _winScrollOffset);
        } else if (_winTopLine >= 0 && deltaTop == 1 && _winFirstLineHeight > 0) {
            // topLine advanced by 1.  Compensate for the departed line's
            // actual height (not avgLineHeight) to avoid a visual jump.
            _renderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                + _winFirstLineHeight;
        } else if (_winTopLine >= 0 && deltaTop == -1 && _layout.Lines.Count > 0) {
            // topLine retreated by 1.  The new first line was prepended;
            // subtract its actual height to keep content in place.
            _renderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                - _layout.Lines[0].Height;
        } else {
            // First layout or large jump — use formula estimate.
            _renderOffsetY = topLine * avgLineHeight - _scrollOffset.Y;
        }

        // Safety: prevent any remaining gap at the viewport top.
        if (_renderOffsetY > 0) {
            _renderOffsetY = 0;
        }

        // Safety: prevent gap at the viewport bottom.  If the layout has
        // enough content to fill the viewport but the render offset pushes
        // it too far above, snap it down so the content reaches the bottom.
        if (_layout.TotalHeight >= _viewport.Height) {
            var contentBottom = _renderOffsetY + _layout.TotalHeight;
            if (contentBottom < _viewport.Height) {
                _renderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // When at max scroll and the layout includes the end of the document,
        // anchor content bottom to viewport bottom so the last line is visible.
        // Only at max scroll — otherwise scrolling up from the bottom would be stuck.
        var scrollMax = _extent.Height - _viewport.Height;
        if (bottomLine >= lineCount && _scrollOffset.Y >= scrollMax - 1.0) {
            var contentBottom = _renderOffsetY + _layout.TotalHeight;
            if (contentBottom > _viewport.Height) {
                _renderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // Cache state for next incremental frame.
        _winTopLine = topLine;
        _winScrollOffset = _scrollOffset.Y;
        _winRenderOffsetY = _renderOffsetY;
        _winFirstLineHeight = _layout.Lines.Count > 0 ? _layout.Lines[0].Height : rh;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext context) {
        _perfSw.Restart();

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

        // Draw caret (hidden during any scroll-drag operation)
        var scrollDrag = ScrollBar?.IsDragging ?? false;
        if (doc != null && _caretVisible && IsFocused && !_middleDrag && !scrollDrag) {
            DrawCaret(context, layout, doc.Selection.Caret);
        }

        _perfSw.Stop();
        PerfStats.Render.Record(_perfSw.Elapsed.TotalMilliseconds);
        PerfStats.SampleMemory();
        StatsUpdated?.Invoke();
    }

    private const double SelCornerRadius = 3.0;

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

        // Collect all selection rects so we know first/last for corner rounding.
        var rects = new List<Rect>();
        foreach (var line in layout.Lines) {
            var lineY = line.Y + _renderOffsetY;
            if (lineY + line.Height < 0) {
                continue;
            }
            if (lineY > Bounds.Height) {
                break;
            }
            if (line.CharEnd <= localStart || line.CharStart >= localEnd) {
                continue;
            }

            var rangeStart = Math.Max(0, localStart - line.CharStart);
            var rangeEnd = Math.Min(line.CharLen, localEnd - line.CharStart);
            var rangeLen = rangeEnd - rangeStart;
            if (rangeLen <= 0) {
                continue;
            }

            foreach (var rect in line.Layout.HitTestTextRange(rangeStart, rangeLen)) {
                rects.Add(new Rect(rect.X, lineY + rect.Y, rect.Width, rect.Height));
            }
        }

        if (rects.Count == 0) {
            return;
        }

        // Round a corner only when it is truly "exposed" — the adjacent
        // rect doesn't cover it.  A corner is covered when the neighbour
        // extends at least as far in that direction.
        //   TL: covered when above.Left  <= cur.Left   (above reaches left)
        //   TR: covered when above.Right >= cur.Right   (above reaches right)
        //   BL: covered when below.Left  <= cur.Left
        //   BR: covered when below.Right >= cur.Right
        const double r = SelCornerRadius;
        const double eps = 0.5;
        for (var i = 0; i < rects.Count; i++) {
            var cur = rects[i];
            var hasAbove = i > 0;
            var hasBelow = i < rects.Count - 1;
            var above = hasAbove ? rects[i - 1] : default;
            var below = hasBelow ? rects[i + 1] : default;

            var tl = !hasAbove || above.Left  > cur.Left  + eps ? r : 0;
            var tr = !hasAbove || above.Right < cur.Right - eps ? r : 0;
            var bl = !hasBelow || below.Left  > cur.Left  + eps ? r : 0;
            var br = !hasBelow || below.Right < cur.Right - eps ? r : 0;

            FillRoundedRect(context, SelectionBrush, cur, tl, tr, br, bl);
        }
    }

    /// <summary>Fills a rectangle with individually rounded corners.</summary>
    private static void FillRoundedRect(
        DrawingContext ctx, IBrush brush, Rect r,
        double tl, double tr, double br, double bl) {
        if (tl == 0 && tr == 0 && br == 0 && bl == 0) {
            ctx.FillRectangle(brush, r);
            return;
        }
        var g = new StreamGeometry();
        using (var c = g.Open()) {
            c.BeginFigure(new Point(r.Left + tl, r.Top), true);
            c.LineTo(new Point(r.Right - tr, r.Top));
            if (tr > 0) {
                c.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Right, r.Bottom - br));
            if (br > 0) {
                c.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Left + bl, r.Bottom));
            if (bl > 0) {
                c.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise);
            }
            c.LineTo(new Point(r.Left, r.Top + tl));
            if (tl > 0) {
                c.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise);
            }
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
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
        if (_middleDrag) {
            return;
        }
        _caretVisible = !_caretVisible;
        InvalidateVisual();
    }

    public void ResetCaretBlink() {
        if (_middleDrag) {
            return;
        }
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
        _editSw.Restart();
        doc.Insert(e.Text);
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
                _editSw.Restart();
                doc.DeleteBackward();
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Delete:
                _editSw.Restart();
                doc.DeleteForward();
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) {
                    _editSw.Restart();
                    doc.MoveLineUp();
                    _editSw.Stop();
                    PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                    InvalidateLayout();
                    ResetCaretBlink();
                } else {
                    MoveCaretVertical(doc, -1, shift);
                }
                e.Handled = true;
                break;

            case Key.Down:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) {
                    _editSw.Restart();
                    doc.MoveLineDown();
                    _editSw.Stop();
                    PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                    InvalidateLayout();
                    ResetCaretBlink();
                } else {
                    MoveCaretVertical(doc, +1, shift);
                }
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
                _editSw.Restart();
                if (shift) {
                    doc.Redo();
                } else {
                    doc.Undo();
                }
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                _editSw.Restart();
                doc.DeleteLine();
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.A when ctrl:
                doc.Selection = new Selection(0L, doc.Table.Length);
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.W when ctrl:
                doc.SelectWord();
                InvalidateVisual();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.U when ctrl && shift:
                _editSw.Restart();
                doc.TransformCase(CaseTransform.Upper);
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.L when ctrl && shift:
                _editSw.Restart();
                doc.TransformCase(CaseTransform.Lower);
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.P when ctrl && shift:
                _editSw.Restart();
                doc.TransformCase(CaseTransform.Proper);
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Return:
                _editSw.Restart();
                doc.Insert("\n");
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                InvalidateLayout();
                ResetCaretBlink();
                e.Handled = true;
                break;

            case Key.Tab:
                _editSw.Restart();
                doc.Insert("    ");
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
            _middleDrag = true;
            _middleDragStartY = e.GetPosition(this).Y;
            Cursor = new Cursor(StandardCursorType.None);
            ScrollBar?.BeginExternalMiddleDrag();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
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
        if (_middleDrag) {
            var deltaY = e.GetPosition(this).Y - _middleDragStartY;
            ScrollBar?.UpdateExternalMiddleDrag(deltaY);
            return;
        }
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
        if (_middleDrag) {
            _middleDrag = false;
            Cursor = new Cursor(StandardCursorType.Ibeam);
            ScrollBar?.EndExternalMiddleDrag();
            e.Pointer.Capture(null);
            ResetCaretBlink();
            return;
        }
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
        var rh = GetRowHeight();
        var delta = -e.Delta.Y * rh * 3; // 3 rows per wheel notch
        ScrollValue += delta;
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls by one page (viewport minus one line, rounded down to whole lines).
    /// </summary>
    private void ScrollByPage(int direction) {
        var rh = GetRowHeight();
        var pageSize = _viewport.Height - rh;
        if (pageSize < rh) {
            pageSize = rh;
        }
        pageSize = Math.Floor(pageSize / rh) * rh;
        ScrollValue += direction * pageSize;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) {
        InvalidateLayout();
    }
}
