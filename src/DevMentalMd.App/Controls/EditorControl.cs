using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;
using Cmd = DevMentalMd.App.Commands.Commands;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Clipboard;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.Documents.History;
using DevMentalMd.Rendering.Layout;

namespace DevMentalMd.App.Controls;

/// <summary>
/// Custom plain-text editing control built directly on Avalonia's DrawingContext.
/// Implements <see cref="ILogicalScrollable"/> so a parent ScrollViewer cooperates
/// with windowed layout for large documents.
/// </summary>
public sealed class EditorControl : Control, ILogicalScrollable, IScrollSource {
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
    /// <summary>
    /// When true, editing commands (typing, delete, paste, undo/redo) are blocked
    /// but navigation commands (arrow keys, Page Up/Down, Home/End, click-to-position)
    /// are allowed.  Set during file loading so users can browse while content streams in.
    /// </summary>
    public bool IsEditBlocked { get; set; }

    /// <summary>
    /// True while a large paste is being processed on a background thread.
    /// Prevents layout/render from accessing the PieceTable.
    /// </summary>
    internal bool BackgroundPasteInProgress { get; private set; }

    /// <summary>
    /// True while the document is still streaming from disk or a large paste
    /// is processing.  Blocks scrolling, caret drawing, and mouse interaction.
    /// </summary>
    public bool IsLoading => BackgroundPasteInProgress || (Document?.IsLoading ?? false);

    /// <summary>True when the document has an active selection or column selection.</summary>
    private bool HasSelection() {
        var doc = Document;
        if (doc == null) return false;
        return !doc.Selection.IsEmpty || doc.ColumnSel != null;
    }

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private readonly TextLayoutEngine _layoutEngine = new();
    private LayoutResult? _layout;
    /// <summary>
    /// Set when a layout pass throws an unrecoverable exception.  Once set,
    /// all subsequent layout/render attempts return an empty layout so the
    /// error dialog can paint without triggering the same crash again.
    /// </summary>
    private bool _layoutFailed;
    private bool _caretVisible = true;
    private bool _keepScrollOnSwap;
    private readonly DispatcherTimer _caretTimer;
    private bool _pointerDown;
    private bool _middleDrag;
    private bool _columnDrag;
    private double _middleDragStartY;
    private bool _overwriteMode;

    // Clipboard ring — shared by PasteMore (inline cycling) and ClipboardRing (popup).
    internal readonly ClipboardRing _clipboardRing = new();

    // PasteMore cycling state — active while the user holds Ctrl and presses
    // Shift+V repeatedly to cycle through ring entries.
    private bool _isClipboardCycling;
    private int _clipboardCycleIndex;
    private int _cycleInsertedLength;

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

    // Search state (shared across Find Bar, incremental search, Find Word/Selection)
    private string _lastSearchTerm = "";

    // Incremental search state
    private bool _inIncrementalSearch;
    private string _isearchString = "";
    private bool _isearchFailed;

    /// <summary>Optional reference to the scrollbar for middle-drag visual feedback.</summary>
    public DualZoneScrollBar? ScrollBar { get; set; }

    /// <summary>Whether long lines wrap at the viewport edge.</summary>
    public bool WrapLines {
        get => _wrapLines;
        set {
            if (_wrapLines == value) return;
            _wrapLines = value;
            // Column mode is incompatible with wrapping — exit if active.
            if (_wrapLines) {
                // Column mode is incompatible with wrapping — exit if active.
                if (Document?.ColumnSel != null) {
                    Document.ClearColumnSelection(_indentWidth);
                }
                // Reset horizontal scroll when wrapping is enabled.
                HScrollValue = 0;
            }
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

    /// <summary>
    /// Maximum assumed regex match length for chunked search overlap.
    /// Set from <see cref="Services.AppSettings.MaxRegexMatchLength"/>.
    /// </summary>
    public int MaxRegexMatchLength { get; set; } = 1024;

    /// <summary>Number of spaces per indent level. Also controls tab display width.</summary>
    public int IndentWidth {
        get => _indentWidth;
        set {
            var clamped = Math.Clamp(value, 1, 16);
            if (_indentWidth == clamped) return;
            _indentWidth = clamped;
            InvalidateLayout();
        }
    }

    /// <summary>Whether line numbers are displayed in a gutter on the left.</summary>
    public bool ShowLineNumbers {
        get => _showLineNumbers;
        set {
            if (_showLineNumbers == value) return;
            _showLineNumbers = value;
            InvalidateLayout();
        }
    }

    /// <summary>Whether visible whitespace glyphs are rendered for spaces, tabs, and NBSP.</summary>
    public bool ShowWhitespace {
        get => _showWhitespace;
        set {
            if (_showWhitespace == value) return;
            _showWhitespace = value;
            InvalidateVisual();
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
    private double RenderOffsetY {
        get => _renderOffsetY;
        set => _renderOffsetY = value; // tracepoint here
    }
    private EventHandler? _scrollInvalidated;

    /// <summary>
    /// X origin for text drawing, accounting for gutter and horizontal scroll.
    /// </summary>
    private double TextOriginX => _gutterWidth - _scrollOffset.X;

    /// <summary>Current horizontal scroll offset in pixels.</summary>
    public double HScrollValue {
        get => _scrollOffset.X;
        set {
            var clamped = Math.Clamp(value, 0, HScrollMaximum);
            if (Math.Abs(_scrollOffset.X - clamped) < 0.01) return;
            _scrollOffset = new Vector(clamped, _scrollOffset.Y);
            InvalidateVisual();
            HScrollChanged?.Invoke();
        }
    }

    /// <summary>Maximum horizontal scroll value.</summary>
    public double HScrollMaximum => Math.Max(0, _extent.Width - _viewport.Width);

    /// <summary>Raised when the horizontal extent or scroll changes.</summary>
    public event Action? HScrollChanged;

    // Word wrap
    private bool _wrapLines = false;
    private int _wrapLinesAt = 100;

    // Indentation
    private int _indentWidth = 4;

    // Whitespace visibility
    private bool _showWhitespace;

    // Line number gutter
    private bool _showLineNumbers = true;
    private double _gutterWidth;
    public double GutterWidth => _gutterWidth;
    public double CharWidth => GetCharWidth();
    private int _gutterDigitCnt;
    private const double GutterPadLeft = 6;
    private const double GutterPadRight = 6;

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
        /// <summary>Time for the most recent ReplaceAll operation.</summary>
        public double ReplaceAllTimeMs { get; set; }
        /// <summary>How many times ScrollCaretIntoView needed a retry pass.</summary>
        public long ScrollRetries { get; set; }
        /// <summary>How many times ScrollCaretIntoView ran (past all early exits).</summary>
        public long ScrollCaretCalls { get; set; }
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
    /// Fired when a document property (line ending, encoding, etc.) changes
    /// without a content edit. The tab should be marked dirty.
    /// </summary>
    public event Action? MetadataChanged;

    /// <summary>Raises the <see cref="MetadataChanged"/> event.</summary>
    public void RaiseMetadataChanged() => MetadataChanged?.Invoke();

    /// <summary>Whether overwrite mode is active (toggled by Insert key).</summary>
    public bool OverwriteMode {
        get => _overwriteMode;
        set {
            if (_overwriteMode == value) return;
            _overwriteMode = value;
            OverwriteModeChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>Fired when overwrite mode is toggled.</summary>
    public event EventHandler? OverwriteModeChanged;


    // -------------------------------------------------------------------------
    // Public scroll API (used by DualZoneScrollBar)
    // -------------------------------------------------------------------------

    /// <summary>Fired when a background paste starts or finishes.</summary>
    public event Action<bool>? BackgroundPasteChanged;

    /// <summary>Fired when scroll state changes (offset, extent, or viewport).</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Maximum scroll offset (extent height − viewport height). Always ≥ 0.</summary>
    public double ScrollMaximum => Math.Max(0, _extent.Height - _viewport.Height);

    /// <summary>
    /// True when the viewport is scrolled to the very bottom of the document
    /// (or the document is shorter than the viewport).
    /// </summary>
    public bool IsScrolledToEnd =>
        _extent.Height <= _viewport.Height
        || _scrollOffset.Y >= ScrollMaximum - 1.0;

    /// <summary>Current vertical scroll offset (0 .. ScrollMaximum).</summary>
    public double ScrollValue {
        get => _scrollOffset.Y;
        set {
            if (IsLoading) return;
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
            if (IsLoading) return;
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
            if (_keepScrollOnSwap) {
                _keepScrollOnSwap = false;
                // Reload path — keep current scroll offset and do NOT
                // dispose the existing layout eagerly. The next
                // MeasureOverride will rebuild it with the new document.
                // This avoids a frame where the layout is null and the
                // viewport renders as blank.
                _rowHeight = 0;
                _charWidth = 0;
                InvalidateMeasure();
                return;
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
    /// Returns the number of characters that fit in one visual row at the
    /// current font/wrap settings, or 0 when wrapping is off.
    /// </summary>
    private int GetCharsPerRow(double textWidth) {
        if (!double.IsFinite(textWidth) || textWidth <= 0) return 0;
        return Math.Max(1, (int)(textWidth / GetCharWidth()));
    }

    /// <summary>
    /// Estimates the pixel Y position of a logical line using per-line
    /// character counts from the <see cref="LineIndexTree"/>.  O(log N).
    /// When wrapping is off (<paramref name="charsPerRow"/> = 0), each line
    /// is exactly one row, so the result is <c>lineIndex * rh</c>.
    /// </summary>
    /// <summary>
    /// Estimates the Y pixel offset for a given line index when wrapping is
    /// enabled. Lines can span multiple visual rows, so we approximate by
    /// dividing total chars before the line by chars-per-row.
    /// NOT used when wrapping is off — use <c>lineIndex * rh</c> instead.
    /// </summary>
    private static double EstimateWrappedLineY(long lineIndex, PieceTable table, int charsPerRow, double rh) {
        if (lineIndex <= 0) return 0;
        var charsBefore = (long)table.DocLineStartOfs(lineIndex);
        if (charsBefore < 0) return lineIndex * rh; // streaming load fallback
        var visualRows = charsPerRow > 0
            ? Math.Max(lineIndex, (long)Math.Ceiling((double)charsBefore / charsPerRow))
            : lineIndex;
        return visualRows * rh;
    }

    /// <summary>
    /// Inverse of <see cref="EstimateWrappedLineY"/>: maps a scroll-pixel offset
    /// to the logical line at the viewport top when wrapping is enabled.
    /// NOT used when wrapping is off — use <c>(long)(scrollY / rh)</c> instead.
    /// </summary>
    private static long EstimateWrappedTopLine(double scrollY, PieceTable table, long lineCount, int charsPerRow, double rh) {
        if (scrollY <= 0 || lineCount <= 0) return 0;
        var targetRow = (long)(scrollY / rh);
        if (charsPerRow <= 0) return Math.Clamp(targetRow, 0, lineCount - 1);
        var targetCharOfs = Math.Min((long)targetRow * charsPerRow, table.DocLength);
        var charBasedLine = table.LineFromDocOfs(targetCharOfs);
        return Math.Clamp(Math.Min(targetRow, charBasedLine), 0, lineCount - 1);
    }

    /// <summary>
    /// Builds or retrieves the current layout.
    /// Only the visible window of text is fetched and laid out (windowed layout).
    /// </summary>
    private LayoutResult EnsureLayout() {
        if (_layout != null) {
            return _layout;
        }
        if (_layoutFailed) {
            _layout = _layoutEngine.LayoutEmpty(
                new Typeface(FontFamily), FontSize, ForegroundBrush, 100);
            return _layout;
        }
        _perfSw.Restart();

        var doc = BackgroundPasteInProgress ? null : Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var boundsW = Bounds.Width > 0 ? Bounds.Width : 900;
        var extentW = Math.Max(100, boundsW - _gutterWidth);
        var textW = GetTextWidth(extentW);
        var lineCount = doc?.Table.LineCount ?? 0;

        try {
            if (doc != null && lineCount > 0) {
                LayoutWindowed(doc, lineCount, typeface, textW, extentW);
            } else {
                _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, textW);
                _extent = new Size(extentW, 0);
                RenderOffsetY = 0;
            }
        } catch {
            _layoutFailed = true;
            _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            throw;
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
        _viewport = availableSize;

        if (_layoutFailed) {
            _layout = _layoutEngine.LayoutEmpty(
                new Typeface(FontFamily), FontSize, ForegroundBrush, 100);
            return availableSize;
        }

        _perfSw.Restart();

        var doc = Document;
        var typeface = new Typeface(FontFamily);
        var rh = GetRowHeight();
        UpdateGutterWidth();
        var extentW = double.IsInfinity(availableSize.Width)
            ? 0
            : Math.Max(100, availableSize.Width - _gutterWidth);
        var textW = GetTextWidth(extentW);

        var lineCount = doc?.Table.LineCount ?? 0;

        try {
            if (doc != null && lineCount > 0) {
                LayoutWindowed(doc, lineCount, typeface, textW, extentW);
            } else {
                _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, textW);
                _extent = new Size(extentW, 0);
                RenderOffsetY = 0;
            }
        } catch {
            _layoutFailed = true;
            _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, textW);
            _extent = new Size(extentW, 0);
            RenderOffsetY = 0;
            throw;
        }

        _perfSw.Stop();
        PerfStats.Layout.Record(_perfSw.Elapsed.TotalMilliseconds);

        RaiseScrollInvalidated();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
        return availableSize;
    }

    /// <summary>
    /// Fetches only the text visible in the current scroll viewport and lays it out.
    /// Sets <see cref="_layout"/>, <see cref="_extent"/>, and <see cref="RenderOffsetY"/>.
    /// </summary>
    /// <remarks>
    /// The scroll math estimates total visual rows from character count and character width,
    /// so that the extent accounts for word-wrap and the scroll-to-line mapping works in
    /// visual-row units. For monospace fonts this is exact; for proportional fonts it's a
    /// close approximation.
    /// </remarks>
    private void LayoutWindowed(Document doc, long lineCount, Typeface typeface, double maxWidth, double extentWidth) {
        var rh = GetRowHeight();

        // Compute total visual rows and map scroll offset → top line.
        var charsPerRow = _wrapLines ? GetCharsPerRow(maxWidth) : 0;
        long totalVisualRows;
        long topLine;
        if (!_wrapLines) {
            // Wrapping off: each line tree entry = one visual row (exact).
            totalVisualRows = lineCount;
            topLine = Math.Clamp((long)(_scrollOffset.Y / rh), 0, Math.Max(0, lineCount - 1));
        } else {
            // Wrapping on: lines can span multiple rows (estimated).
            var totalChars = doc.Table.Length;
            totalVisualRows = charsPerRow > 0
                ? Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow))
                : lineCount;
            topLine = EstimateWrappedTopLine(_scrollOffset.Y, doc.Table, lineCount, charsPerRow, rh);
        }

        // For single-row scrolls (arrow buttons), constrain topLine to change
        // by at most ±1 from the previous frame.  This lets the incremental
        // render-offset logic use the actual cached line height instead of
        // the estimate, giving pixel-perfect smooth scrolling.
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

        // If showing the end of the document but topLine is too high to
        // fill the viewport (e.g. after deleting a line while scrolled to
        // the bottom), pull topLine back so the layout has enough content.
        if (bottomLine >= lineCount && lineCount > visibleRows) {
            topLine = Math.Min(topLine, lineCount - visibleRows);
        }

        var startOfs = topLine > 0 ? doc.Table.DocLineStartOfs(topLine) : 0L;
        long endOfs;
        if (bottomLine >= lineCount) {
            endOfs = doc.Table.Length;
        } else {
            endOfs = doc.Table.DocLineStartOfs(bottomLine);
        }

        // During streaming/paged loads, LineStartOfs returns -1 when the
        // required page isn't in memory yet. Layout empty text — the next
        // ProgressChanged event will trigger re-layout once data is available.
        if (startOfs < 0 || endOfs < 0) {
            _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, maxWidth);
            _extent = new Size(extentWidth, totalVisualRows * rh);
            RenderOffsetY = 0;
            return;
        }

        var len = (int)(endOfs - startOfs);

        // Sanity check: the visible window spans at most visibleRows lines,
        // each capped at MaxPseudoLine+1 doc chars.  If len exceeds that, or
        // the computed range falls outside the actual table content, the line
        // tree and piece table are in an inconsistent state (e.g. intermediate
        // state during undo or document reload).  Skip this layout pass —
        // the next one will see the consistent state.
        var docLen = doc.Table.DocLength;
        var maxLayoutChars = visibleRows * (PieceTable.MaxPseudoLine + 1);
        if (len > maxLayoutChars || startOfs + len > docLen) {
            _layout = _layoutEngine.LayoutEmpty(typeface, FontSize, ForegroundBrush, maxWidth);
            _extent = new Size(extentWidth, totalVisualRows * rh);
            RenderOffsetY = 0;
            return;
        }

        // Layout one line at a time directly from the PieceTable so we
        // never materialize multiple lines into a single string.
        _layout = _layoutEngine.LayoutLines(
            doc.Table, topLine, bottomLine, typeface, FontSize, ForegroundBrush,
            maxWidth, startOfs, lineCount, doc.Table.DocLength);
        _layout.TopLine = topLine;

        // When the layout covers the entire document, use exact height
        // instead of the estimate — gives pixel-perfect scrolling on small files.
        var extentHeight = (topLine == 0 && bottomLine >= lineCount)
            ? _layout.TotalHeight
            : totalVisualRows * rh;
        var contentWidth = !_wrapLines
            ? _gutterWidth + doc.Table.MaxLineLength * GetCharWidth()
            : extentWidth;
        var oldHMax = HScrollMaximum;
        _extent = new Size(Math.Max(extentWidth, contentWidth), extentHeight);
        if (Math.Abs(HScrollMaximum - oldHMax) > 0.5) HScrollChanged?.Invoke();

        // Compute render offset.  For small topLine changes (arrow keys, wheel)
        // use an incremental offset based on the actual cached line height so
        // each rh of scroll produces exactly rh of visual movement.
        // For large jumps (thumb drag, page-down) use the formula estimate.
        var deltaTop = topLine - _winTopLine;

        if (_winTopLine >= 0 && deltaTop == 0) {
            // topLine unchanged — pure scroll, trivially smooth.
            RenderOffsetY = _winRenderOffsetY - (_scrollOffset.Y - _winScrollOffset);
        } else if (_winTopLine >= 0 && deltaTop == 1 && _winFirstLineHeight > 0) {
            // topLine advanced by 1.  Compensate for the departed line's
            // actual height (not the estimate) to avoid a visual jump.
            RenderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                + _winFirstLineHeight;
        } else if (_winTopLine >= 0 && deltaTop == -1 && _layout.Lines.Count > 0) {
            // topLine retreated by 1.  The new first line was prepended;
            // subtract its actual height to keep content in place.
            RenderOffsetY = _winRenderOffsetY
                - (_scrollOffset.Y - _winScrollOffset)
                - _layout.Lines[0].HeightInRows * rh;
        } else {
            // First layout or large jump.
            RenderOffsetY = _wrapLines
                ? EstimateWrappedLineY(topLine, doc.Table, charsPerRow, rh) - _scrollOffset.Y
                : topLine * rh - _scrollOffset.Y;
        }

        // Safety: prevent any remaining gap at the viewport top.
        if (RenderOffsetY > 0) {
            RenderOffsetY = 0;
        }

        // Safety: prevent gap at the viewport bottom.  If the layout has
        // enough content to fill the viewport but the render offset pushes
        // it too far above, snap it down so the content reaches the bottom.
        if (_layout.TotalHeight >= _viewport.Height) {
            var contentBottom = RenderOffsetY + _layout.TotalHeight;
            if (contentBottom < _viewport.Height) {
                RenderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // When at max scroll and the layout includes the end of the document,
        // anchor content bottom to viewport bottom so the last line is flush.
        // Only at max scroll — otherwise scrolling up from the bottom would be stuck.
        var scrollMax = _extent.Height - _viewport.Height;
        if (bottomLine >= lineCount && _scrollOffset.Y >= scrollMax - 1.0
                && _layout.TotalHeight >= _viewport.Height) {
            var contentBottom = RenderOffsetY + _layout.TotalHeight;
            if (contentBottom != _viewport.Height) {
                RenderOffsetY = _viewport.Height - _layout.TotalHeight;
            }
        }

        // Cache state for next incremental frame.
        _winTopLine = topLine;
        _winScrollOffset = _scrollOffset.Y;
        _winRenderOffsetY = RenderOffsetY;
        _winFirstLineHeight = _layout.Lines.Count > 0 ? _layout.Lines[0].HeightInRows * rh : rh;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    public override void Render(DrawingContext context) {
        _perfSw.Restart();

        var layout = EnsureLayout();
        var doc = BackgroundPasteInProgress ? null : Document;

        // Background
        context.FillRectangle(_theme.EditorBackground, new Rect(Bounds.Size));

        // Gutter (line numbers)
        DrawGutter(context, layout);

        // Clip text area so horizontally-scrolled content doesn't paint over the gutter.
        using var _ = _scrollOffset.X > 0
            ? context.PushClip(new Rect(_gutterWidth, 0, Bounds.Width - _gutterWidth, Bounds.Height))
            : default;

        // Column guide line
        if (_wrapLines && _wrapLinesAt >= 1) {
            var guideX = 2 + TextOriginX + _wrapLinesAt * GetCharWidth();
            if (guideX < Bounds.Width) {
                context.DrawLine(_theme.GuideLinePen, new Point(guideX, 0), new Point(guideX, Bounds.Height));
            }
        }

        // Draw selection rectangles behind text
        if (doc != null) {
            if (doc.ColumnSel is { } colSel) {
                DrawColumnSelection(context, layout, colSel);
            } else if (!doc.Selection.IsEmpty) {
                DrawSelection(context, layout, doc.Selection);
            }
        }

        // Draw each visible line's text
        var rh = layout.RowHeight;
        foreach (var line in layout.Lines) {
            var y = line.Row * rh + RenderOffsetY;
            if (y + line.HeightInRows * rh < 0) {
                continue; // above viewport
            }
            if (y > Bounds.Height) {
                break; // below viewport
            }
            line.Layout.Draw(context, new Point(TextOriginX, y));
            if (_showWhitespace) {
                DrawWhitespace(context, layout, line, y, rh);
            }
        }

        // Draw caret (hidden during any scroll-drag operation)
        var scrollDrag = ScrollBar?.IsDragging ?? false;
        if (doc != null && _caretVisible && IsFocused && !_middleDrag && !scrollDrag
            && !IsLoading) {
            if (doc.ColumnSel is { } colSelCarets) {
                DrawMultiCarets(context, layout, colSelCarets);
            } else {
                DrawCaret(context, layout, doc.Selection.Caret);
            }
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
        var firstLineIdx = layout.TopLine;

        for (var i = 0; i < layout.Lines.Count; i++) {
            var line = layout.Lines[i];
            var y = line.Row * rh + RenderOffsetY;
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

    private void DrawWhitespace(DrawingContext ctx, LayoutResult layout, LayoutLine line, double y, double rh) {
        var table = Document?.Table;
        if (table == null || line.CharLen <= 0) return;

        var docOfs = layout.ViewportBase + line.CharStart;
        var bufOfs = table.DocOfsToBufOfs(docOfs);
        if (bufOfs < 0 || line.CharLen > table.MaxGetTextLength) return;
        if (bufOfs + line.CharLen > table.Length) return;
        var text = table.GetText(bufOfs, line.CharLen);
        var typeface = new Typeface(FontFamily);

        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (ch != ' ' && ch != '\t' && ch != '\u00A0') continue;

            var hit = line.Layout.HitTestTextPosition(i);
            var x = TextOriginX + hit.X;

            if (ch == '\t') {
                // Draw arrow spanning the tab's width
                var hitNext = line.Layout.HitTestTextPosition(i + 1);
                var x1 = TextOriginX + hit.X;
                var x2 = TextOriginX + hitNext.X;
                if (x2 <= x1) continue;

                const double pad = 2;
                const double arrowSize = 3;
                var midY = y + rh / 2;
                var left = x1 + pad;
                var right = x2 - pad;
                if (right - left < arrowSize + 1) continue;

                // Horizontal line
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(left, midY), new Point(right, midY));

                // Arrowhead
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(right - arrowSize, midY - arrowSize),
                    new Point(right, midY));
                ctx.DrawLine(_theme.WhitespaceGlyphPen,
                    new Point(right - arrowSize, midY + arrowSize),
                    new Point(right, midY));
            } else {
                // Space → · (U+00B7), NBSP → ␣ (U+2423)
                var glyph = ch == ' ' ? "\u00B7" : "\u2423";
                using var tl = new TextLayout(glyph, typeface, FontSize,
                    _theme.WhitespaceGlyphBrush, textAlignment: TextAlignment.Left);
                // Center the glyph in the character cell
                var glyphW = tl.WidthIncludingTrailingWhitespace;
                var cw = GetCharWidth();
                var dx = (cw - glyphW) / 2;
                tl.Draw(ctx, new Point(x + dx, y));
            }
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
            var lineY = line.Row * rh + RenderOffsetY;
            if (lineY + line.HeightInRows * rh < 0) {
                continue;
            }
            if (lineY > Bounds.Height) {
                break;
            }
            if (line.CharEnd <= localStart || line.CharStart >= localEnd) {
                // Blank lines have CharEnd == CharStart, so the check above
                // rejects them when they sit exactly at localStart.  Let them
                // through when they fall inside [localStart, localEnd).
                if (!(line.CharLen == 0 && line.CharStart >= localStart && line.CharStart < localEnd))
                    continue;
            }

            var rangeStart = Math.Max(0, localStart - line.CharStart);
            var rangeEnd = Math.Min(line.CharLen, localEnd - line.CharStart);
            var rangeLen = rangeEnd - rangeStart;

            if (rangeLen <= 0) {
                // Blank line fully inside selection: show a 1-char-wide placeholder
                // so the user can see that the selection spans across it.
                if (line.CharLen == 0) {
                    rects.Add(new Rect(TextOriginX, lineY, GetCharWidth(), rh));
                }
                continue;
            }

            foreach (var rect in line.Layout.HitTestTextRange(rangeStart, rangeLen)) {
                rects.Add(new Rect(rect.X + TextOriginX, lineY + rect.Y, rect.Width, rect.Height));
            }
        }

        if (rects.Count == 0) {
            return;
        }

        if (rects.Count == 1) {
            const double r = SelCornerRadius;
            FillRoundedRect(context, SelectionBrush, rects[0], r, r, r, r);
        } else {
            // Build a single outline path for all contiguous rects so there
            // are no internal horizontal edges (which cause sub-pixel seams).
            FillSelectionPath(context, SelectionBrush, rects);
        }
    }

    /// <summary>
    /// Traces the outer contour of a contiguous list of rects as a single
    /// filled path.  Rounded corners are applied only at the four outermost
    /// corners (top of first rect, bottom of last rect).
    /// </summary>
    private static void FillSelectionPath(
        DrawingContext ctx, IBrush brush, List<Rect> rects) {
        const double r = SelCornerRadius;
        var first = rects[0];
        var last = rects[^1];

        var g = new StreamGeometry();
        using (var c = g.Open()) {
            // Start at top-left of first rect, trace clockwise.

            // ── Top edge ──
            c.BeginFigure(new Point(first.Left + r, first.Top), true);
            c.LineTo(new Point(first.Right - r, first.Top));
            c.ArcTo(new Point(first.Right, first.Top + r),
                new Size(r, r), 0, false, SweepDirection.Clockwise);

            // ── Right edge (top → bottom) ──
            for (var i = 0; i < rects.Count - 1; i++) {
                var cur = rects[i];
                var next = rects[i + 1];
                c.LineTo(new Point(cur.Right, cur.Bottom));
                if (Math.Abs(cur.Right - next.Right) > 0.5) {
                    c.LineTo(new Point(next.Right, next.Top));
                }
            }

            // ── Bottom edge ──
            c.LineTo(new Point(last.Right, last.Bottom - r));
            c.ArcTo(new Point(last.Right - r, last.Bottom),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(last.Left + r, last.Bottom));
            c.ArcTo(new Point(last.Left, last.Bottom - r),
                new Size(r, r), 0, false, SweepDirection.Clockwise);

            // ── Left edge (bottom → top) ──
            for (var i = rects.Count - 1; i > 0; i--) {
                var cur = rects[i];
                var prev = rects[i - 1];
                c.LineTo(new Point(cur.Left, cur.Top));
                if (Math.Abs(cur.Left - prev.Left) > 0.5) {
                    c.LineTo(new Point(prev.Left, cur.Top));
                }
            }

            // ── Close at top-left ──
            c.LineTo(new Point(first.Left, first.Top + r));
            c.ArcTo(new Point(first.Left + r, first.Top),
                new Size(r, r), 0, false, SweepDirection.Clockwise);
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, g);
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
        var y = rect.Y + RenderOffsetY;
        if (y + rect.Height < 0 || y > Bounds.Height) {
            return;
        }

        if (_overwriteMode) {
            // Block caret: character-width, translucent.
            var caretWidth = rect.Height * 0.55; // fallback: ~em-width
            if (localCaret < totalChars) {
                var nextRect = _layoutEngine.GetCaretBounds(localCaret + 1, layout);
                var w = nextRect.X - rect.X;
                if (w > 0) caretWidth = w;
            }
            var caretColor = CaretBrush is ISolidColorBrush scb
                ? Color.FromArgb(100, scb.Color.R, scb.Color.G, scb.Color.B)
                : Color.FromArgb(100, 0, 0, 0);
            context.FillRectangle(new SolidColorBrush(caretColor),
                new Rect(rect.X + TextOriginX, y, caretWidth, rect.Height));
        } else {
            context.FillRectangle(CaretBrush, new Rect(rect.X + TextOriginX, y, 1.5, rect.Height));
        }
    }

    // -------------------------------------------------------------------------
    // Column selection rendering
    // -------------------------------------------------------------------------

    private void DrawColumnSelection(DrawingContext context, LayoutResult layout, ColumnSelection colSel) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        var table = doc.Table;
        var sels = colSel.Materialize(table, _indentWidth);
        var rh = layout.RowHeight;
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
        var rects = new List<Rect>();

        for (var i = 0; i < sels.Count; i++) {
            var s = sels[i];
            if (s.IsEmpty) {
                continue;
            }
            var localStart = (int)(s.Start - layout.ViewportBase);
            var localEnd = (int)(s.End - layout.ViewportBase);
            if (localEnd < 0 || localStart > totalChars) {
                continue;
            }
            localStart = Math.Clamp(localStart, 0, totalChars);
            localEnd = Math.Clamp(localEnd, 0, totalChars);
            if (localStart == localEnd) {
                continue;
            }

            foreach (var line in layout.Lines) {
                if (line.CharEnd <= localStart || line.CharStart >= localEnd) {
                    continue;
                }
                var lineY = line.Row * rh + RenderOffsetY;
                if (lineY + line.HeightInRows * rh < 0 || lineY > Bounds.Height) {
                    continue;
                }
                var rangeStart = Math.Max(0, localStart - line.CharStart);
                var rangeEnd = Math.Min(line.CharLen, localEnd - line.CharStart);
                var rangeLen = rangeEnd - rangeStart;
                if (rangeLen <= 0) {
                    continue;
                }
                foreach (var rect in line.Layout.HitTestTextRange(rangeStart, rangeLen)) {
                    rects.Add(new Rect(rect.X + TextOriginX, lineY + rect.Y, rect.Width, rect.Height));
                }
            }
        }

        if (rects.Count == 0) {
            return;
        }
        if (rects.Count == 1) {
            const double r = SelCornerRadius;
            FillRoundedRect(context, SelectionBrush, rects[0], r, r, r, r);
        } else {
            FillSelectionPath(context, SelectionBrush, rects);
        }
    }

    private void DrawMultiCarets(DrawingContext context, LayoutResult layout, ColumnSelection colSel) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        var carets = colSel.MaterializeCarets(doc.Table, _indentWidth);
        foreach (var caret in carets) {
            DrawCaret(context, layout, caret);
        }
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
        if (doc == null) return;
        FlushCompound();

        if (doc.ColumnSel != null) {
            // Column selection: always materializes (selections are small).
            var text = doc.GetColumnSelectedText(_indentWidth);
            if (string.IsNullOrEmpty(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
            return;
        }

        if (doc.Selection.IsEmpty) return;
        await CopySelectionToClipboard(doc);
    }

    public async Task CutAsync() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();

        if (doc.ColumnSel != null) {
            var text = doc.GetColumnSelectedText(_indentWidth);
            if (string.IsNullOrEmpty(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
            _editSw.Restart();
            doc.DeleteColumnSelectionContent(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
            return;
        }

        if (doc.Selection.IsEmpty) return;
        if (!await CopySelectionToClipboard(doc)) return;
        _editSw.Restart();
        doc.DeleteSelection();
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Copies the current stream selection to the system clipboard.
    /// Uses the native clipboard service when available (zero managed allocation);
    /// falls back to Avalonia's <c>SetTextAsync</c> with <c>string.Create</c>.
    /// </summary>
    private async Task<bool> CopySelectionToClipboard(Document doc) {
        var sel = doc.Selection;
        var nativeClip = NativeClipboardDiscovery.Service;
        if (nativeClip != null) {
            if (!nativeClip.Copy(doc.Table, sel.Start, sel.Len)) return false;
            // Push to ring only for small selections.
            if (sel.Len <= _clipboardRing.MaxEntryChars) {
                var ringText = doc.GetSelectedText();
                if (ringText != null) _clipboardRing.Push(ringText);
            }
        } else {
            // Fallback: materialize via string.Create + Avalonia clipboard.
            var text = doc.GetSelectedText();
            if (text == null) {
                CopyTooLarge?.Invoke(sel.Len);
                return false;
            }
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return false;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
        }
        return true;
    }

    public async Task PasteAsync() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _preferredCaretX = -1;
        _editSw.Restart();

        // Column mode paste always needs the full string (for line splitting).
        // Native streaming paste is only used for normal (non-column) mode.
        var nativeClip = NativeClipboardDiscovery.Service;
        if (nativeClip != null && doc.ColumnSel == null) {
            const long LargePasteThreshold = 1024 * 1024; // 1M chars
            var clipSize = nativeClip.GetClipboardCharCount();

            if (clipSize > LargePasteThreshold) {
                await PasteLargeAsync(doc, nativeClip, clipSize);
                // Post to dispatcher so the layout cycle resolves
                // scrollbar visibility before we compute scroll position.
                Dispatcher.UIThread.Post(ScrollCaretIntoView);
            } else {
                PasteSmall(doc, nativeClip);
                ScrollCaretIntoView();
            }
        } else {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { _editSw.Stop(); return; }
#pragma warning disable CS0618 // GetTextAsync is deprecated but TryGetTextAsync requires IAsyncDataTransfer
            var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (string.IsNullOrEmpty(text)) { _editSw.Stop(); return; }
            // Normalize Windows line endings to LF
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            _clipboardRing.Push(text);
            if (doc.ColumnSel is { } colSel) {
                var lines = text.Split('\n');
                if (lines.Length == colSel.LineCount) {
                    doc.PasteAtCursors(lines, _indentWidth);
                } else {
                    doc.InsertAtCursors(text, _indentWidth);
                }
            } else {
                doc.Insert(text);
            }
            ScrollCaretIntoView();
        }

        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Small paste: clipboard → in-memory add buffer → insert.</summary>
    private void PasteSmall(Document doc, INativeClipboardService nativeClip) {
        var ofs = doc.Selection.Start;
        var replacing = !doc.Selection.IsEmpty;
        if (replacing) {
            doc.History.BeginCompound();
            var pieces = doc.Table.CapturePieces(ofs, doc.Selection.Len);
            var lineInfo = doc.Table.CaptureLineInfo(ofs, doc.Selection.Len);
            var delEdit = lineInfo is var (sl, ll, dll)
                ? new DeleteEdit(ofs, doc.Selection.Len, pieces, sl, ll, dll)
                : new DeleteEdit(ofs, doc.Selection.Len, pieces);
            doc.History.Push(delEdit, doc.Table, doc.Selection);
        }
        var addBufStart = doc.Table.AddBufferLength;
        nativeClip.Paste(doc.Table, null, default);
        var totalLen = (int)(doc.Table.AddBufferLength - addBufStart);
        if (totalLen > 0) {
            doc.History.Push(
                new SpanInsertEdit(ofs, addBufStart, totalLen), doc.Table, doc.Selection);
        }
        if (replacing) {
            doc.History.EndCompound();
        }
        doc.Selection = Selection.Collapsed(ofs + totalLen);
        doc.RaiseChanged();
    }

    /// <summary>
    /// Large paste: clipboard → file on disk → PagedFileBuffer → piece insert.
    /// The file stays on disk and is paged into memory on demand (~16 MB).
    /// </summary>
    private async Task PasteLargeAsync(Document doc, INativeClipboardService nativeClip,
        long clipCharCount) {

        var ofs = doc.Selection.Start;
        var selBefore = doc.Selection;
        var replacing = !selBefore.IsEmpty;

        // Capture delete pieces before going to background.
        Piece[]? deletePieces = null;
        (int StartLine, int[] LineLengths, int[] DocLineLengths)? deleteLineInfo = null;
        if (replacing) {
            deletePieces = doc.Table.CapturePieces(ofs, selBefore.Len);
            deleteLineInfo = doc.Table.CaptureLineInfo(ofs, selBefore.Len);
        }

        // Find the active tab ID for the file name.
        var mw = TopLevel.GetTopLevel(this) as MainWindow;
        var tabId = mw?._activeTab?.Id ?? Guid.NewGuid().ToString("N")[..12];
        var bufFileIdx = doc.Table.Buffers.Count; // next available index
        var filePath = SessionStore.AllocateAddBufPath(tabId, bufFileIdx);

        // Phase 1 (UI thread): Stream clipboard → UTF-8 file on disk.
        using (var fs = File.Create(filePath)) {
            nativeClip.PasteToStream(fs, null, default);
        }

        // Phase 2 (background): Scan the file to build page table + line index.
        var savedIsEditBlocked = IsEditBlocked;
        IsEditBlocked = true;
        BackgroundPasteInProgress = true;
        BackgroundPasteChanged?.Invoke(true);
        InvalidateVisual();

        var byteLen = new FileInfo(filePath).Length;
        var paged = new PagedFileBuffer(filePath, byteLen);
        var tcs = new TaskCompletionSource();
        paged.LoadComplete += () => tcs.TrySetResult();
        paged.StartLoading(default);
        await tcs.Task;

        var pastedBufIdx = doc.Table.RegisterBuffer(paged);
        var totalLen = (int)paged.Length;

        // Phase 3 (background): Delete + insert piece + rebuild line tree.
        await Task.Run(() => {
            if (replacing) {
                doc.Table.Delete(ofs, selBefore.Len);
            }
            doc.Table.InsertFromBuffer(ofs, pastedBufIdx, 0, totalLen);
        });

        // Phase 4 (UI thread): Record history, update selection, unblock.
        doc.RecordBackgroundPaste(ofs, selBefore, 0, totalLen,
            replacing, deletePieces, deleteLineInfo, pastedBufIdx);

        BackgroundPasteInProgress = false;
        BackgroundPasteChanged?.Invoke(false);
        IsEditBlocked = savedIsEditBlocked;
    }

    /// <summary>
    /// Pastes from the clipboard ring with inline cycling. First press pastes
    /// the most recent entry; subsequent presses (while Ctrl is held) replace
    /// the pasted text with the next older entry.
    /// </summary>
    public void PasteMore() {
        var doc = Document;
        if (doc == null || _clipboardRing.Count == 0) return;
        FlushCompound();
        if (!_isClipboardCycling) {
            // Start a new cycling session.
            _isClipboardCycling = true;
            _clipboardCycleIndex = 0;
        } else {
            // Cycle to the next entry — undo the previous paste first.
            _clipboardCycleIndex = (_clipboardCycleIndex + 1) % _clipboardRing.Count;
            if (_cycleInsertedLength > 0) {
                var caret = doc.Selection.Caret;
                doc.Selection = new Selection(caret - _cycleInsertedLength, caret);
                doc.DeleteSelection();
            }
        }
        var text = _clipboardRing.Get(_clipboardCycleIndex);
        if (text == null) return;
        _preferredCaretX = -1;
        _editSw.Restart();
        doc.Insert(text);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        _cycleInsertedLength = text.Length;
        InvalidateLayout();
        ResetCaretBlink();
        ClipboardCycleStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Confirms the current clipboard cycling selection. Called when the
    /// user releases Ctrl (or performs any other action).
    /// </summary>
    public void ConfirmClipboardCycle() {
        if (!_isClipboardCycling) return;
        _isClipboardCycling = false;
        _clipboardCycleIndex = 0;
        _cycleInsertedLength = 0;
        ClipboardCycleStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pastes a specific entry from the clipboard ring by index. Used by the
    /// ClipboardRing popup dialog.
    /// </summary>
    public void PasteFromRing(int index) {
        var doc = Document;
        if (doc == null) return;
        var text = _clipboardRing.Get(index);
        if (text == null) return;
        FlushCompound();
        _preferredCaretX = -1;
        _editSw.Restart();
        doc.Insert(text);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>Whether the editor is currently in a PasteMore cycling session.</summary>
    public bool IsClipboardCycling => _isClipboardCycling;

    /// <summary>Current cycling index (0-based) into the clipboard ring.</summary>
    public int ClipboardCycleIndex => _clipboardCycleIndex;

    /// <summary>Raised when clipboard cycling starts, advances, or ends.</summary>
    public event EventHandler? ClipboardCycleStatusChanged;

    /// <summary>
    /// Raised when a Copy or Cut falls back to the Avalonia clipboard and
    /// the selection exceeds <see cref="Document.MaxCopyLength"/>.
    /// The argument is the selection length in characters.
    /// </summary>
    public event Action<long>? CopyTooLarge;

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
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        var edit = doc.Undo();
        if (!IsBulkReplace(edit)) {
            ScrollCaretIntoView();
        }
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        var edit = doc.Redo();
        if (!IsBulkReplace(edit)) {
            ScrollCaretIntoView();
        }
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private static bool IsBulkReplace(IDocumentEdit? edit) =>
        edit is UniformBulkReplaceEdit or VaryingBulkReplaceEdit;

    public void PerformSelectAll() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.Selection = new Selection(0L, doc.Table.DocLength);
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
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Keyboard input
    // -------------------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        if (IsEditBlocked) { e.Handled = true; return; }
        if (_inIncrementalSearch) {
            HandleIncrementalSearchChar(e.Text ?? "");
            e.Handled = true;
            return;
        }
        var doc = Document;
        if (doc == null || string.IsNullOrEmpty(e.Text)) {
            return;
        }
        _preferredCaretX = -1;

        if (doc.ColumnSel != null) {
            Coalesce("col-char");
            _editSw.Restart();
            doc.InsertAtCursors(e.Text, _indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            e.Handled = true;
            InvalidateLayout();
            ResetCaretBlink();
            return;
        }

        Coalesce("char");

        // In overwrite mode, select the next character(s) so Insert replaces them.
        // Don't overwrite past line endings (standard overwrite behavior).
        if (_overwriteMode && doc.Selection.IsEmpty && e.Text != null) {
            var caret = doc.Selection.Caret;
            var table = doc.Table;
            var bufCaret = table.DocOfsToBufOfs(caret);
            var bufLen = table.Length;
            var charsToOverwrite = 0;
            for (var i = 0; i < e.Text.Length && bufCaret + charsToOverwrite < bufLen; i++) {
                var ch = table.GetText(bufCaret + charsToOverwrite, 1);
                if (ch[0] is '\r' or '\n') break;
                charsToOverwrite++;
            }
            if (charsToOverwrite > 0) {
                doc.Selection = new Selection(caret, caret + charsToOverwrite);
            }
        }

        _editSw.Restart();
        doc.Insert(e.Text!);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        e.Handled = true;
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Command dispatch (called by MainWindow after key → command resolution)
    // -------------------------------------------------------------------------


    private void PerformColumnSelectVertical(Document doc, int delta) {
        if (_wrapLines || doc.Table.HasPseudoLines) return;
        FlushCompound();
        var table = doc.Table;
        if (doc.ColumnSel is { } colSel) {
            // Already in column mode — extend by one line.
            var newLine = Math.Clamp(colSel.ActiveLine + delta, 0, (int)table.LineCount - 1);
            doc.ColumnSel = colSel.ExtendTo(newLine, colSel.ActiveCol);
        } else {
            // Enter column mode from current caret.
            var caret = doc.Selection.Caret;
            var line = (int)table.LineFromDocOfs(caret);
            var col = ColumnSelection.OfsToCol(table, caret, _indentWidth);
            var targetLine = Math.Clamp(line + delta, 0, (int)table.LineCount - 1);
            doc.ColumnSel = new ColumnSelection(line, col, targetLine, col);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void PerformColumnSelectHorizontal(Document doc, int delta) {
        if (_wrapLines || doc.Table.HasPseudoLines) return;
        FlushCompound();
        if (doc.ColumnSel is { } colSel) {
            var newCol = ColumnSelection.NextCharCol(
                doc.Table, colSel.ActiveLine, colSel.ActiveCol, delta, _indentWidth);
            doc.ColumnSel = colSel.ExtendTo(colSel.ActiveLine, newCol);
        } else {
            // Enter column mode from current caret.
            var caret = doc.Selection.Caret;
            var line = (int)doc.Table.LineFromDocOfs(caret);
            var col = ColumnSelection.OfsToCol(doc.Table, caret, _indentWidth);
            var newCol = ColumnSelection.NextCharCol(doc.Table, line, col, delta, _indentWidth);
            doc.ColumnSel = new ColumnSelection(line, col, line, newCol);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Plain Left/Right in column mode: collapse selection or shift carets.
    /// </summary>
    private void PerformColumnMoveHorizontal(Document doc, int delta) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        if (colSel.LeftCol != colSel.RightCol) {
            // Has selection → collapse to the edge in the movement direction.
            doc.ColumnSel = delta < 0 ? colSel.CollapseToLeft() : colSel.CollapseToRight();
        } else {
            var newCol = ColumnSelection.NextCharCol(
                doc.Table, colSel.ActiveLine, colSel.ActiveCol, delta, _indentWidth);
            doc.ColumnSel = colSel.MoveColumnsTo(newCol);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Plain Up/Down in column mode: collapse any selection, then shift the
    /// entire caret group by one line.
    /// </summary>
    private void PerformColumnMoveVertical(Document doc, int delta) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        if (colSel.LeftCol != colSel.RightCol) {
            colSel = colSel.CollapseToLeft();
        }
        var maxLine = (int)doc.Table.LineCount - 1;
        doc.ColumnSel = colSel.ShiftLines(delta, maxLine);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Ctrl+Left/Right in column mode: move all carets to the next word boundary.
    /// Uses the first caret line as the reference for computing the word delta.
    /// </summary>
    private void PerformColumnMoveWord(Document doc, int direction) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        // Collapse selection first if any.
        if (colSel.LeftCol != colSel.RightCol) {
            colSel = direction < 0 ? colSel.CollapseToLeft() : colSel.CollapseToRight();
        }
        var wordCol = ColumnSelection.FindWordBoundaryCol(doc.Table, colSel.TopLine, colSel.LeftCol, direction, _indentWidth);
        doc.ColumnSel = colSel.MoveColumnsTo(wordCol);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Ctrl+Shift+Left/Right in column mode: extend selection to word boundary.
    /// </summary>
    private void PerformColumnSelectWord(Document doc, int direction) {
        if (doc.ColumnSel is not { } colSel) return;
        var wordCol = ColumnSelection.FindWordBoundaryCol(doc.Table, colSel.ActiveLine, colSel.ActiveCol, direction, _indentWidth);
        doc.ColumnSel = colSel.ExtendTo(colSel.ActiveLine, wordCol);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Returns the maximum end-of-line column across all lines in the column selection.
    /// </summary>
    private int MaxEndColumn(Document doc, ColumnSelection colSel) {
        var table = doc.Table;
        var max = 0;
        for (var line = colSel.TopLine; line <= colSel.BottomLine; line++) {
            var endCol = ColumnSelection.EndOfLineCol(table, line, _indentWidth);
            if (endCol > max) max = endCol;
        }
        return max;
    }

    public void RegisterCommands() {
        // Column-mode intercepts: alternative behavior for commands when column
        // selection is active. Returns true if fully handled, false to fall
        // through to normal handling (e.g. Edit.Newline exits column mode first).
        var columnIntercepts = new Dictionary<Command, Func<Document, bool>>();

        void ColIntercept(Command cmd, Func<Document, bool> handler) =>
            columnIntercepts[cmd] = handler;

        // Local helper: wraps each editor command with the standard preamble.
        void Reg(Command cmd, Action<Document> action,
                 bool isVerticalNav = false, bool isColumnAware = false,
                 Func<bool>? canExecute = null) {
            cmd.Wire(() => {
                if (IsEditBlocked && cmd.Category == "Edit"
                    && cmd != Cmd.EditSelectAll && cmd != Cmd.EditSelectWord
                    && cmd != Cmd.EditExpandSelection && cmd != Cmd.EditCopy
                    && cmd != Cmd.EditToggleOverwrite) return;
                var doc = Document;
                if (doc == null) return;
                if (_isClipboardCycling && cmd != Cmd.EditPasteMore) ConfirmClipboardCycle();
                if (!isVerticalNav) _preferredCaretX = -1;

                if (cmd == Cmd.NavColumnSelectUp || cmd == Cmd.NavColumnSelectDown) {
                    var delta = cmd == Cmd.NavColumnSelectUp ? -1 : +1;
                    PerformColumnSelectVertical(doc, delta);
                    return;
                }

                if (cmd == Cmd.NavColumnSelectLeft || cmd == Cmd.NavColumnSelectRight) {
                    var delta = cmd == Cmd.NavColumnSelectLeft ? -1 : +1;
                    PerformColumnSelectHorizontal(doc, delta);
                    return;
                }

                if (doc.ColumnSel != null) {
                    if (columnIntercepts.TryGetValue(cmd, out var intercept) && intercept(doc))
                        return;
                    if (!isColumnAware) doc.ClearColumnSelection(_indentWidth);
                }

                action(doc);
            }, canExecute: canExecute);
        }

        // -- Edit commands --

        Reg(Cmd.EditBackspace, doc => {
            Coalesce("backspace");
            _editSw.Restart();
            doc.DeleteBackward();
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditDelete, _ => EditDelete(), isColumnAware: true,
            canExecute: () => HasSelection() || (Document is { } d && d.Selection.Caret < d.Table.DocLength));
        Reg(Cmd.EditUndo, _ => PerformUndo(), canExecute: () => Document?.CanUndo == true);
        Reg(Cmd.EditRedo, _ => PerformRedo(), canExecute: () => Document?.CanRedo == true);
        Reg(Cmd.EditCut, doc => { _ = CutAsync(); }, isColumnAware: true,
            canExecute: HasSelection);
        Reg(Cmd.EditCopy, doc => { _ = CopyAsync(); }, isColumnAware: true,
            canExecute: HasSelection);
        Reg(Cmd.EditPaste, doc => { _ = PasteAsync(); }, isColumnAware: true);
        Reg(Cmd.EditPasteMore, _ => PasteMore(),
            canExecute: () => _clipboardRing.Count > 1);
        Reg(Cmd.EditSelectAll, _ => PerformSelectAll());
        Reg(Cmd.EditSelectWord, _ => PerformSelectWord());
        Reg(Cmd.EditExpandSelection, _ => PerformExpandSelection());
        Reg(Cmd.EditDeleteLine, _ => PerformDeleteLine());
        Reg(Cmd.EditMoveLineUp, _ => PerformMoveLineUp());
        Reg(Cmd.EditMoveLineDown, _ => PerformMoveLineDown());
        Reg(Cmd.EditUpperCase, _ => PerformTransformCase(CaseTransform.Upper),
            canExecute: HasSelection);
        Reg(Cmd.EditLowerCase, _ => PerformTransformCase(CaseTransform.Lower),
            canExecute: HasSelection);
        Reg(Cmd.EditProperCase, _ => PerformTransformCase(CaseTransform.Proper),
            canExecute: HasSelection);

        Reg(Cmd.EditToggleOverwrite, _ => {
            OverwriteMode = !OverwriteMode;
        });

        Reg(Cmd.EditNewline, doc => {
            FlushCompound();
            _editSw.Restart();
            doc.Insert(doc.LineEndingInfo.NewlineString);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditTab, doc => {
            Coalesce("tab");
            _editSw.Restart();
            doc.Insert("\t");
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        // -- Navigation: horizontal --

        Reg(Cmd.NavMoveLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, false, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, false, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveWordLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectWordLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, true, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveWordRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectWordRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, true, true); }, isColumnAware: true);

        // -- Navigation: vertical --

        Reg(Cmd.NavMoveUp, doc => { FlushCompound(); MoveCaretVertical(doc, -1, false); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavSelectUp, doc => { FlushCompound(); MoveCaretVertical(doc, -1, true); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavMoveDown, doc => { FlushCompound(); MoveCaretVertical(doc, +1, false); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavSelectDown, doc => { FlushCompound(); MoveCaretVertical(doc, +1, true); }, isVerticalNav: true, isColumnAware: true);

        // -- Navigation: home/end --

        Reg(Cmd.NavMoveHome, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectHome, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: true, true); });
        Reg(Cmd.NavMoveEnd, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectEnd, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: false, true); });

        // -- Navigation: document start/end --

        Reg(Cmd.NavMoveDocStart, doc => {
            FlushCompound();
            doc.Selection = Selection.Collapsed(0);
            ScrollCaretIntoView();
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavSelectDocStart, doc => {
            FlushCompound();
            doc.Selection = doc.Selection.ExtendTo(0);
            ScrollCaretIntoView();
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavMoveDocEnd, doc => {
            FlushCompound();
            doc.Selection = Selection.Collapsed(doc.Table.DocLength);
            ScrollCaretIntoView();
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavSelectDocEnd, doc => {
            FlushCompound();
            doc.Selection = doc.Selection.ExtendTo(doc.Table.DocLength);
            ScrollCaretIntoView();
            InvalidateVisual();
            ResetCaretBlink();
        });

        // -- Navigation: page up/down --

        Reg(Cmd.NavPageUp, doc => { FlushCompound(); MoveCaretByPage(doc, -1, false); }, isVerticalNav: true);
        Reg(Cmd.NavSelectPageUp, doc => { FlushCompound(); MoveCaretByPage(doc, -1, true); }, isVerticalNav: true);
        Reg(Cmd.NavPageDown, doc => { FlushCompound(); MoveCaretByPage(doc, +1, false); }, isVerticalNav: true);
        Reg(Cmd.NavSelectPageDown, doc => { FlushCompound(); MoveCaretByPage(doc, +1, true); }, isVerticalNav: true);

        // -- Editing: word delete, line ops, indent --

        Reg(Cmd.EditDeleteWordLeft, doc => {
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
        }, isColumnAware: true);

        Reg(Cmd.EditDeleteWordRight, doc => {
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
        }, isColumnAware: true);

        Reg(Cmd.EditInsertLineBelow, _ => PerformInsertLineBelow());
        Reg(Cmd.EditInsertLineAbove, _ => PerformInsertLineAbove());
        Reg(Cmd.EditDuplicateLine, _ => PerformDuplicateLine());
        Reg(Cmd.EditSmartIndent, _ => { FlushCompound(); PerformSmartIndent(); });
        Reg(Cmd.EditIndent, _ => { FlushCompound(); PerformSimpleIndent(); });
        Reg(Cmd.EditOutdent, _ => { FlushCompound(); PerformOutdent(); });

        // -- Indent conversion --

        Reg(Cmd.EditIndentToSpaces, doc => {
            FlushCompound();
            doc.ConvertIndentation(Core.Documents.IndentStyle.Spaces, _indentWidth);
            InvalidateLayout();
        });
        Reg(Cmd.EditIndentToTabs, doc => {
            FlushCompound();
            doc.ConvertIndentation(Core.Documents.IndentStyle.Tabs, _indentWidth);
            InvalidateLayout();
        });

        // -- Scroll without moving caret --

        Reg(Cmd.ViewScrollLineUp, _ => {
            FlushCompound();
            ScrollValue -= GetRowHeight();
            InvalidateVisual();
        }, isVerticalNav: true);
        Reg(Cmd.ViewScrollLineDown, _ => {
            FlushCompound();
            ScrollValue += GetRowHeight();
            InvalidateVisual();
        }, isVerticalNav: true);

        // -- Column selection commands (handled in preamble, register with empty action) --

        Reg(Cmd.NavColumnSelectUp, _ => { }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavColumnSelectDown, _ => { }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavColumnSelectLeft, _ => { }, isColumnAware: true);
        Reg(Cmd.NavColumnSelectRight, _ => { }, isColumnAware: true);

        // -- Column-mode intercepts --
        // These replace the normal behavior of existing commands when a column
        // selection is active. Return true = fully handled; false = exit column
        // mode and fall through to normal handling.
        ColIntercept(Cmd.NavColumnSelectUp, doc => { PerformColumnSelectVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavColumnSelectDown, doc => { PerformColumnSelectVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavColumnSelectLeft, doc => { PerformColumnSelectHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavColumnSelectRight, doc => { PerformColumnSelectHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveLeft, doc => { PerformColumnMoveHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveRight, doc => { PerformColumnMoveHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveUp, doc => { PerformColumnMoveVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveDown, doc => { PerformColumnMoveVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectLeft, doc => { PerformColumnSelectHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectRight, doc => { PerformColumnSelectHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectUp, doc => { PerformColumnSelectVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectDown, doc => { PerformColumnSelectVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveHome, doc => {
            if (doc.ColumnSel is { } sel) {
                doc.ColumnSel = sel.MoveColumnsTo(0);
                ScrollCaretIntoView(); InvalidateVisual(); ResetCaretBlink();
            }
            return true;
        });
        ColIntercept(Cmd.NavMoveEnd, doc => {
            if (doc.ColumnSel is { } sel) {
                doc.ColumnSel = sel.MoveColumnsTo(MaxEndColumn(doc, sel));
                ScrollCaretIntoView(); InvalidateVisual(); ResetCaretBlink();
            }
            return true;
        });
        ColIntercept(Cmd.NavMoveWordLeft, doc => { PerformColumnMoveWord(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveWordRight, doc => { PerformColumnMoveWord(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectWordLeft, doc => { PerformColumnSelectWord(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectWordRight, doc => { PerformColumnSelectWord(doc, +1); return true; });
        ColIntercept(Cmd.EditNewline, doc => {
            // Exit column mode, then fall through to normal newline handling.
            doc.ClearColumnSelection(_indentWidth);
            return false;
        });
        ColIntercept(Cmd.EditBackspace, doc => {
            FlushCompound();
            _editSw.Restart();
            doc.DeleteBackwardAtCursors(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
        ColIntercept(Cmd.EditDelete, doc => {
            FlushCompound();
            _editSw.Restart();
            doc.DeleteForwardAtCursors(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
        ColIntercept(Cmd.EditTab, doc => {
            Coalesce("col-tab");
            _editSw.Restart();
            doc.InsertAtCursors("\t", _indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
    }

    // -------------------------------------------------------------------------
    // Caret movement helpers
    // -------------------------------------------------------------------------

    private void MoveCaretHorizontal(Document doc, int delta, bool byWord, bool extend) {
        var caret = doc.Selection.Caret;
        var table = doc.Table;
        var len = table.DocLength;
        long newCaret;

        if (!byWord) {
            newCaret = Math.Clamp(caret + delta, 0L, len);
        } else {
            newCaret = delta < 0
                ? FindWordBoundaryLeft(doc, caret)
                : FindWordBoundaryRight(doc, caret);
        }

        // Skip over dead zones (line terminators).  When moving right past
        // the last content character of a line, jump to the start of the
        // next line.  When moving left into a dead zone, jump to the end
        // of the previous line's content.
        newCaret = SnapOutOfDeadZone(table, newCaret, delta > 0);

        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// If <paramref name="ofs"/> falls inside a line terminator dead zone,
    /// snaps it to the nearest valid content position.
    /// </summary>
    private static long SnapOutOfDeadZone(PieceTable table, long ofs, bool forward) {
        if (ofs <= 0 || ofs >= table.DocLength) return ofs;
        var line = (int)table.LineFromDocOfs(ofs);
        var docLineStart = table.DocLineStartOfs(line);
        var contentLen = table.LineContentLength(line);
        var contentEnd = docLineStart + contentLen;

        if (ofs <= contentEnd) {
            // Within content — valid position.
            return ofs;
        }

        // Past content end: we're in the terminator region.
        var termType = table.GetLineTerminator(line);
        if (termType == LineTerminatorType.Pseudo) {
            // Pseudo-line virtual terminator is a valid caret position.
            // The virtual terminator occupies exactly 1 doc-space position
            // (contentEnd itself), distinguishing end-of-line from start-of-next.
            return ofs; // allow resting on the virtual terminator
        }

        // Real terminator (LF, CR, CRLF) — snap out.
        if (forward) {
            return line + 1 < table.LineCount
                ? table.DocLineStartOfs(line + 1)
                : table.DocLength;
        } else {
            return contentEnd;
        }
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
        var caretScreenY = caretRect.Y + RenderOffsetY;
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
        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var line = (int)table.LineFromDocOfs(Math.Min(caret, table.DocLength));
        var docLineStart = table.DocLineStartOfs(line);
        if (docLineStart < 0) return;

        var newCaret = toStart
            ? docLineStart
            : docLineStart + table.LineContentLength(line);
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
        // Caret is doc-space; translate to buf-space for GetText.
        var bufCaret = doc.Table.DocOfsToBufOfs(caret);
        var bufWindowStart = Math.Max(0L, bufCaret - 1024);
        var windowLen = (int)(bufCaret - bufWindowStart);
        var text = doc.Table.GetText(bufWindowStart, windowLen);
        var pos = windowLen; // position within the window
        // Skip whitespace going left, then skip non-whitespace
        while (pos > 0 && char.IsWhiteSpace(text[pos - 1])) {
            pos--;
        }
        while (pos > 0 && !char.IsWhiteSpace(text[pos - 1])) {
            pos--;
        }
        return doc.Table.BufOfsToDocOfs(bufWindowStart + pos);
    }

    private static long FindWordBoundaryRight(Document doc, long caret) {
        var docLen = doc.Table.DocLength;
        if (caret >= docLen) {
            return docLen;
        }
        // Caret is doc-space; translate to buf-space for GetText.
        var bufCaret = doc.Table.DocOfsToBufOfs(caret);
        var bufLen = doc.Table.Length;
        var windowLen = (int)Math.Min(1024L, bufLen - bufCaret);
        var text = doc.Table.GetText(bufCaret, windowLen);
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
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineIdx = doc.Table.LineFromDocOfs(doc.Selection.Caret);
        if (lineIdx + 1 < doc.Table.LineCount) {
            var nextLineStart = doc.Table.DocLineStartOfs(lineIdx + 1);
            doc.Selection = Selection.Collapsed(nextLineStart);
            doc.Insert(nl);
            doc.Selection = Selection.Collapsed(nextLineStart);
        } else {
            // Last line — append newline at end
            doc.Selection = Selection.Collapsed(doc.Table.DocLength);
            doc.Insert(nl);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformInsertLineAbove() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineStart = doc.Table.DocLineStartOfs(doc.Table.LineFromDocOfs(doc.Selection.Caret));
        doc.Selection = Selection.Collapsed(lineStart);
        doc.Insert(nl);
        doc.Selection = Selection.Collapsed(lineStart);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformDuplicateLine() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineIdx = table.LineFromDocOfs(caret);
        var docLineStart = table.DocLineStartOfs(lineIdx);
        var caretCol = caret - docLineStart;

        // Buf-space for text access.
        var bufLineStart = table.LineStartOfs(lineIdx);
        long bufLineEnd = lineIdx + 1 < table.LineCount
            ? table.LineStartOfs(lineIdx + 1)
            : table.Length;
        var bufLineLen = (int)(bufLineEnd - bufLineStart);
        string lineText;
        if (bufLineLen <= table.MaxGetTextLength) {
            lineText = table.GetText(bufLineStart, bufLineLen);
        } else {
            var sb = new StringBuilder(bufLineLen);
            table.ForEachPiece(bufLineStart, bufLineLen, span => sb.Append(span));
            lineText = sb.ToString();
        }

        // Doc-space for Selection.
        long docLineEnd = lineIdx + 1 < table.LineCount
            ? table.DocLineStartOfs(lineIdx + 1)
            : table.DocLength;

        // If the line doesn't end with a newline (last line), prepend one.
        if (bufLineEnd == table.Length && (lineText.Length == 0 || lineText[^1] != '\n')) {
            doc.BeginCompound();
            doc.Selection = Selection.Collapsed(table.DocLength);
            doc.Insert(nl + lineText);
            doc.EndCompound();
        } else {
            doc.Selection = Selection.Collapsed(docLineEnd);
            doc.Insert(lineText);
        }
        // Place caret on the duplicated line at the same column offset.
        var nlLen = nl.Length;
        var newDocLineStart = docLineEnd + (lineText.Length > 0 && lineText[^1] != '\n' ? nlLen : 0);
        doc.Selection = Selection.Collapsed(Math.Min(newDocLineStart + caretCol, table.DocLength));
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Measures the indentation depth of a line in canonical units.
    /// Tabs count as <paramref name="tabSize"/> spaces each.
    /// </summary>
    private static int MeasureIndent(string lineText, int tabSize) {
        var depth = 0;
        foreach (var ch in lineText) {
            if (ch == ' ') depth++;
            else if (ch == '\t') depth += tabSize;
            else break;
        }
        return depth;
    }

    /// <summary>
    /// Builds the indentation string for a given depth using the document's
    /// dominant indent style.
    /// </summary>
    private static string BuildIndent(int depth, Core.Documents.IndentStyle style, int tabSize) {
        if (depth <= 0) return string.Empty;
        if (style == Core.Documents.IndentStyle.Tabs) {
            var tabs = depth / tabSize;
            var spaces = depth % tabSize;
            return new string('\t', tabs) + (spaces > 0 ? new string(' ', spaces) : "");
        }
        return new string(' ', depth);
    }

    /// <summary>
    /// Returns the number of leading whitespace characters in the line text.
    /// </summary>
    private static int LeadingWhitespaceLength(string lineText) {
        var i = 0;
        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t')) i++;
        return i;
    }

    /// <summary>
    /// Finds the indentation depth (in spaces) of the nearest non-empty line
    /// above <paramref name="lineIdx"/>.
    /// </summary>
    private static int FindPrevIndent(Core.Documents.PieceTable table, long lineIdx, int tabSize) {
        for (var i = lineIdx - 1; i >= 0; i--) {
            var text = table.GetLine(i);
            if (!string.IsNullOrWhiteSpace(text))
                return MeasureIndent(text, tabSize);
        }
        return 0;
    }

    /// <summary>
    /// Replaces the leading whitespace of a single line to achieve
    /// <paramref name="targetDepth"/>. No-op if already at that depth.
    /// </summary>
    private static void SetLineIndent(
        Core.Documents.Document doc, Core.Documents.PieceTable table,
        long lineIdx, string lineText, int targetDepth,
        Core.Documents.IndentStyle style, int tabSize) {
        var currentDepth = MeasureIndent(lineText, tabSize);
        if (targetDepth == currentDepth) return;
        var newIndent = BuildIndent(targetDepth, style, tabSize);
        var wsLen = LeadingWhitespaceLength(lineText);
        var lineStart = table.DocLineStartOfs(lineIdx);
        if (wsLen > 0 && newIndent.Length == 0) {
            doc.Selection = new Selection(lineStart, lineStart + wsLen);
            doc.DeleteSelection();
        } else if (wsLen > 0) {
            doc.Selection = new Selection(lineStart, lineStart + wsLen);
            doc.Insert(newIndent);
        } else {
            doc.Selection = Selection.Collapsed(lineStart);
            doc.Insert(newIndent);
        }
    }

    private void PerformSmartIndent() {
        var doc = Document;
        if (doc == null) return;
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromDocOfs(sel.Start);
        var endLine = table.LineFromDocOfs(Math.Max(sel.Start, sel.End - 1));

        if (sel.IsEmpty || startLine == endLine) {
            // Single line: stateless smart indent.
            // Candidates: {prevDepth - tabSize, prevDepth, prevDepth + tabSize},
            // clamped to >= 0, deduplicated, sorted ascending.
            // Current depth picks the next candidate up; wraps to smallest.
            var lineText = table.GetLine(startLine);
            var currentDepth = MeasureIndent(lineText, tabSize);
            var prevDepth = FindPrevIndent(table, startLine, tabSize);

            var candidates = new SortedSet<int> {
                Math.Max(0, prevDepth - tabSize),
                prevDepth,
                prevDepth + tabSize,
            };
            var sorted = candidates.ToList();

            // Pick the next candidate strictly above currentDepth; wrap to first.
            var targetDepth = sorted.FirstOrDefault(d => d > currentDepth, sorted[0]);

            if (targetDepth != currentDepth) {
                var newIndent = BuildIndent(targetDepth, style, tabSize);
                var wsLen = LeadingWhitespaceLength(lineText);
                var lineStart = table.DocLineStartOfs(startLine);
                if (wsLen > 0 && newIndent.Length == 0) {
                    // Removing all indentation: just delete the whitespace.
                    doc.Selection = new Selection(lineStart, lineStart + wsLen);
                    doc.DeleteSelection();
                } else if (wsLen > 0) {
                    // Replacing existing whitespace with different whitespace.
                    doc.Selection = new Selection(lineStart, lineStart + wsLen);
                    doc.Insert(newIndent);
                } else {
                    // Adding indentation to an unindented line.
                    doc.Selection = Selection.Collapsed(lineStart);
                    doc.Insert(newIndent);
                }
            }
        } else {
            // Multi-line: set each line's indent to one level more than the
            // line above the selection. This is the smart indent interpretation
            // for documents without block structure awareness.
            var refDepth = FindPrevIndent(table, startLine, tabSize);
            var targetDepth = refDepth + tabSize;
            doc.BeginCompound();
            for (var line = startLine; line <= endLine; line++) {
                var lineText = table.GetLine(line);
                SetLineIndent(doc, table, line, lineText, targetDepth, style, tabSize);
            }
            doc.EndCompound();
            var rangeStart = table.DocLineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.DocLineStartOfs(endLine + 1)
                : table.DocLength;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Adds one indent level to the current line or all selected lines.
    /// </summary>
    private void PerformSimpleIndent() {
        var doc = Document;
        if (doc == null) return;
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromDocOfs(sel.Start);
        var endLine = table.LineFromDocOfs(Math.Max(sel.Start, sel.End - 1));

        doc.BeginCompound();
        for (var line = startLine; line <= endLine; line++) {
            var lineText = table.GetLine(line);
            var currentDepth = MeasureIndent(lineText, tabSize);
            var targetDepth = currentDepth + tabSize;
            SetLineIndent(doc, table, line, lineText, targetDepth, style, tabSize);
        }
        doc.EndCompound();

        if (!sel.IsEmpty && startLine != endLine) {
            var rangeStart = table.DocLineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.DocLineStartOfs(endLine + 1)
                : table.DocLength;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }

        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Removes one indent level from the current line or all selected lines.
    /// </summary>
    private void PerformOutdent() {
        var doc = Document;
        if (doc == null) return;
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromDocOfs(sel.Start);
        var endLine = table.LineFromDocOfs(Math.Max(sel.Start, sel.End - 1));

        doc.BeginCompound();
        for (var line = startLine; line <= endLine; line++) {
            var lineText = table.GetLine(line);
            var currentDepth = MeasureIndent(lineText, tabSize);
            if (currentDepth <= 0) continue;
            var targetDepth = Math.Max(0, currentDepth - tabSize);
            var newIndent = BuildIndent(targetDepth, style, tabSize);
            var wsLen = LeadingWhitespaceLength(lineText);
            var lineStart = table.DocLineStartOfs(line);
            if (newIndent.Length == 0) {
                doc.Selection = new Selection(lineStart, lineStart + wsLen);
                doc.DeleteSelection();
            } else {
                doc.Selection = new Selection(lineStart, lineStart + wsLen);
                doc.Insert(newIndent);
            }
        }
        doc.EndCompound();

        if (startLine == endLine) {
            // Single line: place caret at end of new indentation.
            var newText = table.GetLine(startLine);
            var newWs = LeadingWhitespaceLength(newText);
            doc.Selection = Selection.Collapsed(table.DocLineStartOfs(startLine) + newWs);
        } else {
            // Re-select the full line range.
            var rangeStart = table.DocLineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.DocLineStartOfs(endLine + 1)
                : table.DocLength;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
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
        return Math.Max(0, (int)Math.Round((caretRect.Y + RenderOffsetY) / rh));
    }

    /// <summary>
    /// Hit-tests the layout at a given screen row, returning the absolute
    /// document offset closest to (<see cref="_preferredCaretX"/>, screenRow).
    /// </summary>
    private long HitTestAtScreenRow(int screenRow, double rh, LayoutResult layout) {
        var targetY = screenRow * rh + rh / 2 - RenderOffsetY;
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
        offset = Math.Clamp(offset, 0, doc.Table.DocLength);
        doc.Selection = Core.Documents.Selection.Collapsed(offset);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
        Focus();
    }

    private void ScrollCaretIntoView() {
        var doc = Document;
        if (doc == null) return;
        if (IsLoading) {
            return; // Can't actually happen
        }

        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        PerfStats.ScrollCaretCalls++;

        var rh = GetRowHeight();
        var vpH = Bounds.Height > 0 ? Bounds.Height : _viewport.Height;
        var caretLine = table.LineFromDocOfs(caret);

        if (!_wrapLines) {
            // ── Wrapping off ──────────────────────────────────────────
            // The line tree is exact: each entry = one visual row.
            // Update extents so ScrollMaximum/HScrollMaximum aren't stale.
            var cw = GetCharWidth();
            var maxLine = table.MaxLineLength;
            var hExtent = maxLine > 0 ? maxLine * cw + _gutterWidth : _extent.Width;
            _extent = new Size(hExtent, lineCount * rh);

            var caretY = caretLine * rh;
            if (_scrollOffset.Y > ScrollMaximum) {
                ScrollValue = ScrollMaximum;
            }
            if (caretY < _scrollOffset.Y) {
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + vpH) {
                ScrollValue = caretY + rh - vpH;
            }
        } else {
            // ── Wrapping on ───────────────────────────────────────────
            // Lines can span multiple visual rows. Use char-based
            // estimation to position the scroll, then verify with an
            // actual layout pass since the estimate can be off.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);
            var caretY = EstimateWrappedLineY(caretLine, table, charsPerRow, rh);

            // Update extent from estimated total visual rows so that
            // ScrollMaximum isn't stale (e.g. after a large paste).
            var totalChars = table.Length;
            var totalVisualRows = charsPerRow > 0
                ? Math.Max(lineCount, (long)Math.Ceiling((double)totalChars / charsPerRow))
                : lineCount;
            _extent = new Size(_extent.Width, totalVisualRows * rh);

            if (_scrollOffset.Y > ScrollMaximum) {
                ScrollValue = ScrollMaximum;
            }
            if (caretY < _scrollOffset.Y) {
                ScrollValue = caretY;
            } else if (caretY + rh > _scrollOffset.Y + vpH) {
                ScrollValue = caretY + rh - vpH;
            }

            // Verify with actual layout. Repeat up to 3 times because
            // the first correction can be off when RenderOffsetY shifts.
            for (var pass = 0; pass < 3; pass++) {
                InvalidateLayout();
                var layout = EnsureLayout();
                var caretLocalOfs = (int)(caret - layout.ViewportBase);
                var layoutCharEnd = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;
                if (caretLocalOfs < 0 || caretLocalOfs > layoutCharEnd) break;

                var caretRect = _layoutEngine.GetCaretBounds(caretLocalOfs, layout);
                var caretScreenY = caretRect.Y + RenderOffsetY;
                var caretH = Math.Ceiling(Math.Max(caretRect.Height, rh));
                if (caretScreenY < 0) {
                    PerfStats.ScrollRetries++;
                    ScrollValue = _scrollOffset.Y + caretScreenY;
                } else if (caretScreenY + caretH > vpH) {
                    PerfStats.ScrollRetries++;
                    ScrollValue = _scrollOffset.Y + caretScreenY + caretH - vpH;
                } else {
                    break; // caret is visible — done
                }
            }
        }

        // Horizontal scroll (wrapping off only).
        // Horizontal extent was already updated above.
        if (!_wrapLines) {
            var lineStart = table.DocLineStartOfs(caretLine);
            var caretCol = (int)(caret - lineStart);
            var cw = GetCharWidth();
            var caretX = caretCol * cw;
            var textAreaW = _viewport.Width - _gutterWidth;

            if (caretX < _scrollOffset.X) {
                HScrollValue = caretX;
            } else if (caretX + cw > _scrollOffset.X + textAreaW) {
                HScrollValue = caretX + cw - textAreaW;
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
        var topLine = table.LineFromDocOfs(layout.ViewportBase);
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
    /// at the top of the viewport.
    /// </summary>
    private void ScrollToTopLine(long targetLine, PieceTable table) {
        var lineCount = table.LineCount;
        if (lineCount <= 0) return;

        var rh = GetRowHeight();
        if (!_wrapLines) {
            // Wrapping off: exact.
            ScrollValue = targetLine * rh;
        } else {
            // Wrapping on: estimated. Nudge by a sub-pixel amount so the
            // round-trip in LayoutWindowed always resolves to targetLine.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var charsPerRow = GetCharsPerRow(textW);
            ScrollValue = EstimateWrappedLineY(targetLine, table, charsPerRow, rh) + 0.01;
        }
    }

    /// <summary>
    /// Returns the logical line index of the last fully visible line in the
    /// current layout.  A visual row is "fully visible" when its entire height
    /// fits within the viewport after applying <see cref="RenderOffsetY"/>.
    /// </summary>
    private long FindLastVisibleLogicalLine(LayoutResult layout, PieceTable table) {
        var lines = layout.Lines;
        if (lines.Count == 0) {
            return table.LineFromDocOfs(layout.ViewportBase);
        }

        var rh = layout.RowHeight;
        for (var i = lines.Count - 1; i >= 0; i--) {
            var screenBottom = (lines[i].Row + lines[i].HeightInRows) * rh + RenderOffsetY;
            if (screenBottom <= _viewport.Height + 0.5) {
                return table.LineFromDocOfs(layout.ViewportBase + lines[i].CharStart);
            }
        }

        return table.LineFromDocOfs(layout.ViewportBase);
    }

    // -------------------------------------------------------------------------
    // Mouse / pointer input
    // -------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (IsLoading) return;
        var props = e.GetCurrentPoint(this).Properties;


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
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
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
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (alt && !_wrapLines && !doc.Table.HasPseudoLines) {
                // Alt+click: start column (block) selection.
                var table = doc.Table;
                var line = (int)table.LineFromDocOfs(ofs);
                var col = ColumnSelection.OfsToCol(table, ofs, _indentWidth);
                doc.ColumnSel = new ColumnSelection(line, col, line, col);
                doc.Selection = Selection.Collapsed(ofs);
                _columnDrag = true;
                _pointerDown = true;
                e.Pointer.Capture(this);
                // Force carets steady-visible (no blinking) during column
                // drag so the user can see the anchor point, especially
                // when dragging vertically with no horizontal extent.
                _caretVisible = true;
                _caretTimer.Stop();
            } else {
                // Exit column mode on normal click.
                if (doc.ColumnSel != null) {
                    doc.ColumnSel = null;
                }
                doc.Selection = shift
                    ? doc.Selection.ExtendTo(ofs)
                    : Selection.Collapsed(ofs);
                _pointerDown = true;
                e.Pointer.Capture(this);
            }
        } else {
            if (doc.ColumnSel != null) {
                doc.ColumnSel = null;
            }
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
        var layoutPt = new Point(pt.X - _gutterWidth + _scrollOffset.X, pt.Y - RenderOffsetY);
        var localOfs = _layoutEngine.HitTest(layoutPt, layout);
        var ofs = layout.ViewportBase + localOfs;
        if (_columnDrag && doc.ColumnSel is { } colSel) {
            var table = doc.Table;
            var line = (int)table.LineFromDocOfs(ofs);
            var col = ColumnSelection.OfsToCol(table, ofs, _indentWidth);
            doc.ColumnSel = colSel.ExtendTo(line, col);
        } else {
            doc.Selection = doc.Selection.ExtendTo(ofs);
        }
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
        _columnDrag = false;
        _pointerDown = false;
        e.Pointer.Capture(null);
        ResetCaretBlink();
    }

    protected override void OnGotFocus(GotFocusEventArgs e) {
        base.OnGotFocus(e);
        // Show caret even during loading — user can navigate and position caret.
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
    /// <see cref="RenderOffsetY"/> converts between layout and screen
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
        var targetY = caretRow * rh + rh / 2 - RenderOffsetY;
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
    /// Replaces the document and restores scroll state in a single
    /// operation without any intermediate frame where the layout is
    /// null. Used by auto-reload so the viewport stays visually stable.
    /// </summary>
    public void ReplaceDocument(Document newDoc, TabState scrollState) {
        if (Document is Document old) {
            old.Changed -= OnDocumentChanged;
        }
        // Set the property value. OnPropertyChanged will fire but
        // _keepScrollOnSwap prevents scroll reset and layout disposal.
        _keepScrollOnSwap = true;
        Document = newDoc;

        // Apply the target scroll position. Don't dispose the old
        // layout — MeasureOverride will rebuild it atomically so there
        // is never a frame with _layout == null.
        _scrollOffset = new Vector(0, scrollState.ScrollOffsetY);
        _winTopLine = scrollState.WinTopLine;
        _winScrollOffset = scrollState.WinScrollOffset;
        _winRenderOffsetY = scrollState.WinRenderOffsetY;
        _winFirstLineHeight = scrollState.WinFirstLineHeight;

        // Force a full measure → layout → render cycle. MeasureOverride
        // rebuilds the layout with the new document's content and fires
        // ScrollChanged (which syncs the scrollbar extent/position).
        // InvalidateVisual ensures the render pass runs even if the
        // available size hasn't changed.
        InvalidateMeasure();
        InvalidateVisual();
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

    // -------------------------------------------------------------------------
    // Search — shared state
    // -------------------------------------------------------------------------

    /// <summary>
    /// The most recent successful search term. Shared across Find Bar,
    /// incremental search, and Find Word/Selection. Used by Find Next / Find Previous.
    /// </summary>
    public string LastSearchTerm {
        get => _lastSearchTerm;
        set => _lastSearchTerm = value ?? "";
    }

    /// <summary>
    /// Selects the next occurrence of <see cref="LastSearchTerm"/> after the
    /// current selection (or caret), wrapping around if needed.
    /// Returns true if a match was found.
    /// </summary>
    public bool FindNext(bool matchCase = false, bool wholeWord = false,
                         SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        var table = doc.Table;
        var sel = doc.Selection;
        // Start searching one character past the start of the current selection
        // so we advance past the current match.
        var searchFrom = sel.IsEmpty ? sel.Caret : sel.Start + 1;
        if (searchFrom >= table.Length) {
            searchFrom = 0;
        }
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var found = FindInDocument(table, opts, searchFrom);
        if (found < 0) {
            return false;
        }
        doc.Selection = new Selection(found, found + opts.MatchLength(table, found));
        ScrollCaretIntoView();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Selects the previous occurrence of <see cref="LastSearchTerm"/> before
    /// the current selection (or caret), wrapping around if needed.
    /// Returns true if a match was found.
    /// </summary>
    public bool FindPrevious(bool matchCase = false, bool wholeWord = false,
                             SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        var table = doc.Table;
        var sel = doc.Selection;
        var searchBefore = sel.IsEmpty ? sel.Caret : sel.Start;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var found = FindInDocumentBackward(table, opts, searchBefore);
        if (found < 0) {
            return false;
        }
        doc.Selection = new Selection(found, found + opts.MatchLength(table, found));
        ScrollCaretIntoView();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Uses the current selection (or selects the word at the caret if collapsed)
    /// as the search term, then finds the next occurrence.
    /// </summary>
    /// <summary>Maximum length for a search term derived from the selection.</summary>
    private const int MaxSearchTermLength = 1024;

    /// <summary>
    /// Returns the selected text if it is a single line and within
    /// <see cref="MaxSearchTermLength"/>, or null otherwise.
    /// Does not modify the selection.
    /// </summary>
    public string? GetSelectionAsSearchTerm() {
        var doc = Document;
        if (doc == null || doc.Selection.IsEmpty) return null;
        var sel = doc.Selection;
        if (sel.Len > MaxSearchTermLength) return null;
        var table = doc.Table;
        var startLine = table.LineFromDocOfs(sel.Start);
        var endLine = table.LineFromDocOfs(sel.End - 1);
        if (startLine != endLine) return null;
        return doc.GetSelectedText();
    }

    /// <summary>
    /// Returns the selected text as a search term, or null if the selection
    /// is empty or spans more than one line.  When collapsed, selects the
    /// word at the caret first.
    /// </summary>
    private string? GetSingleLineSelectionTerm() {
        var doc = Document;
        if (doc == null) return null;
        if (doc.Selection.IsEmpty) {
            doc.SelectWord();
            if (doc.Selection.IsEmpty) return null;
        }
        return GetSelectionAsSearchTerm();
    }

    public bool FindNextSelection() {
        var term = GetSingleLineSelectionTerm();
        if (term == null) return false;
        _lastSearchTerm = term;
        return FindNext();
    }

    /// <summary>
    /// Uses the current selection (or selects the word at the caret if collapsed)
    /// as the search term, then finds the previous occurrence.
    /// </summary>
    public bool FindPreviousSelection() {
        var term = GetSingleLineSelectionTerm();
        if (term == null) return false;
        _lastSearchTerm = term;
        return FindPrevious();
    }

    // -------------------------------------------------------------------------
    // Replace
    // -------------------------------------------------------------------------

    /// <summary>
    /// Replaces the current selection (if it matches the search term) with
    /// <paramref name="replacement"/>, then advances to the next match.
    /// Returns true if a replacement was made.
    /// </summary>
    public bool ReplaceCurrent(string replacement, bool matchCase = false,
                               bool wholeWord = false, SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        // Only replace if the current selection matches the search term.
        if (doc.Selection.IsEmpty) {
            // No selection — try to find the next match first.
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        // The selection should match the search term, so its length is bounded.
        if (doc.Selection.Len > MaxSearchTermLength) {
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        var selectedText = doc.GetSelectedText()!; // bounded by MaxSearchTermLength above
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        if (!IsSelectionMatch(selectedText, opts)) {
            // Selection doesn't match — find next match instead.
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        FlushCompound();
        doc.Insert(replacement);
        ScrollCaretIntoView();
        InvalidateVisual();
        // Advance to next match.
        FindNext(matchCase, wholeWord, mode);
        return true;
    }

    /// <summary>
    /// Replaces all occurrences of the current search term with
    /// <paramref name="replacement"/>.  Returns the number of replacements made.
    /// </summary>
    /// <summary>
    /// Collects match positions on a background thread, then applies a single
    /// bulk PieceTable replace on the UI thread.  Reports progress via
    /// <paramref name="progress"/> (0–100) and supports cancellation.
    /// Returns the number of replacements made, or 0 if cancelled.
    /// </summary>
    public async Task<int> ReplaceAllAsync(string replacement, bool matchCase = false,
                          bool wholeWord = false, SearchMode mode = SearchMode.Normal,
                          IProgress<(string Message, double Percent)>? progress = null,
                          CancellationToken ct = default) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return 0;
        }
        var table = doc.Table;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var isRegex = opts.CompiledRegex != null;
        var docLen = table.Length;
        var maxOverlap = MaxRegexMatchLength;

        // Phase 1: collect all match positions on a background thread.
        // Progress updates throttled to ~100ms to avoid UI marshalling overhead.
        var (positions, matchLengths) = await Task.Run(() => {
            var pos = new List<long>();
            var lens = isRegex ? new List<int>() : null;
            long searchFrom = 0;
            var lastProgressTicks = Environment.TickCount64;
            while (searchFrom <= docLen) {
                ct.ThrowIfCancellationRequested();
                var found = SearchChunked(table, opts, searchFrom, maxOverlap);
                if (found < 0) break;
                var matchLen = isRegex
                    ? RegexMatchLengthAt(table, opts, found)
                    : opts.Needle.Length;
                pos.Add(found);
                lens?.Add(matchLen);
                searchFrom = found + Math.Max(matchLen, 1);

                var now = Environment.TickCount64;
                if (docLen > 0 && now - lastProgressTicks >= 100) {
                    lastProgressTicks = now;
                    var pct = (double)searchFrom / docLen * 99.0;
                    progress?.Report(($"Searching\u2026 {pos.Count:N0} matches found", pct));
                }
            }
            return (pos, lens);
        }, ct);

        if (positions.Count == 0) return 0;

        progress?.Report(($"Replacing {positions.Count:N0} matches\u2026", 99));

        // Phase 2: single bulk PieceTable operation on the UI thread.
        FlushCompound();
        int count;
        if (matchLengths == null) {
            count = doc.BulkReplaceUniform(
                positions.ToArray(), opts.Needle.Length, replacement);
        } else {
            var matches = new (long Pos, int Len)[positions.Count];
            for (var i = 0; i < positions.Count; i++) {
                matches[i] = (positions[i], matchLengths[i]);
            }
            var replacements = new string[matches.Length];
            Array.Fill(replacements, replacement);
            count = doc.BulkReplaceVarying(matches, replacements);
        }

        progress?.Report(("Done", 100));

        ScrollCaretIntoView();
        InvalidateVisual();
        return count;
    }

    /// <summary>
    /// Copies document text into a caller-owned array without allocating
    /// a string.  Uses <see cref="PieceTable.ForEachPiece"/>.
    /// </summary>
    private static void CopyFromTable(PieceTable table, long start, char[] buf, int len) {
        var pos = 0;
        table.ForEachPiece(start, len, span => {
            span.CopyTo(buf.AsSpan(pos));
            pos += span.Length;
        });
    }

    /// <summary>
    /// Searches forward from <paramref name="start"/> in bounded chunks
    /// so we never materialize the entire remaining document.
    /// </summary>
    private static long SearchChunked(PieceTable table, SearchOptions opts, long start,
        int maxRegexMatchLen = 1024, long endLimit = -1) {
        const int chunkSize = 64 * 1024;
        var docLen = endLimit >= 0 ? Math.Min(endLimit, table.Length) : table.Length;
        // Overlap by needle length (or maxRegexMatchLen for regex) so
        // matches spanning chunk boundaries are found.
        var overlap = opts.CompiledRegex != null
            ? Math.Min(maxRegexMatchLen, (int)(docLen - start))
            : opts.Needle.Length;

        var bufLen = chunkSize + overlap;
        var buf = ArrayPool<char>.Shared.Rent(bufLen);
        try {
            while (start < docLen) {
                var readLen = (int)Math.Min(bufLen, docLen - start);
                CopyFromTable(table, start, buf, readLen);
                var dest = buf.AsSpan(0, readLen);
                int idx;
                if (opts.CompiledRegex != null) {
                    var m = opts.CompiledRegex.Match(new string(dest));
                    idx = m.Success ? m.Index : -1;
                } else {
                    idx = dest.IndexOf(opts.Needle.AsSpan(), opts.Comparison);
                }
                if (idx >= 0 && start + idx < docLen) return start + idx;
                start += chunkSize;
            }
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
        return -1;
    }

    /// <summary>
    /// Counts all matches and determines the 1-based index of the match at the
    /// current selection. Returns (0, 0) if there's no search term or no document.
    /// </summary>
    /// <summary>Maximum matches to count before returning a capped result.</summary>
    private const int MaxMatchCount = 9999;

    /// <summary>
    /// Counts matches on a background thread.  The caller should cancel
    /// <paramref name="ct"/> when the search term or document changes.
    /// </summary>
    public Task<(int Current, int Total, bool Capped)> GetMatchInfoAsync(
            bool matchCase, bool wholeWord, SearchMode mode,
            CancellationToken ct = default) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0)
            return Task.FromResult((0, 0, false));
        // Capture UI-thread state before offloading.
        var table = doc.Table;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var selStart = doc.Selection.Start;
        var maxOverlap = MaxRegexMatchLength;
        return Task.Run(() => CountMatches(table, opts, selStart, maxOverlap, ct), ct);
    }

    private static (int Current, int Total, bool Capped) CountMatches(
            PieceTable table, SearchOptions opts, long selStart,
            int maxOverlap, CancellationToken ct) {
        int total = 0, current = 0;
        long pos = 0;
        while (pos <= table.Length) {
            ct.ThrowIfCancellationRequested();
            var found = SearchChunked(table, opts, pos, maxOverlap);
            if (found < 0) break;
            total++;
            var matchLen = opts.CompiledRegex != null
                ? RegexMatchLengthAt(table, opts, found)
                : opts.Needle.Length;
            if (found == selStart) current = total;
            if (total >= MaxMatchCount) return (current, total, true);
            pos = found + Math.Max(matchLen, 1);
        }
        return (current, total, false);
    }

    /// <summary>
    /// Returns the regex match length at an exact document position.
    /// Reads only enough text to determine the match.
    /// </summary>
    private static int RegexMatchLengthAt(PieceTable table, SearchOptions opts, long pos) {
        var remaining = (int)Math.Min(table.Length - pos, 1024);
        var text = table.GetText(pos, remaining);
        var m = opts.CompiledRegex!.Match(text);
        return m.Success && m.Index == 0 ? m.Length : 0;
    }

    /// <summary>
    /// Checks whether <paramref name="text"/> matches the search pattern.
    /// For plain/wildcard/regex modes the check varies.
    /// </summary>
    private static bool IsSelectionMatch(string text, SearchOptions opts) {
        if (opts.CompiledRegex != null) {
            var m = opts.CompiledRegex.Match(text);
            return m.Success && m.Index == 0 && m.Length == text.Length;
        }
        return string.Equals(text, opts.Needle, opts.Comparison);
    }

    // -------------------------------------------------------------------------
    // Incremental search
    // -------------------------------------------------------------------------

    public bool InIncrementalSearch => _inIncrementalSearch;
    public string IncrementalSearchText => _isearchString;
    public bool IncrementalSearchFailed => _isearchFailed;

    /// <summary>Raised when incremental search state changes (for status bar updates).</summary>
    public event EventHandler? IncrementalSearchChanged;

    public void StartIncrementalSearch() {
        _inIncrementalSearch = true;
        _isearchString = "";
        _isearchFailed = false;
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExitIncrementalSearch() {
        if (!_inIncrementalSearch) {
            return;
        }
        _inIncrementalSearch = false;
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HandleIncrementalSearchChar(string text) {
        if (_isearchFailed || string.IsNullOrEmpty(text)) {
            return;
        }

        _isearchString += text;
        var doc = Document;
        if (doc == null) {
            return;
        }

        var table = doc.Table;
        var sel = doc.Selection;

        // Try 1: extend current selection in-place if the next char(s) match.
        if (!sel.IsEmpty) {
            var endOfSel = sel.End;
            if (endOfSel + text.Length <= table.Length) {
                var nextChars = table.GetText(endOfSel, text.Length);
                if (nextChars.Equals(text, StringComparison.OrdinalIgnoreCase)) {
                    doc.Selection = new Selection(sel.Start, endOfSel + text.Length);
                    _lastSearchTerm = _isearchString;
                    ScrollCaretIntoView();
                    InvalidateVisual();
                    IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }

        // Try 2: search the document for the full accumulated string.
        var searchFrom = sel.IsEmpty ? sel.Caret : sel.Start;
        var found = FindInDocument(table, _isearchString, searchFrom);
        if (found >= 0) {
            doc.Selection = new Selection(found, found + _isearchString.Length);
            _lastSearchTerm = _isearchString;
            ScrollCaretIntoView();
            InvalidateVisual();
        } else {
            _isearchFailed = true;
        }
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------
    // Search options
    // -----------------------------------------------------------------

    /// <summary>
    /// Encapsulates search parameters so helpers don't need many arguments.
    /// </summary>
    private readonly struct SearchOptions {
        public readonly string Needle;
        public readonly bool MatchCase;
        public readonly bool WholeWord;
        public readonly SearchMode Mode;
        public readonly Regex? CompiledRegex;

        public SearchOptions(string needle, bool matchCase, bool wholeWord, SearchMode mode) {
            Needle = needle;
            MatchCase = matchCase;
            WholeWord = wholeWord;
            Mode = mode;
            CompiledRegex = mode switch {
                SearchMode.Regex => BuildRegex(needle, matchCase, wholeWord),
                SearchMode.Wildcard => BuildRegex(WildcardToRegex(needle), matchCase, wholeWord),
                _ => wholeWord ? BuildRegex(Regex.Escape(needle), matchCase, wholeWord) : null,
            };
        }

        public StringComparison Comparison =>
            MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>Returns the length of the match at <paramref name="pos"/>.</summary>
        public int MatchLength(PieceTable table, long pos) {
            if (CompiledRegex == null) {
                return Needle.Length;
            }
            // For regex matches we need to re-match at the position to get length.
            var remaining = (int)Math.Min(table.Length - pos, Needle.Length * 4 + 256);
            var text = table.GetText(pos, remaining);
            var m = CompiledRegex.Match(text);
            return m.Success && m.Index == 0 ? m.Length : Needle.Length;
        }

        private static Regex? BuildRegex(string pattern, bool matchCase, bool wholeWord) {
            if (wholeWord) {
                pattern = @"\b" + pattern + @"\b";
            }
            var opts = RegexOptions.CultureInvariant;
            if (!matchCase) {
                opts |= RegexOptions.IgnoreCase;
            }
            try {
                return new Regex(pattern, opts);
            } catch {
                return null; // invalid regex — fall back to no matches
            }
        }

        private static string WildcardToRegex(string wildcard) {
            // * → .*, ? → ., everything else escaped
            var sb = new System.Text.StringBuilder(wildcard.Length * 2);
            foreach (var ch in wildcard) {
                sb.Append(ch switch {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString()),
                });
            }
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------
    // Forward search
    // -----------------------------------------------------------------

    private static long FindInDocument(PieceTable table, string needle, long fromOfs) =>
        FindInDocument(table, new SearchOptions(needle, false, false, SearchMode.Normal), fromOfs);

    private static long FindInDocument(PieceTable table, SearchOptions opts, long fromOfs,
                                       int maxOverlap = 1024) {
        var docLen = table.Length;
        if (opts.Needle.Length == 0 || docLen == 0) {
            return -1;
        }

        // Search forward from caret to end (chunked).
        var hit = SearchChunked(table, opts, fromOfs, maxOverlap);
        if (hit >= 0) {
            return hit;
        }

        // Wrap around: search from start up to fromOfs + needle overlap.
        if (fromOfs > 0) {
            var wrapEnd = Math.Min(fromOfs + opts.Needle.Length - 1, docLen);
            hit = SearchChunked(table, opts, 0, maxOverlap, wrapEnd);
            if (hit >= 0) {
                return hit;
            }
        }

        return -1;
    }

    // -----------------------------------------------------------------
    // Backward search
    // -----------------------------------------------------------------

    private static long FindInDocumentBackward(PieceTable table, SearchOptions opts,
                                               long beforeOfs, int maxOverlap = 1024) {
        var docLen = table.Length;
        if (opts.Needle.Length == 0 || docLen == 0) {
            return -1;
        }

        var hit = SearchChunkedBackward(table, opts, 0, beforeOfs, maxOverlap);
        if (hit >= 0) {
            return hit;
        }

        // Wrap around: search backward from end to beforeOfs.
        if (beforeOfs < docLen) {
            hit = SearchChunkedBackward(table, opts, beforeOfs, docLen, maxOverlap);
        }
        return hit;
    }

    /// <summary>
    /// Searches backward from <paramref name="end"/> to <paramref name="start"/>
    /// in bounded chunks, returning the last match position in the range.
    /// </summary>
    private static long SearchChunkedBackward(PieceTable table, SearchOptions opts,
            long start, long end, int maxOverlap) {
        const int chunkSize = 64 * 1024;
        var overlap = opts.CompiledRegex != null
            ? Math.Min(maxOverlap, (int)(end - start))
            : opts.Needle.Length;

        var bufLen = chunkSize + overlap;
        var buf = ArrayPool<char>.Shared.Rent(bufLen);
        try {
            // Walk backward from end in chunks.
            var chunkEnd = end;
            while (chunkEnd > start) {
                var chunkStart = Math.Max(start, chunkEnd - chunkSize - overlap);
                var readLen = (int)(chunkEnd - chunkStart);
                CopyFromTable(table, chunkStart, buf, readLen);
                var dest = buf.AsSpan(0, readLen);

                long hit;
                if (opts.CompiledRegex != null) {
                    var chunk = new string(dest);
                    Match? last = null;
                    foreach (Match m in opts.CompiledRegex.Matches(chunk)) {
                        if (chunkStart + m.Index < end) last = m;
                    }
                    hit = last != null ? chunkStart + last.Index : -1;
                } else {
                    var idx = dest.LastIndexOf(opts.Needle.AsSpan(), opts.Comparison);
                    hit = idx >= 0 ? chunkStart + idx : -1;
                }
                if (hit >= 0) return hit;

                chunkEnd = chunkStart + overlap;
                if (chunkEnd <= start) break;
            }
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
        return -1;
    }
}
