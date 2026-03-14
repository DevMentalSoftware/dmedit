using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DevMentalMd.App.Services;
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
        AvaloniaProperty.Register<EditorControl, double>(nameof(FontSize), 11.ToPixels());

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

    /// <summary>
    /// When true, the editor ignores text input, keyboard commands, and
    /// mouse editing. Set while the tab's file is still loading.
    /// </summary>
    public bool IsInputBlocked { get; set; }

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

    /// <summary>
    /// Pixel X coordinate to aim for when pressing Up/Down.  Set on the first
    /// vertical move and preserved across consecutive vertical moves so the
    /// caret returns to the original column after traversing short lines.
    /// Reset to <c>-1</c> by any non-vertical caret action (typing, left/right,
    /// Home/End, click, etc.).
    /// </summary>
    private double _preferredCaretX = -1;

    // Edit coalescing: groups consecutive similar edits into single undo
    // entries.  Uses a string "coalesce key" to identify the edit type and
    // an idle timer that flushes after a pause.  The timer restarts on every
    // edit so continuous typing never fires it — only actual pauses.
    private string? _coalesceKey;
    private bool _compoundOpen;
    private readonly DispatcherTimer _coalesceTimer;

    /// <summary>
    /// Idle time (ms) before consecutive edits are committed as a single undo
    /// entry. Continuous typing resets the timer on every keystroke. Default: 1000.
    /// </summary>
    public int CoalesceTimerMs {
        get => (int)_coalesceTimer.Interval.TotalMilliseconds;
        set => _coalesceTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, value));
    }

    /// <summary>Optional reference to the scrollbar for middle-drag visual feedback.</summary>
    public DualZoneScrollBar? ScrollBar { get; set; }

    /// <summary>Whether long lines wrap at the viewport edge.</summary>
    public bool WrapLines {
        get => _wrapLines;
        set {
            if (_wrapLines == value) return;
            _wrapLines = value;
            InvalidateLayout();
        }
    }

    /// <summary>
    /// Maximum columns before wrapping. Wrapping occurs at this limit or the
    /// viewport edge, whichever is narrower. Only effective when
    /// <see cref="WrapLines"/> is true. Values &lt; 1 are treated as unlimited.
    /// </summary>
    public int WrapLinesAt {
        get => _wrapLinesAt;
        set {
            if (_wrapLinesAt == value) return;
            _wrapLinesAt = value;
            InvalidateLayout();
        }
    }

    /// <summary>Controls the hierarchy of levels used by Expand Selection.</summary>
    public ExpandSelectionMode ExpandSelectionMode { get; set; } = ExpandSelectionMode.SubwordFirst;

    /// <summary>Whether line numbers are displayed in a gutter on the left.</summary>
    public bool ShowLineNumbers {
        get => _showLineNumbers;
        set {
            if (_showLineNumbers == value) return;
            _showLineNumbers = value;
            InvalidateLayout();
        }
    }

    /// <summary>Applies a new theme, pushing colors into styled properties and fields.</summary>
    public void ApplyTheme(EditorTheme theme) {
        _theme = theme;
        ForegroundBrush = theme.EditorForeground;
        SelectionBrush = theme.SelectionBrush;
        CaretBrush = theme.CaretBrush;
        InvalidateVisual();
    }

    // Scroll state
    private Size _extent;
    private Size _viewport;
    private Vector _scrollOffset;
    private double _rowHeight;
    private double _charWidth;
    private double _renderOffsetY;
    private EventHandler? _scrollInvalidated;

    // Word wrap
    private bool _wrapLines = true;
    private int _wrapLinesAt = 100;

    // Line number gutter
    private bool _showLineNumbers = true;
    private double _gutterWidth;
    private int _gutterDigitCnt;
    private const double GutterPadLeft = 4;
    private const double GutterPadRight = 12;

    // Theme — set by MainWindow when the effective theme changes.
    private EditorTheme _theme = EditorTheme.Light;

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
    public event Action? StatusUpdated;

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
        _coalesceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _coalesceTimer.Tick += (_, _) => FlushCompound();
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
    /// Recomputes <see cref="_gutterWidth"/> and <see cref="_gutterDigitCnt"/>
    /// based on the current document's line count and font metrics.
    /// </summary>
    private void UpdateGutterWidth() {
        if (!_showLineNumbers) {
            _gutterWidth = GutterPadLeft;
            _gutterDigitCnt = 0;
            return;
        }
        var lineCount = Document?.Table.LineCount ?? 1;
        if (lineCount < 1) lineCount = 1;
        _gutterDigitCnt = Math.Max(2, (int)Math.Floor(Math.Log10(lineCount)) + 1);
        _gutterWidth = GutterPadLeft + _gutterDigitCnt * GetCharWidth() + GutterPadRight;
    }

    /// <summary>
    /// Computes the effective text-area width passed to the layout engine.
    /// When wrapping is off returns <see cref="double.PositiveInfinity"/>.
    /// When wrapping is on, returns the lesser of the viewport width and
    /// the <see cref="WrapLinesAt"/> column limit (in pixels).
    /// </summary>
    private double GetTextWidth(double extentWidth) {
        if (!_wrapLines) return double.PositiveInfinity;
        if (_wrapLinesAt >= 1) {
            var colLimit = _wrapLinesAt * GetCharWidth();
            return Math.Min(extentWidth, colLimit);
        }
        return extentWidth;
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
        UpdateGutterWidth();
        var boundsW = Bounds.Width > 0 ? Bounds.Width : 900;
        var extentW = Math.Max(100, boundsW - _gutterWidth);
        var textW = GetTextWidth(extentW);
        var lineCount = doc?.Table.LineCount ?? 0;

        if (lineCount > WindowedLayoutThreshold) {
            LayoutWindowed(doc!, lineCount, typeface, textW, extentW);
        } else {
            var text = doc != null ? doc.Table.GetText() : string.Empty;
            _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, textW);
            _extent = new Size(extentW, _layout.TotalHeight);
            _renderOffsetY = -_scrollOffset.Y;
        }

        _perfSw.Stop();
        PerfStats.Layout.Record(_perfSw.Elapsed.TotalMilliseconds);
        
        PerfStats.ViewportLines = _layout?.Lines.Count ?? 0;
        PerfStats.ViewportRows = _layout?.TotalRows ?? 0;
        PerfStats.ScrollPercent = _extent.Height > _viewport.Height
            ? _scrollOffset.Y / (_extent.Height - _viewport.Height) * 100
            : 0;

        return _layout!;
    }

    protected override Size MeasureOverride(Size availableSize) {
        _layout?.Dispose();
        _layout = null;

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var extentW = double.IsInfinity(availableSize.Width)
            ? 0
            : Math.Max(100, availableSize.Width - _gutterWidth);
        var textW = GetTextWidth(extentW);
        _viewport = availableSize;

        var lineCount = doc?.Table.LineCount ?? 0;

        if (lineCount > WindowedLayoutThreshold) {
            LayoutWindowed(doc!, lineCount, typeface, textW, extentW);
        } else {
            var text = doc != null ? doc.Table.GetText() : string.Empty;
            _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, textW);
            _extent = new Size(extentW, _layout.TotalHeight);
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
    private void LayoutWindowed(Document doc, long lineCount, Typeface typeface, double maxWidth, double extentWidth) {
        var rh = GetRowHeight();
        var cw = GetCharWidth();

        // Estimate total visual rows from document character count.
        // When wrapping is off (maxWidth = infinity), each line = 1 row.
        var totalChars = doc.Table.Length;
        long totalVisualRows;
        if (!double.IsFinite(maxWidth) || maxWidth <= 0) {
            totalVisualRows = lineCount;
        } else {
            var charsPerRow = Math.Max(1, (int)(maxWidth / cw));
            totalVisualRows = Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow));
        }

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
            _extent = new Size(extentWidth, totalVisualRows * rh);
            _renderOffsetY = 0;
            return;
        }

        var len = (int)(endOfs - startOfs);

        // If the underlying buffer has evicted pages for this range, kick off
        // async page loads and layout empty text.  ProgressChanged will trigger
        // a re-layout once the data arrives.
        if (len > 0 && doc.Table.Buffer is { } buf && !buf.IsLoaded(startOfs, len)) {
            buf.EnsureLoaded(startOfs, len);
            _layout = _layoutEngine.Layout(string.Empty, typeface, FontSize, ForegroundBrush, maxWidth, 0);
            _extent = new Size(extentWidth, totalVisualRows * rh);
            _renderOffsetY = 0;
            return;
        }

        var text = len > 0 ? doc.Table.GetText(startOfs, len) : string.Empty;

        _layout = _layoutEngine.Layout(text, typeface, FontSize, ForegroundBrush, maxWidth, startOfs);
        _extent = new Size(extentWidth, totalVisualRows * rh);

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
                - _layout.Lines[0].HeightInRows * rh;
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
        _winFirstLineHeight = _layout.Lines.Count > 0 ? _layout.Lines[0].HeightInRows * rh : rh;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext context) {
        _perfSw.Restart();

        var layout = EnsureLayout();
        var doc = Document;

        // Background
        context.FillRectangle(_theme.EditorBackground, new Rect(Bounds.Size));

        // Gutter (line numbers)
        DrawGutter(context, layout);

        // Column guide line
        if (_wrapLinesAt >= 1) {
            var guideX = 2 + _gutterWidth + _wrapLinesAt * GetCharWidth();
            if (guideX < Bounds.Width) {
                context.DrawLine(_theme.GuideLinePen, new Point(guideX, 0), new Point(guideX, Bounds.Height));
            }
        }

        // Draw selection rectangles behind text
        if (doc != null && !doc.Selection.IsEmpty) {
            DrawSelection(context, layout, doc.Selection);
        }

        // Draw each visible line's text
        var rh = layout.RowHeight;
        foreach (var line in layout.Lines) {
            var y = line.Row * rh + _renderOffsetY;
            if (y + line.HeightInRows * rh < 0) {
                continue; // above viewport
            }
            if (y > Bounds.Height) {
                break; // below viewport
            }
            line.Layout.Draw(context, new Point(_gutterWidth, y));
        }

        // Draw caret (hidden during any scroll-drag operation)
        var scrollDrag = ScrollBar?.IsDragging ?? false;
        if (doc != null && _caretVisible && IsFocused && !_middleDrag && !scrollDrag) {
            DrawCaret(context, layout, doc.Selection.Caret);
        }

        _perfSw.Stop();
        PerfStats.Render.Record(_perfSw.Elapsed.TotalMilliseconds);
        PerfStats.SampleMemory();
        StatusUpdated?.Invoke();
    }

    private void DrawGutter(DrawingContext context, LayoutResult layout) {
        if (_gutterWidth <= 0) return;

        var rh = layout.RowHeight;
        var table = Document?.Table;

        // Gutter background
        context.FillRectangle(_theme.GutterBackground, new Rect(0, 0, _gutterWidth, Bounds.Height));

        if (!_showLineNumbers || table == null || layout.Lines.Count == 0) return;

        var typeface = new Typeface(FontFamily);
        var numW = _gutterWidth - GutterPadRight;
        var firstLineIdx = table.LineFromOfs(layout.ViewportBase + layout.Lines[0].CharStart);

        for (var i = 0; i < layout.Lines.Count; i++) {
            var line = layout.Lines[i];
            var y = line.Row * rh + _renderOffsetY;
            if (y + rh < 0) continue;
            if (y > Bounds.Height) break;

            var lineNum = firstLineIdx + i + 1;
            var numText = lineNum.ToString();
            using var tl = new TextLayout(
                numText, typeface, FontSize, _theme.GutterForeground,
                textAlignment: TextAlignment.Right,
                maxWidth: numW);
            tl.Draw(context, new Point(0, y));
        }
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
        var rh = layout.RowHeight;
        var rects = new List<Rect>();
        foreach (var line in layout.Lines) {
            var lineY = line.Row * rh + _renderOffsetY;
            if (lineY + line.HeightInRows * rh < 0) {
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
                rects.Add(new Rect(rect.X + _gutterWidth, lineY + rect.Y, rect.Width, rect.Height));
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
        context.FillRectangle(CaretBrush, new Rect(rect.X + _gutterWidth, y, 1.5, rect.Height));
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
    // Edit coalescing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Commits the current compound edit (if any) and resets coalescing state.
    /// Call before undo, redo, clipboard ops, cursor movement, focus loss, etc.
    /// </summary>
    public void FlushCompound() {
        _coalesceTimer.Stop();
        if (_compoundOpen) {
            Document?.EndCompound();
            _compoundOpen = false;
        }
        _coalesceKey = null;
    }

    /// <summary>
    /// Ensures a compound edit is open for the given coalesce <paramref name="key"/>.
    /// If the key differs from the current one, flushes the old compound first.
    /// Restarts the idle timer so a pause commits the compound automatically.
    /// </summary>
    private void Coalesce(string key) {
        if (_coalesceKey != key) {
            FlushCompound();
        }
        if (!_compoundOpen) {
            Document?.BeginCompound();
            _compoundOpen = true;
        }
        _coalesceKey = key;
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Public edit commands (invoked by menu and keyboard shortcuts)
    // -------------------------------------------------------------------------

    public async Task CopyAsync() {
        var doc = Document;
        if (doc == null || doc.Selection.IsEmpty) {
            return;
        }
        FlushCompound();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) {
            return;
        }
        await clipboard.SetTextAsync(doc.GetSelectedText());
    }

    public async Task CutAsync() {
        var doc = Document;
        if (doc == null || doc.Selection.IsEmpty) {
            return;
        }
        FlushCompound();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) {
            return;
        }
        await clipboard.SetTextAsync(doc.GetSelectedText());
        _editSw.Restart();
        doc.DeleteSelection();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public async Task PasteAsync() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) {
            return;
        }
#pragma warning disable CS0618 // GetTextAsync is deprecated but TryGetTextAsync requires IAsyncDataTransfer
        var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(text)) {
            return;
        }
        // Normalize Windows line endings to LF
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        _preferredCaretX = -1;
        _editSw.Restart();
        doc.Insert(text);
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void EditDelete() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("delete");
        _editSw.Restart();
        if (!doc.Selection.IsEmpty) {
            doc.DeleteSelection();
        } else {
            doc.DeleteForward();
        }
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformUndo() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        doc.Undo();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformRedo() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        doc.Redo();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformSelectAll() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.Selection = new Selection(0L, doc.Table.Length);
        InvalidateVisual();
    }

    public void PerformSelectWord() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.SelectWord();
        InvalidateVisual();
        ResetCaretBlink();
    }

    public void PerformExpandSelection() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.ExpandSelection(ExpandSelectionMode);
        InvalidateVisual();
        ResetCaretBlink();
    }

    public void PerformDeleteLine() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("delete-line");
        _editSw.Restart();
        doc.DeleteLine();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformMoveLineUp() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("move-line-up");
        _editSw.Restart();
        doc.MoveLineUp();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformMoveLineDown() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("move-line-down");
        _editSw.Restart();
        doc.MoveLineDown();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformTransformCase(CaseTransform transform) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        doc.TransformCase(transform);
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Keyboard input
    // -------------------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        if (IsInputBlocked) { e.Handled = true; return; }
        var doc = Document;
        if (doc == null || string.IsNullOrEmpty(e.Text)) {
            return;
        }
        _preferredCaretX = -1;
        Coalesce("char");

        _editSw.Restart();
        doc.Insert(e.Text);
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        e.Handled = true;
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Command dispatch (called by MainWindow after key → command resolution)
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> VerticalNavCommands = [
        Commands.CommandIds.NavMoveUp,
        Commands.CommandIds.NavMoveDown,
        Commands.CommandIds.NavSelectUp,
        Commands.CommandIds.NavSelectDown,
        Commands.CommandIds.NavPageUp,
        Commands.CommandIds.NavPageDown,
        Commands.CommandIds.NavSelectPageUp,
        Commands.CommandIds.NavSelectPageDown,
        Commands.CommandIds.NavScrollLineUp,
        Commands.CommandIds.NavScrollLineDown,
    ];

    /// <summary>
    /// Executes an editor-level command by ID. Returns true if the command
    /// was handled. Called by MainWindow's centralized dispatch after
    /// resolving a key gesture to a command ID via KeyBindingService.
    /// </summary>
    public bool ExecuteCommand(string commandId) {
        if (IsInputBlocked) return false;
        var doc = Document;
        if (doc == null) return false;

        // Vertical movement keys preserve the preferred column; everything
        // else resets it so the next Up/Down captures a fresh X position.
        if (!VerticalNavCommands.Contains(commandId)) {
            _preferredCaretX = -1;
        }

        switch (commandId) {
            // -- Edit commands --
            case Commands.CommandIds.EditBackspace:
                Coalesce("backspace");
                _editSw.Restart();
                doc.DeleteBackward();
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                ScrollCaretIntoView();
                InvalidateLayout();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.EditDelete:
                EditDelete();
                return true;

            case Commands.CommandIds.EditUndo:
                PerformUndo();
                return true;

            case Commands.CommandIds.EditRedo:
                PerformRedo();
                return true;

            case Commands.CommandIds.EditCut:
                _ = CutAsync();
                return true;

            case Commands.CommandIds.EditCopy:
                _ = CopyAsync();
                return true;

            case Commands.CommandIds.EditPaste:
                _ = PasteAsync();
                return true;

            case Commands.CommandIds.EditSelectAll:
                PerformSelectAll();
                return true;

            case Commands.CommandIds.EditSelectWord:
                PerformSelectWord();
                return true;

            case Commands.CommandIds.EditExpandSelection:
                PerformExpandSelection();
                return true;

            case Commands.CommandIds.EditDeleteLine:
                PerformDeleteLine();
                return true;

            case Commands.CommandIds.EditMoveLineUp:
                PerformMoveLineUp();
                return true;

            case Commands.CommandIds.EditMoveLineDown:
                PerformMoveLineDown();
                return true;

            case Commands.CommandIds.EditUpperCase:
                PerformTransformCase(CaseTransform.Upper);
                return true;

            case Commands.CommandIds.EditLowerCase:
                PerformTransformCase(CaseTransform.Lower);
                return true;

            case Commands.CommandIds.EditProperCase:
                PerformTransformCase(CaseTransform.Proper);
                return true;

            case Commands.CommandIds.EditNewline:
                FlushCompound();
                _editSw.Restart();
                doc.Insert(doc.LineEndingInfo.NewlineString);
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                ScrollCaretIntoView();
                InvalidateLayout();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.EditTab:
                Coalesce("tab");
                _editSw.Restart();
                doc.Insert("    ");
                _editSw.Stop();
                PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
                ScrollCaretIntoView();
                InvalidateLayout();
                ResetCaretBlink();
                return true;

            // -- Navigation: horizontal --
            case Commands.CommandIds.NavMoveLeft:
                FlushCompound();
                MoveCaretHorizontal(doc, -1, false, false);
                return true;

            case Commands.CommandIds.NavSelectLeft:
                FlushCompound();
                MoveCaretHorizontal(doc, -1, false, true);
                return true;

            case Commands.CommandIds.NavMoveRight:
                FlushCompound();
                MoveCaretHorizontal(doc, +1, false, false);
                return true;

            case Commands.CommandIds.NavSelectRight:
                FlushCompound();
                MoveCaretHorizontal(doc, +1, false, true);
                return true;

            case Commands.CommandIds.NavMoveWordLeft:
                FlushCompound();
                MoveCaretHorizontal(doc, -1, true, false);
                return true;

            case Commands.CommandIds.NavSelectWordLeft:
                FlushCompound();
                MoveCaretHorizontal(doc, -1, true, true);
                return true;

            case Commands.CommandIds.NavMoveWordRight:
                FlushCompound();
                MoveCaretHorizontal(doc, +1, true, false);
                return true;

            case Commands.CommandIds.NavSelectWordRight:
                FlushCompound();
                MoveCaretHorizontal(doc, +1, true, true);
                return true;

            // -- Navigation: vertical --
            case Commands.CommandIds.NavMoveUp:
                FlushCompound();
                MoveCaretVertical(doc, -1, false);
                return true;

            case Commands.CommandIds.NavSelectUp:
                FlushCompound();
                MoveCaretVertical(doc, -1, true);
                return true;

            case Commands.CommandIds.NavMoveDown:
                FlushCompound();
                MoveCaretVertical(doc, +1, false);
                return true;

            case Commands.CommandIds.NavSelectDown:
                FlushCompound();
                MoveCaretVertical(doc, +1, true);
                return true;

            // -- Navigation: home/end --
            case Commands.CommandIds.NavMoveHome:
                FlushCompound();
                MoveCaretToLineEdge(doc, toStart: true, false);
                return true;

            case Commands.CommandIds.NavSelectHome:
                FlushCompound();
                MoveCaretToLineEdge(doc, toStart: true, true);
                return true;

            case Commands.CommandIds.NavMoveEnd:
                FlushCompound();
                MoveCaretToLineEdge(doc, toStart: false, false);
                return true;

            case Commands.CommandIds.NavSelectEnd:
                FlushCompound();
                MoveCaretToLineEdge(doc, toStart: false, true);
                return true;

            // -- Navigation: document start/end --
            case Commands.CommandIds.NavMoveDocStart:
                FlushCompound();
                doc.Selection = Selection.Collapsed(0);
                ScrollCaretIntoView();
                InvalidateVisual();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.NavSelectDocStart:
                FlushCompound();
                doc.Selection = doc.Selection.ExtendTo(0);
                ScrollCaretIntoView();
                InvalidateVisual();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.NavMoveDocEnd:
                FlushCompound();
                doc.Selection = Selection.Collapsed(doc.Table.Length);
                ScrollCaretIntoView();
                InvalidateVisual();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.NavSelectDocEnd:
                FlushCompound();
                doc.Selection = doc.Selection.ExtendTo(doc.Table.Length);
                ScrollCaretIntoView();
                InvalidateVisual();
                ResetCaretBlink();
                return true;

            // -- Navigation: page up/down --
            case Commands.CommandIds.NavPageUp:
                FlushCompound();
                MoveCaretByPage(doc, -1, false);
                return true;

            case Commands.CommandIds.NavSelectPageUp:
                FlushCompound();
                MoveCaretByPage(doc, -1, true);
                return true;

            case Commands.CommandIds.NavPageDown:
                FlushCompound();
                MoveCaretByPage(doc, +1, false);
                return true;

            case Commands.CommandIds.NavSelectPageDown:
                FlushCompound();
                MoveCaretByPage(doc, +1, true);
                return true;

            // -- New editing commands --
            case Commands.CommandIds.EditDeleteWordLeft:
                FlushCompound();
                if (!doc.Selection.IsEmpty) {
                    doc.DeleteSelection();
                } else {
                    var wordLeft = FindWordBoundaryLeft(doc, doc.Selection.Caret);
                    if (wordLeft < doc.Selection.Caret) {
                        doc.Selection = new Selection(wordLeft, doc.Selection.Caret);
                        doc.DeleteSelection();
                    }
                }
                ScrollCaretIntoView();
                InvalidateLayout();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.EditDeleteWordRight:
                FlushCompound();
                if (!doc.Selection.IsEmpty) {
                    doc.DeleteSelection();
                } else {
                    var wordRight = FindWordBoundaryRight(doc, doc.Selection.Caret);
                    if (wordRight > doc.Selection.Caret) {
                        doc.Selection = new Selection(doc.Selection.Caret, wordRight);
                        doc.DeleteSelection();
                    }
                }
                ScrollCaretIntoView();
                InvalidateLayout();
                ResetCaretBlink();
                return true;

            case Commands.CommandIds.EditInsertLineBelow:
                PerformInsertLineBelow();
                return true;

            case Commands.CommandIds.EditInsertLineAbove:
                PerformInsertLineAbove();
                return true;

            case Commands.CommandIds.EditDuplicateLine:
                PerformDuplicateLine();
                return true;

            case Commands.CommandIds.EditIndent:
                Coalesce("indent");
                PerformIndent();
                return true;

            case Commands.CommandIds.EditLineEndingLF:
                FlushCompound();
                doc.ConvertLineEndings(Core.Documents.LineEnding.LF);
                InvalidateLayout();
                return true;
            case Commands.CommandIds.EditLineEndingCRLF:
                FlushCompound();
                doc.ConvertLineEndings(Core.Documents.LineEnding.CRLF);
                InvalidateLayout();
                return true;
            case Commands.CommandIds.EditLineEndingCR:
                FlushCompound();
                doc.ConvertLineEndings(Core.Documents.LineEnding.CR);
                InvalidateLayout();
                return true;

            case Commands.CommandIds.EditIndentToSpaces:
                FlushCompound();
                doc.ConvertIndentation(Core.Documents.IndentStyle.Spaces);
                InvalidateLayout();
                return true;
            case Commands.CommandIds.EditIndentToTabs:
                FlushCompound();
                doc.ConvertIndentation(Core.Documents.IndentStyle.Tabs);
                InvalidateLayout();
                return true;

            // -- Scroll without moving caret --
            case Commands.CommandIds.NavScrollLineUp:
                FlushCompound();
                ScrollValue -= GetRowHeight();
                InvalidateVisual();
                return true;

            case Commands.CommandIds.NavScrollLineDown:
                FlushCompound();
                ScrollValue += GetRowHeight();
                InvalidateVisual();
                return true;

            default:
                return false;
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
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Moves the caret up or down by one visual row.
    /// When the caret is already at the top or bottom edge of the viewport,
    /// the document scrolls by one row while the caret stays at the same
    /// screen position — matching the page-up/down pattern but at row scale.
    /// </summary>
    private void MoveCaretVertical(Document doc, int lineDelta, bool extend) {
        var layout = EnsureLayout();
        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;

        // If the caret is outside the visible window, skip the movement.
        if (localCaret < 0 || localCaret > totalChars) {
            return;
        }

        var rh = layout.RowHeight;
        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout);

        // On the first vertical move, capture the caret's current X as the
        // "preferred" column.  Subsequent vertical moves reuse this so the
        // caret returns to the original column after traversing short lines.
        if (_preferredCaretX < 0) {
            _preferredCaretX = caretRect.X;
        }

        // Pixel-based edge detection: check whether the next row would be
        // fully visible.  This avoids rounding-dependent off-by-ones that
        // the integer screen-row check was susceptible to.
        var caretScreenY = caretRect.Y + _renderOffsetY;
        var atTopEdge = lineDelta < 0 && caretScreenY < rh;
        var atBottomEdge = lineDelta > 0 && caretScreenY + 2 * rh > _viewport.Height;

        if (atTopEdge || atBottomEdge) {
            // Scroll the viewport by one row; keep the caret at the same
            // screen position so the content slides under it.
            var caretScreenRow = GetCaretScreenRow(caretRect, rh);
            ScrollValue += lineDelta * rh;
            _layout?.Dispose();
            _layout = null;
            var newLayout = EnsureLayout();
            var newCaret = HitTestAtScreenRow(caretScreenRow, rh, newLayout);
            doc.Selection = extend
                ? doc.Selection.ExtendTo(newCaret)
                : Selection.Collapsed(newCaret);
        } else {
            // Normal movement within the viewport — move the caret one
            // visual row.  Use rh (not caretRect.Height) as the step so
            // wrapped lines advance one visual row at a time.
            var targetY = caretRect.Y + rh / 2 + lineDelta * rh;
            var localNewCaret = _layoutEngine.HitTest(
                new Point(_preferredCaretX >= 0 ? _preferredCaretX : 0, targetY), layout);
            var newCaret = layout.ViewportBase + localNewCaret;
            doc.Selection = extend
                ? doc.Selection.ExtendTo(newCaret)
                : Selection.Collapsed(newCaret);
        }

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
        ScrollCaretIntoView();
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

    // -------------------------------------------------------------------------
    // New editing command helpers
    // -------------------------------------------------------------------------

    private void PerformInsertLineBelow() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineIdx = doc.Table.LineFromOfs(doc.Selection.Caret);
        if (lineIdx + 1 < doc.Table.LineCount) {
            var nextLineStart = doc.Table.LineStartOfs(lineIdx + 1);
            doc.Selection = Selection.Collapsed(nextLineStart);
            doc.Insert(nl);
            doc.Selection = Selection.Collapsed(nextLineStart);
        } else {
            // Last line — append newline at end
            doc.Selection = Selection.Collapsed(doc.Table.Length);
            doc.Insert(nl);
        }
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformInsertLineAbove() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineStart = doc.Table.LineStartOfs(doc.Table.LineFromOfs(doc.Selection.Caret));
        doc.Selection = Selection.Collapsed(lineStart);
        doc.Insert(nl);
        doc.Selection = Selection.Collapsed(lineStart);
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformDuplicateLine() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        var nl = doc.LineEndingInfo.NewlineString;
        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineIdx = table.LineFromOfs(caret);
        var lineStart = table.LineStartOfs(lineIdx);
        var caretCol = caret - lineStart;
        long lineEnd = lineIdx + 1 < table.LineCount
            ? table.LineStartOfs(lineIdx + 1)
            : table.Length;
        var lineText = table.GetText(lineStart, (int)(lineEnd - lineStart));

        // If the line doesn't end with a newline (last line), prepend one.
        if (lineEnd == table.Length && (lineText.Length == 0 || lineText[^1] != '\n')) {
            doc.BeginCompound();
            doc.Selection = Selection.Collapsed(table.Length);
            doc.Insert(nl + lineText);
            doc.EndCompound();
        } else {
            doc.Selection = Selection.Collapsed(lineEnd);
            doc.Insert(lineText);
        }
        // Place caret on the duplicated line at the same column offset.
        var nlLen = nl.Length;
        var newLineStart = lineEnd + (lineText.Length > 0 && lineText[^1] != '\n' ? nlLen : 0);
        doc.Selection = Selection.Collapsed(Math.Min(newLineStart + caretCol, table.Length));
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformIndent() {
        var doc = Document;
        if (doc == null) return;
        var table = doc.Table;
        var sel = doc.Selection;

        // If selection spans multiple lines, indent each line.
        var startLine = table.LineFromOfs(sel.Start);
        var endLine = table.LineFromOfs(Math.Max(sel.Start, sel.End - 1));
        if (sel.IsEmpty || startLine == endLine) {
            // Single-line or no selection: just insert 4 spaces at caret.
            _editSw.Restart();
            doc.Insert("    ");
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        } else {
            // Multi-line selection: prepend 4 spaces to each line.
            doc.BeginCompound();
            for (var line = startLine; line <= endLine; line++) {
                var ofs = table.LineStartOfs(line);
                doc.Selection = Selection.Collapsed(ofs);
                doc.Insert("    ");
            }
            doc.EndCompound();
            // Select the full indented line range so the user can indent again.
            var rangeStart = table.LineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.LineStartOfs(endLine + 1)
                : table.Length;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }
        ScrollCaretIntoView();
        InvalidateLayout();
        ResetCaretBlink();
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

    /// <summary>
    /// Returns the caret's visual screen row (0-based from viewport top).
    /// Used by <see cref="MoveCaretVertical"/> and <see cref="MoveCaretByPage"/>
    /// to preserve the caret's screen position across scroll operations.
    /// </summary>
    private int GetCaretScreenRow(Rect caretRect, double rh) {
        return Math.Max(0, (int)Math.Round((caretRect.Y + _renderOffsetY) / rh));
    }

    /// <summary>
    /// Hit-tests the layout at a given screen row, returning the absolute
    /// document offset closest to (<see cref="_preferredCaretX"/>, screenRow).
    /// </summary>
    private long HitTestAtScreenRow(int screenRow, double rh, LayoutResult layout) {
        var targetY = screenRow * rh + rh / 2 - _renderOffsetY;
        targetY = Math.Clamp(targetY, 0, Math.Max(0, layout.TotalHeight - 1));
        var hitX = _preferredCaretX >= 0 ? _preferredCaretX : 0;
        var localNew = _layoutEngine.HitTest(new Point(hitX, targetY), layout);
        return layout.ViewportBase + localNew;
    }

    /// <summary>
    /// Moves the caret to the given offset and scrolls it into view.
    /// Used by GoTo Line and similar external navigation features.
    /// </summary>
    public void GoToPosition(long offset) {
        var doc = Document;
        if (doc == null) return;
        offset = Math.Clamp(offset, 0, doc.Table.Length);
        doc.Selection = Core.Documents.Selection.Collapsed(offset);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
        Focus();
    }

    /// <summary>
    /// Adjusts <see cref="ScrollValue"/> so the caret's logical line is visible
    /// within the viewport.  For windowed layout the caret position is estimated
    /// from the logical line index and the average line height; for small (full)
    /// layouts the exact pixel position from <see cref="TextLayoutEngine"/> is used.
    /// </summary>
    private void ScrollCaretIntoView() {
        var doc = Document;
        if (doc == null) return;

        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var rh = GetRowHeight();

        // At the document end, scroll to the exact bottom so the result
        // matches dragging the scroll thumb to the bottom.
        if (caret >= table.Length) {
            ScrollValue = ScrollMaximum;
            return;
        }

        if (lineCount > WindowedLayoutThreshold) {
            // Windowed layout — estimate caret Y from its logical line index.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var totalChars = table.Length;
            long totalVisualRows;
            if (!double.IsFinite(textW) || textW <= 0) {
                totalVisualRows = lineCount;
            } else {
                var cw = GetCharWidth();
                var charsPerRow = Math.Max(1, (int)(textW / cw));
                totalVisualRows = Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow));
            }
            var avgRowsPerLine = Math.Max(1.0, (double)totalVisualRows / lineCount);
            var avgLineHeight = avgRowsPerLine * rh;

            var caretLine = table.LineFromOfs(caret);
            var caretY = caretLine * avgLineHeight;

            if (caretY < _scrollOffset.Y) {
                // Caret is above viewport — scroll so caret line is at the top.
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + _viewport.Height) {
                // Caret is below viewport — scroll so caret line is at the bottom.
                ScrollValue = caretY + rh - _viewport.Height;
            }
        } else {
            // Full layout — use exact pixel position from the layout engine.
            var layout = EnsureLayout();
            var localCaret = (int)(caret - layout.ViewportBase);
            var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout);
            var caretTop = caretRect.Y;
            var caretBot = caretRect.Y + caretRect.Height;

            if (caretTop < _scrollOffset.Y) {
                ScrollValue = caretTop;
            } else if (caretBot > _scrollOffset.Y + _viewport.Height) {
                ScrollValue = caretBot - _viewport.Height;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Page scrolling (keyboard and scrollbar track-click)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scrolls the viewport by one page so that the current bottom line becomes
    /// the new top (page down) or vice versa.  Works in logical-line space so
    /// wrapping is handled correctly.  Called by the scrollbar track-click and
    /// used internally by <see cref="MoveCaretByPage"/>.
    /// </summary>
    public void ScrollPage(int direction) {
        var doc = Document;
        if (doc == null) return;
        var table = doc.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var layout = EnsureLayout();
        var topLine = table.LineFromOfs(layout.ViewportBase);
        var lastVisibleLine = FindLastVisibleLogicalLine(layout, table);
        var pageLines = Math.Max(1L, lastVisibleLine - topLine);

        long targetLine;
        if (direction > 0) {
            targetLine = Math.Min(lastVisibleLine, lineCount - 1);
            // If we can't advance (viewport shows only one line due to wrapping),
            // advance by at least one line.
            if (targetLine <= topLine) {
                targetLine = Math.Min(topLine + 1, lineCount - 1);
            }
        } else {
            targetLine = Math.Max(0, topLine - pageLines);
            if (targetLine >= topLine && topLine > 0) {
                targetLine = topLine - 1;
            }
        }

        ScrollToTopLine(targetLine, table);
    }

    /// <summary>
    /// Sets <see cref="ScrollValue"/> so that the given logical line appears
    /// at the top of the viewport.  Uses the same avgLineHeight formula that
    /// <see cref="LayoutWindowed"/> uses, so the mapping is consistent.
    /// </summary>
    private void ScrollToTopLine(long targetLine, PieceTable table) {
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var rh = GetRowHeight();
        var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
        var textW = GetTextWidth(maxW);
        var totalChars = table.Length;
        long totalVisualRows;
        if (!double.IsFinite(textW) || textW <= 0) {
            totalVisualRows = lineCount;
        } else {
            var cw = GetCharWidth();
            var charsPerRow = Math.Max(1, (int)(textW / cw));
            totalVisualRows = Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow));
        }
        var avgRowsPerLine = Math.Max(1.0, (double)totalVisualRows / lineCount);
        var avgLineHeight = avgRowsPerLine * rh;

        // Nudge by a sub-pixel amount so that the (long)(scrollOfs / avgLineHeight)
        // truncation in LayoutWindowed always resolves to targetLine.  Without
        // this, the floating-point round-trip can land at targetLine - ε, which
        // (long) truncates down by 1, making _renderOffsetY jump by avgLineHeight
        // and destabilising the screen-row computation in MoveCaretByPage.
        ScrollValue = targetLine * avgLineHeight + 0.01;
    }

    /// <summary>
    /// Returns the logical line index of the last fully visible line in the
    /// current layout.  A visual row is "fully visible" when its entire height
    /// fits within the viewport after applying <see cref="_renderOffsetY"/>.
    /// </summary>
    private long FindLastVisibleLogicalLine(LayoutResult layout, PieceTable table) {
        var lines = layout.Lines;
        if (lines.Count == 0) {
            return table.LineFromOfs(layout.ViewportBase);
        }

        var rh = layout.RowHeight;
        for (var i = lines.Count - 1; i >= 0; i--) {
            var screenBottom = (lines[i].Row + lines[i].HeightInRows) * rh + _renderOffsetY;
            if (screenBottom <= _viewport.Height + 0.5) {
                return table.LineFromOfs(layout.ViewportBase + lines[i].CharStart);
            }
        }

        return table.LineFromOfs(layout.ViewportBase);
    }

    // -------------------------------------------------------------------------
    // Mouse / pointer input
    // -------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;

        // Allow middle-click scroll while loading, block everything else.
        if (IsInputBlocked && !props.IsMiddleButtonPressed) {
            e.Handled = true;
            return;
        }

        _preferredCaretX = -1;
        FlushCompound();

        // Hide caret and pause blinking while processing the press.
        _caretVisible = false;
        _caretTimer.Stop();
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
        var layoutPt = new Point(pt.X - _gutterWidth, pt.Y - _renderOffsetY);
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;
        var isLeft = props.IsLeftButtonPressed;
        // Left-click: place caret or extend selection (with Shift).
        // Right-click: place caret only (no Shift-extend, no drag-select).
        if (isLeft) {
            var clickCount = e.ClickCount;
            if (clickCount == 3) {
                // Triple-click: select entire line.
                doc.Selection = Selection.Collapsed(ofs);
                doc.SelectLine();
                e.Handled = true;
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
            if (clickCount == 2) {
                // Double-click: select word at click position.
                doc.Selection = Selection.Collapsed(ofs);
                doc.SelectWord();
                e.Handled = true;
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
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
        var layoutPt = new Point(pt.X - _gutterWidth, pt.Y - _renderOffsetY);
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
        if (IsInputBlocked) return; // no caret until loaded
        ResetCaretBlink();
    }

    protected override void OnLostFocus(RoutedEventArgs e) {
        base.OnLostFocus(e);
        _caretVisible = false;
        _caretTimer.Stop();
        FlushCompound();
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
    /// Moves the caret by one page while keeping it at the same screen row.
    /// The viewport scrolls; the caret stays on its row.  Near document
    /// boundaries the scroll is reduced so the caret's row always has content.
    /// </summary>
    /// <remarks>
    /// <c>layout.Lines</c> are <em>logical</em> lines — a wrapped line
    /// occupies one entry but spans multiple visual rows.  The screen-row
    /// math therefore works in screen-pixel space, not in line-index space.
    /// <para/>
    /// The caret's screen Y is captured before the scroll, quantised to
    /// an integer visual row, and then restored after the scroll.
    /// <see cref="_renderOffsetY"/> converts between layout and screen
    /// coordinates on both sides, so the screen position is preserved
    /// even when the render offset changes between layouts.
    /// </remarks>
    private void MoveCaretByPage(Document doc, int direction, bool extend) {
        var table = doc.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var curLayout = EnsureLayout();
        var rh = curLayout.RowHeight;
        var caretRow = 0;

        if (curLayout.Lines.Count > 0) {
            var localCaret = (int)(doc.Selection.Caret - curLayout.ViewportBase);
            var totalChars = curLayout.Lines[^1].CharEnd;
            if (localCaret >= 0 && localCaret <= totalChars) {
                var caretRect = _layoutEngine.GetCaretBounds(localCaret, curLayout);
                caretRow = GetCaretScreenRow(caretRect, rh);
                if (_preferredCaretX < 0) {
                    _preferredCaretX = caretRect.X;
                }
            }
        }

        // Scroll the viewport by one page.
        ScrollPage(direction);

        // Relayout at the new scroll position.
        _layout?.Dispose();
        _layout = null;
        var newLayout = EnsureLayout();

        // If the target screen row is past the layout content, back up so
        // the last document line lands at caretRow.
        var targetY = caretRow * rh + rh / 2 - _renderOffsetY;
        if (targetY >= newLayout.TotalHeight && newLayout.Lines.Count > 0) {
            var lastLogicalLine = lineCount - 1;
            var backupTopLine = Math.Max(0, lastLogicalLine - caretRow);
            ScrollToTopLine(backupTopLine, table);
            _layout?.Dispose();
            _layout = null;
            newLayout = EnsureLayout();
        }

        // Place the caret at the same visual row.
        if (newLayout.Lines.Count == 0) return;
        var newCaret = HitTestAtScreenRow(caretRow, rh, newLayout);

        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void OnDocumentChanged(object? sender, EventArgs e) {
        InvalidateLayout();
    }

    // -------------------------------------------------------------------------
    // Tab scroll state save / restore
    // -------------------------------------------------------------------------

    /// <summary>
    /// Captures scroll and windowed-layout tracking state into
    /// <paramref name="tab"/> so it can be restored when switching back.
    /// </summary>
    public void SaveScrollState(TabState tab) {
        tab.ScrollOffsetY = _scrollOffset.Y;
        tab.WinTopLine = _winTopLine;
        tab.WinScrollOffset = _winScrollOffset;
        tab.WinRenderOffsetY = _winRenderOffsetY;
        tab.WinFirstLineHeight = _winFirstLineHeight;
    }

    /// <summary>
    /// Restores previously saved scroll state after a document swap.
    /// Must be called <b>after</b> setting <see cref="Document"/>,
    /// which resets scroll to (0,0).
    /// </summary>
    public void RestoreScrollState(TabState tab) {
        _scrollOffset = new Vector(0, tab.ScrollOffsetY);
        _winTopLine = tab.WinTopLine;
        _winScrollOffset = tab.WinScrollOffset;
        _winRenderOffsetY = tab.WinRenderOffsetY;
        _winFirstLineHeight = tab.WinFirstLineHeight;
        _layout?.Dispose();
        _layout = null;
        InvalidateMeasure();
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }
}
