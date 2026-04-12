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
using DMEdit.App.Commands;
using DMEdit.App.Services;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.Core.Buffers;
using DMEdit.Core.Clipboard;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;
using DMEdit.Rendering.Layout;

namespace DMEdit.App.Controls;

/// <summary>
/// Custom plain-text editing control built directly on Avalonia's DrawingContext.
/// Implements <see cref="ILogicalScrollable"/> so a parent ScrollViewer cooperates
/// with windowed layout for large documents.
/// </summary>
public sealed partial class EditorControl : Control, ILogicalScrollable, IScrollSource {
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

    public static readonly StyledProperty<double> CaretWidthProperty =
        AvaloniaProperty.Register<EditorControl, double>(nameof(CaretWidth), 1.0);

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

    /// <summary>Zoom percentage (100 = 100%). Changing this invalidates layout.</summary>
    public int ZoomPercent {
        get => _zoomPercent;
        set {
            value = Math.Clamp(value, 10, 800);
            if (_zoomPercent == value) return;
            _zoomPercent = value;
            _rowHeight = 0;
            _charWidth = 0;
            InvalidateLayout();
        }
    }
    private int _zoomPercent = 100;

    /// <summary>FontSize scaled by the current zoom factor.</summary>
    private double EffectiveFontSize => FontSize * _zoomPercent / 100.0;

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

    public double CaretWidth {
        get => GetValue(CaretWidthProperty);
        set => SetValue(CaretWidthProperty, value);
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
    // Plain field — we deliberately do NOT eagerly dispose the previous
    // LayoutResult on assignment.  Eager dispose was tried briefly to make
    // GlyphRun native handle releases prompt; it produced an exponential
    // slowdown during column-mode rapid inserts because each insert
    // triggered Skia/HarfBuzz native releases (one per visible row) which
    // accumulated GC pressure across generations.  Now we let the previous
    // LayoutResult drift to the next Gen 0 collection like the beta did,
    // and rely on InvalidateLayout()'s explicit Dispose for the case where
    // we know the layout is being torn down for good (font change, document
    // swap, etc.).  The cached GlyphRuns inside MonoLineLayout are reused
    // across draws (per-row caching, see entry 21) so per-draw allocation
    // is unaffected by the orphan-and-GC strategy used here.
    private LayoutResult? _layout;
    /// <summary>
    /// Set when a layout pass throws an unrecoverable exception.  Once set,
    /// all subsequent layout/render attempts return an empty layout so the
    /// error dialog can paint without triggering the same crash again.
    /// </summary>
    private bool _layoutFailed;
    private bool _caretVisible = true;
    // True while we are inside Render() — guards against firing
    // HScrollChanged / ScrollChanged synchronously, which would invalidate
    // sibling controls (scrollbar, etc.) during the render pass and trip
    // Avalonia's "Visual was invalidated during the render pass" check.
    // When set, event invocations defer to the next dispatcher tick.
    private bool _inRenderPass;
    // Child overlay that paints the caret rectangle.  Living in its own
    // Control means caret blink can InvalidateVisual just the layer (~20 px
    // of dirty rect) instead of the whole EditorControl, which would force
    // the entire scene-graph for every visible row to be rebuilt every
    // ~530 ms.  See design journal entry 21 for the per-blink allocation
    // story that motivated this split.
    private CaretLayer? _primaryCaret;
    // Pool of additional caret layers used by column-selection (multi-cursor)
    // editing.  Index 0 of the visible carets always corresponds to
    // _primaryCaret; entries beyond that come from this pool.  Layers are
    // added to VisualChildren on demand and reused — never removed — so the
    // pool grows up to the largest column-selection ever seen and stays
    // there.
    private readonly List<CaretLayer> _columnCaretPool = new();
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
    internal double PreferredCaretXForTest => _preferredCaretX;

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
            // PreserveCaretScreenYAcross runs the mutation, invalidates layout,
            // then adjusts ScrollValue so the caret stays at the same screen-Y
            // position.  Previously the caret appeared to jump to a different
            // viewport row because _scrollOffset.Y was kept verbatim across
            // the toggle but mapped to a different row in the new layout.
            PreserveCaretScreenYAcross(() => {
                _wrapLines = value;
                // Column mode is incompatible with wrapping — exit if active.
                if (_wrapLines) {
                    if (Document?.ColumnSel != null) {
                        Document.ClearColumnSelection(_indentWidth);
                    }
                    // Reset horizontal scroll when wrapping is enabled.
                    HScrollValue = 0;
                }
            });
            if (_charWrapMode) {
                Dispatcher.UIThread.Post(() => ScrollCaretIntoView(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
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
            PreserveCaretScreenYAcross(() => _wrapLinesAt = value);
            if (_charWrapMode) {
                Dispatcher.UIThread.Post(() => ScrollCaretIntoView(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }

    /// <summary>
    /// When true, the editor uses character-wrapping mode: text wraps at exact
    /// character positions with O(1) scroll math.  Row N starts at character
    /// offset <c>N * CharsPerRow</c>.  Intended for large files with long lines.
    /// </summary>
    public bool CharWrapMode {
        get => _charWrapMode;
        set {
            if (_charWrapMode == value) return;
            _charWrapMode = value;
            InvalidateLayout();
        }
    }

    /// <summary>Characters per visual row in character-wrapping mode.</summary>
    public int CharsPerRow => _charWrapCharsPerRow;

    /// <summary>
    /// Reference to the app's settings, injected once during MainWindow init.
    /// Use this to read "passive" settings — values that are consulted at the
    /// moment of an action rather than triggering side effects on change.
    /// Active settings (those that need to invalidate layout, refresh menus,
    /// re-theme, etc. when changed) still get pushed in via individual
    /// properties so the setter can run side-effect code.  See
    /// MainWindow.Dialogs.cs SettingChanged switch for the active set.
    /// Nullable so the editor can be constructed before MainWindow assigns it.
    /// </summary>
    public Services.AppSettings? Settings { get; set; }

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

    /// <summary>Whether the WrapLinesAt column limit is enforced.</summary>
    public bool UseWrapColumn {
        get => _useWrapColumn;
        set {
            if (_useWrapColumn == value) return;
            PreserveCaretScreenYAcross(() => _useWrapColumn = value);
        }
    }

    /// <summary>Whether a wrap indicator glyph is drawn at the wrap column for wrapped lines.</summary>
    public bool ShowWrapSymbol {
        get => _showWrapSymbol;
        set {
            if (_showWrapSymbol == value) return;
            _showWrapSymbol = value;
            InvalidateLayout();
        }
    }

    /// <summary>
    /// Whether wrapped continuation rows are indented by half of one indent
    /// level (the "hanging indent" effect).  Currently only honored on the
    /// monospace GlyphRun fast path; proportional fonts ignore this setting.
    /// </summary>
    public bool HangingIndent {
        get => _hangingIndent;
        set {
            if (_hangingIndent == value) return;
            PreserveCaretScreenYAcross(() => _hangingIndent = value);
        }
    }

    /// <summary>
    /// When true (default), monospace lines go through the <c>MonoLineLayout</c>
    /// GlyphRun fast path — faster rendering and enables hanging indent, but
    /// no font ligatures.  When false, every line routes through Avalonia's
    /// <c>TextLayout</c>, which is slower and has no hanging indent but
    /// renders ligatures like <c>=&gt;</c> as single shaped glyphs.
    /// </summary>
    public bool UseFastTextLayout {
        get => _useFastTextLayout;
        set {
            if (_useFastTextLayout == value) return;
            _useFastTextLayout = value;
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
    private double RenderOffsetY {
        get => _renderOffsetY;
        set => _renderOffsetY = value; // tracepoint here
    }

    /// <summary>Test-only: the sub-pixel offset of the first visible line
    /// from the viewport top.  Negative means the first row is partially
    /// above the viewport (expected after a sub-row scroll).</summary>
    internal double RenderOffsetYForTest => _renderOffsetY;
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
            FireHScrollChanged();
        }
    }

    /// <summary>Maximum horizontal scroll value.</summary>
    public double HScrollMaximum => Math.Max(0, _extent.Width - _viewport.Width);

    /// <summary>Raised when the horizontal extent or scroll changes.</summary>
    public event Action? HScrollChanged;

    // Word wrap
    private bool _wrapLines = false;
    private bool _useWrapColumn = true;
    private int _wrapLinesAt = 100;

    /// <summary>True when both UseWrapColumn is on and a valid column limit is set.</summary>
    private bool WrapColumnActive => _useWrapColumn && _wrapLinesAt >= 1;

    // Character-wrapping mode
    private bool _charWrapMode;
    private int _charWrapCharsPerRow;

    // Indentation
    private int _indentWidth = 4;

    // Whitespace visibility
    private bool _showWhitespace;

    // Wrap symbol
    private bool _showWrapSymbol = true;
    private const double WrapSymbolPadBase = 12;
    private double WrapSymbolPadRight => WrapSymbolPadBase * _zoomPercent / 100.0;
    // Cached TextLayout for the wrap-symbol glyph.  Previously rebuilt on
    // every Render() call (i.e. every caret blink) which was a significant
    // per-frame allocation: TextLayout construction runs text shaping and
    // allocates one or more GlyphRun objects internally.  Invalidated when
    // font size, theme brush, or font family change.
    private TextLayout? _wrapSymbolLayout;
    private double _wrapSymbolLayoutFontSize;
    private IBrush? _wrapSymbolLayoutBrush;

    // Cached TextLayouts for gutter line numbers.  DrawGutter previously
    // created a fresh TextLayout per visible line per Render() — 50 allocs
    // + 50 text-shaping passes per caret blink on a typical viewport.
    // Now keyed by the digit string; invalidated when font size, font
    // family, gutter width, or gutter brush change.
    private readonly Dictionary<string, TextLayout> _gutterNumCache = new();
    private double _gutterCacheFontSize;
    private double _gutterCacheMaxWidth;
    private IBrush? _gutterCacheBrush;
    private string? _gutterCacheFontFamily;

    // Hanging indent on wrapped continuation rows.  Only takes effect when
    // wrapping is on, the editor font resolves to a single GlyphTypeface,
    // and that face is monospace — the GlyphRun fast path applies.  See
    // design journal 20 for the rationale.
    private bool _hangingIndent = true;

    // Escape hatch for users who want ligatures at the cost of speed and
    // hanging indent.  When false, LayoutLines is told to skip building a
    // MonoLayoutContext so every line falls back to TextLayout.
    private bool _useFastTextLayout = true;

    // Line number gutter
    private bool _showLineNumbers = true;
    private double _gutterWidth;
    public double GutterWidth => _gutterWidth;
    public double CharWidth => GetCharWidth();
    private int _gutterDigitCnt;
    private const double GutterPadLeft = 6;
    private const double GutterPadRight = 6;
    private const double TextAreaPadRight = 6;

    // Theme — set by MainWindow when the effective theme changes.
    private EditorTheme _theme = EditorTheme.Light;

    // Incremental scroll tracking — used by LayoutWindowed to produce
    // pixel-perfect smooth scrolling even when topLine changes.
    private long _winTopLine = -1;
    private double _winScrollOffset;
    private double _winRenderOffsetY;
    private double _winFirstLineHeight;

    // Cached text-area width from the most recent EnsureLayout pass.
    // ComputeLineRowCount / ComputeRowOfCharInLine must use the same
    // width the layout engine used; recomputing from Bounds can disagree
    // when called during Measure before Bounds is set.
    private double _lastTextWidth;

    // Exact-pin flag — set by ScrollExact, consumed by ONE LayoutWindowed
    // pass, then cleared.  While set, LayoutWindowed trusts the cached
    // topLine exactly (skipping pull-back and the extent estimate for
    // end-of-doc targets).  Without this gate the pull-back skip and
    // tight-extent math incorrectly fire during ordinary wheel/drag/
    // arrow scrolls where `_winTopLine == topLine` holds only because
    // the previous frame happened to end at the same topLine.
    private bool _winExactPinActive;

    // Caret "is at end of row" flag.  When true, a caret sitting at a
    // soft line break boundary renders at the END of the current row
    // instead of the START of the next row (the default).  Set by End
    // key and mouse click on the right edge of a wrapped row.  Reset
    // to false by Home key, arrow keys, edits, and most other caret
    // movements.
    private bool _caretIsAtEnd;

    /// <summary>Whether the caret is at the end-of-row position (for status bar display).</summary>
    public bool CaretIsAtEnd => _caretIsAtEnd;

    // -------------------------------------------------------------------------
    // Performance stats
    // -------------------------------------------------------------------------
    // Nested TimingStat / PerfStatsData classes live in
    // EditorControl.PerfStats.cs.

    private readonly Stopwatch _perfSw = new();
    private readonly Stopwatch _editSw = new();

    /// <summary>Live performance stats. Updated after each render.</summary>
    public PerfStatsData PerfStats { get; } = new();

    /// <summary>Fired after each render with updated stats.</summary>
    public event Action? StatusUpdated;

    /// <summary>
    /// Fires when a line is too long for normal layout mode.  The editor
    /// auto-switches to character-wrapping mode; the handler should show
    /// a user-friendly notification.
    /// </summary>
    public event Action<LineTooLongException>? LineTooLongDetected;

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
            // The caret layer's width (thin bar vs block) is recomputed in
            // UpdateCaretLayers/ArrangeCaretAt from ArrangeOverride — neither
            // is driven by InvalidateVisual.  ResetCaretBlink forces an
            // arrange pass and also makes the caret immediately visible so
            // the user sees the new shape on the toggle instead of waiting
            // for the next blink tick.
            ResetCaretBlink();
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

    /// <summary>
    /// Fires <see cref="HScrollChanged"/>, deferring to the next dispatcher
    /// tick if we are currently inside a render pass.  Sibling controls
    /// (the scrollbars) react to this event by setting their own properties,
    /// which mutate the visual tree — and Avalonia forbids visual tree
    /// mutation during render.
    /// </summary>
    private void FireHScrollChanged() {
        if (_inRenderPass) {
            Dispatcher.UIThread.Post(() => HScrollChanged?.Invoke());
        } else {
            HScrollChanged?.Invoke();
        }
    }

    private void FireScrollChanged() {
        if (_inRenderPass) {
            Dispatcher.UIThread.Post(() => ScrollChanged?.Invoke(this, EventArgs.Empty));
        } else {
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
                // Hide caret during scroll. For drags the !scrollDrag gate
                // in UpdateCaretLayers suppresses drawing; InteractionEnded
                // shows it on release. For wheel/arrow, ResetCaretBlink()
                // re-shows it after the scroll command completes.
                _caretVisible = false;
                SetCaretLayersVisible(false);
                InvalidateVisual();
                InvalidateArrange();
                FireScrollChanged();
            }
        }
    }

    /// <summary>Viewport width in pixels (valid during measure, before arrange sets Bounds).</summary>
    public double ViewportWidth => _viewport.Width;

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

        // Caret overlay child — added to VisualChildren so its Render runs
        // after our own and the caret paints over text/selection.
        _primaryCaret = new CaretLayer { Brush = CaretBrush, CaretWidth = CaretWidth };
        VisualChildren.Add(_primaryCaret);

        BuildContextMenu();
    }

    private void BuildContextMenu() {
        var cut = new MenuItem { Header = "Cu_t", FontSize = 12 };
        var copy = new MenuItem { Header = "_Copy", FontSize = 12 };
        var paste = new MenuItem { Header = "_Paste", FontSize = 12 };
        cut.Click += (_, _) => Cmd.EditCut.Run();
        copy.Click += (_, _) => Cmd.EditCopy.Run();
        paste.Click += (_, _) => Cmd.EditPaste.Run();
        ContextMenu = new ContextMenu {
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Arrow),
            Items = { cut, copy, paste },
        };
        ContextMenu.Opening += (_, _) => {
            cut.IsEnabled = Cmd.EditCut.IsEnabled;
            copy.IsEnabled = Cmd.EditCopy.IsEnabled;
            paste.IsEnabled = Cmd.EditPaste.IsEnabled;
        };
    }

    // -------------------------------------------------------------------------
    // ILogicalScrollable
    // -------------------------------------------------------------------------

    bool ILogicalScrollable.IsLogicalScrollEnabled => true;
    bool ILogicalScrollable.CanHorizontallyScroll { get; set; }
    bool ILogicalScrollable.CanVerticallyScroll { get; set; }

    bool IScrollable.CanHorizontallyScroll => true;
    bool IScrollable.CanVerticallyScroll => true;
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
                FireScrollChanged();
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
            InvalidateRowIndex(); // new document — row counts are stale
            InvalidateLayout();
        } else if (e.Property == FontFamilyProperty
                     || e.Property == FontSizeProperty
                     || e.Property == ForegroundBrushProperty) {
            _rowHeight = 0;
            _charWidth = 0;
            InvalidateRowIndex(); // font change — row counts depend on char width
            InvalidateLayout();
        } else if (e.Property == CaretBrushProperty || e.Property == CaretWidthProperty) {
            // Push directly into the layer; UpdateCaretLayers also picks
            // these up on its next pass, but doing it here means a theme
            // switch repaints the caret immediately even if no other
            // layout-affecting state changes.
            if (_primaryCaret is not null) {
                _primaryCaret.Brush = CaretBrush;
                _primaryCaret.CaretWidth = CaretWidth;
            }
            for (var i = 0; i < _columnCaretPool.Count; i++) {
                _columnCaretPool[i].Brush = CaretBrush;
                _columnCaretPool[i].CaretWidth = CaretWidth;
            }
        }
    }

    // Layout — see EditorControl.Layout.cs

    // Rendering — see EditorControl.Render.cs


    // -------------------------------------------------------------------------
    // Edit coalescing — see EditorControl.Coalesce.cs
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Public edit commands (invoked by menu and keyboard shortcuts)
    // -------------------------------------------------------------------------

    // Copy / Cut / Paste / PasteMore / clipboard ring — see EditorControl.Clipboard.cs

    // Edit commands, caret motion, indent helpers — see EditorControl.Commands.cs


    // Caret hit-test helpers + ScrollCaretIntoView / ScrollPage / FindLastVisibleLogicalLine — see EditorControl.Scroll.cs


    // Pointer / mouse wheel input — see EditorControl.Input.cs


    // MoveCaretByPage / OnDocumentChanged / Save/Replace/RestoreScrollState — see EditorControl.Scroll.cs


    // Search / Replace / Incremental search — see EditorControl.Search.cs
}
