using System.Diagnostics;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DMEdit.Core.Documents;
using DMEdit.Core.Printing;
using FlowDirection = System.Windows.FlowDirection;
using PageOrientation = DMEdit.Core.Printing.PageOrientation;

namespace DMEdit.Windows;

/// <summary>
/// Windows implementation of <see cref="ISystemPrintService"/> using WPF's
/// <see cref="DocumentPaginator"/> and <see cref="System.Printing"/> APIs.
/// The paginator streams pages on demand — only one page is in memory at a time.
/// </summary>
public sealed class WpfPrintService : ISystemPrintService {

    // Paper sizes considered "common" — shown when the user hasn't toggled
    // the full list. Based on PageMediaSizeName enum values.
    private static readonly HashSet<PageMediaSizeName> CommonSizes = [
        PageMediaSizeName.NorthAmericaLetter,
        PageMediaSizeName.NorthAmericaLegal,
        PageMediaSizeName.NorthAmericaExecutive,
        PageMediaSizeName.NorthAmerica11x17,
        PageMediaSizeName.NorthAmericaNumber10Envelope,
        PageMediaSizeName.ISOA3,
        PageMediaSizeName.ISOA4,
        PageMediaSizeName.ISOA5,
        PageMediaSizeName.ISOB5Envelope,
        PageMediaSizeName.JapanDoubleHagakiPostcard,
    ];

    public IReadOnlyList<PrinterInfo> GetPrinters() {
        var result = new List<PrinterInfo>();
        try {
            var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(
                [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
            var defaultName = LocalPrintServer.GetDefaultPrintQueue()?.FullName;

            foreach (var q in queues) {
                result.Add(new PrinterInfo {
                    Name = q.FullName,
                    IsDefault = q.FullName == defaultName,
                });
            }
        } catch {
            // Printer enumeration can fail if the spooler is stopped, etc.
        }
        return result;
    }

    public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string printerName) {
        var result = new List<PaperSizeInfo>();
        try {
            var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(printerName);
            var caps = queue.GetPrintCapabilities();

            foreach (var size in caps.PageMediaSizeCapability) {
                if (size.Width is not { } w || size.Height is not { } h) {
                    continue;
                }
                var sizeName = size.PageMediaSizeName ?? PageMediaSizeName.Unknown;
                // Convert from WPF units (1/96 in) to points (1/72 in).
                var widthPt = w * 72.0 / 96.0;
                var heightPt = h * 72.0 / 96.0;

                result.Add(new PaperSizeInfo {
                    Name = FormatPaperName(sizeName, widthPt, heightPt),
                    Width = widthPt,
                    Height = heightPt,
                    Id = (int)sizeName,
                    IsCommon = CommonSizes.Contains(sizeName),
                });
            }
        } catch {
            // Fall back to built-in defaults if enumeration fails.
        }
        return result.Count > 0 ? result : (IReadOnlyList<PaperSizeInfo>)PaperSizeInfo.Defaults;
    }

    private static string FormatPaperName(PageMediaSizeName name, double widthPt, double heightPt) {
        // Convert the enum name from PascalCase to spaced words.
        var label = name.ToString();
        // Insert spaces before capitals (except runs of capitals like "ISO")
        var sb = new System.Text.StringBuilder(label.Length + 8);
        for (var i = 0; i < label.Length; i++) {
            var c = label[i];
            if (i > 0 && char.IsUpper(c)) {
                var prev = label[i - 1];
                var next = i + 1 < label.Length ? label[i + 1] : '\0';
                // Insert space before: lowercase→Upper, or Upper→Upper+lower
                if (char.IsLower(prev) || (char.IsUpper(prev) && char.IsLower(next))) {
                    sb.Append(' ');
                }
            }
            sb.Append(c);
        }

        // Append dimensions in inches.
        var wIn = widthPt / 72.0;
        var hIn = heightPt / 72.0;
        sb.Append($"  ({wIn:0.##} × {hIn:0.##} in)");
        return sb.ToString();
    }

    public PrintResult Print(Document doc, PrintJobTicket ticket,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default) {
        Exception? error = null;

        var thread = new Thread(() => {
            try {
                PrintOnWpfThread(doc, ticket, progress, cancellation);
            } catch (System.Runtime.CompilerServices.RuntimeWrappedException rwe) {
                // WPF's managed-C++ internals (System.Printing /
                // PresentationCore) sometimes throw non-Exception objects.
                // Unwrap so Message is a single readable line; the full dump
                // including stack traces ends up in ToString() → ErrorDetails.
                var wrapped = rwe.WrappedException;
                var typeName = wrapped?.GetType().FullName ?? "(null)";
                var wrappedStr = wrapped?.ToString() ?? "(no detail)";
                error = new InvalidOperationException(
                    $"WPF print threw a non-Exception: [{typeName}] {wrappedStr}",
                    rwe);
            } catch (Exception ex) {
                error = ex;
            }
        }) {
            // Background so the OS can terminate it on app exit — WPF's
            // XpsDocumentWriter.Write is synchronous and uncancellable, and
            // a foreground thread stuck in the spooler blocks process exit.
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Poll Join so we can return promptly when the caller cancels.
        // On cancel we give the paginator a brief grace period to bail out
        // cleanly, then abandon the thread (it's a background thread so it
        // won't keep the process alive).
        while (!thread.Join(100)) {
            if (cancellation.IsCancellationRequested) {
                thread.Join(500);
                break;
            }
        }

        if (error is not null && error is not OperationCanceledException) {
            // Format the exception fully (including inner exceptions) on the
            // print thread side so the caller only sees plain strings — no
            // Exception objects crossing thread boundaries.
            return PrintResult.Failed(error.Message, error.ToString());
        }
        if (cancellation.IsCancellationRequested) {
            return PrintResult.CancelledResult();
        }
        return PrintResult.Ok();
    }

    private static void PrintOnWpfThread(Document doc, PrintJobTicket ticket,
        IProgress<(string Message, double Percent)>? progress,
        CancellationToken cancellation) {
        var server = new LocalPrintServer();
        PrintQueue? queue = null;
        try {
            queue = server.GetPrintQueue(ticket.PrinterName);
        } catch {
            // Fall back to default if the named printer isn't found.
        }
        queue ??= LocalPrintServer.GetDefaultPrintQueue();
        if (queue is null) {
            throw new InvalidOperationException("No printer available.");
        }

        var settings = ticket.Settings;
        var (pageW, pageH) = settings.GetPageSize();

        // WPF uses 1/96-inch units; our settings are in points (1/72-inch).
        var wpfPageW = pageW * 96.0 / 72.0;
        var wpfPageH = pageH * 96.0 / 72.0;
        var wpfMarginT = settings.Margins.Top * 96.0 / 72.0;
        var wpfMarginR = settings.Margins.Right * 96.0 / 72.0;
        var wpfMarginB = settings.Margins.Bottom * 96.0 / 72.0;
        var wpfMarginL = settings.Margins.Left * 96.0 / 72.0;

        var paginator = new PlainTextPaginator(
            doc, wpfPageW, wpfPageH, wpfMarginT, wpfMarginR, wpfMarginB, wpfMarginL,
            ticket.FontFamily, ticket.FontSizePoints, ticket.IndentWidth,
            ticket.UseGlyphRun, progress, cancellation);

        var pt = queue.DefaultPrintTicket.Clone();
        if (settings.Paper.Id is { } sizeId) {
            pt.PageMediaSize = new PageMediaSize((PageMediaSizeName)sizeId);
        } else {
            // Fallback: set by explicit dimensions (WPF units = 1/96 inch).
            pt.PageMediaSize = new PageMediaSize(
                settings.Paper.Width * 96.0 / 72.0,
                settings.Paper.Height * 96.0 / 72.0);
        }
        pt.PageOrientation = settings.Orientation == PageOrientation.Landscape
            ? System.Printing.PageOrientation.Landscape
            : System.Printing.PageOrientation.Portrait;
        pt.CopyCount = Math.Max(1, ticket.Copies);

        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(paginator, pt);

    }
}

/// <summary>
/// Streaming paginator for plain-text documents. Computes page breaks once
/// (measurement-only pass), then renders individual pages on demand via
/// <see cref="DocumentPaginator.GetPage"/>.
/// </summary>
file sealed class PlainTextPaginator : DocumentPaginator {

    private const string DefaultFontFamily = "Cascadia Code, Consolas, Courier New";
    // Default 11 *typographic points*, converted to WPF DIPs (1/96 inch)
    // below.  Used only when the ticket doesn't supply a size.
    private const double DefaultFontSizePoints = 11.0;

    private readonly Document _doc;
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly double _lineHeight;
    private readonly double _pageWidth;
    private readonly double _pageHeight;
    private readonly double _marginTop;
    private readonly double _marginLeft;
    private readonly double _printableWidth;
    private readonly double _printableHeight;
    private readonly int _linesPerPage;
    private readonly int _safeCharCount; // lines shorter than this can't wrap
    // Hanging indent applied to wrapped continuation rows so wrapped text is
    // visually offset from the first row.  Half of one indent column,
    // measured in characters and pixels (monospace assumption — see entry 19
    // for the proportional-font follow-up).
    private readonly int _hangingIndentChars;
    private readonly double _hangingIndentPx;
    private readonly List<PageBreak> _pageBreaks;
    private readonly IProgress<(string Message, double Percent)>? _progress;
    private readonly CancellationToken _cancellation;

    // GlyphRun fast path.  Each monospace row is drawn via DrawGlyphRun with
    // a glyph-index array looked up through CharacterToGlyphMap — dramatically
    // faster than per-row FormattedText.  If glyph-typeface resolution fails
    // (e.g. the configured font isn't installed) or a row contains a codepoint
    // the face has no glyph for, that row falls back to FormattedText.
    private readonly GlyphTypeface? _glyphTypeface;
    private readonly ushort[] _asciiGlyphs = new ushort[128];
    private readonly double _glyphAdvance;
    private readonly double _glyphBaseline;

    // Rolling-window rate tracking for the "pages/sec" readout.  The clock
    // starts on the first GetPage call (not in the constructor, which does
    // the potentially-slow pagination pass) so the reported rate reflects
    // actual render throughput rather than an ever-increasing cumulative
    // average that drags in the startup cost.
    private readonly Stopwatch _renderElapsed = new();
    private readonly Queue<(TimeSpan Time, int Page)> _rateSamples = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);
    // Throttle progress reports: at 60+ pages/sec the UI dispatcher can't
    // consume a report per page, the queue backs up unbounded, and the user
    // sees historical values replayed from the backlog — which looks like
    // the rate is slowly ramping up when it's actually stable.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private TimeSpan _lastReportTime = TimeSpan.FromSeconds(-1);

    public PlainTextPaginator(
        Document doc,
        double pageWidth, double pageHeight,
        double marginTop, double marginRight, double marginBottom, double marginLeft,
        string? fontFamily = null,
        double? fontSizePoints = null,
        int indentWidth = 4,
        bool useGlyphRun = true,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default) {
        _doc = doc;
        _typeface = new Typeface(fontFamily ?? DefaultFontFamily);
        // WPF's emSize is in DIPs (1/96 inch), but the user / settings
        // express the editor font in typographic points (1/72 inch).
        // Convert pt → DIP so the printout matches the editor visually.
        var sizePts = fontSizePoints ?? DefaultFontSizePoints;
        _fontSize = sizePts * 96.0 / 72.0;

        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _marginTop = marginTop;
        _marginLeft = marginLeft;
        _printableWidth = Math.Max(1, pageWidth - marginLeft - marginRight);
        _printableHeight = Math.Max(1, pageHeight - marginTop - marginBottom);

        var sample = MakeFormattedText("Xg");
        _lineHeight = sample.Height;

        _linesPerPage = Math.Max(1, (int)(_printableHeight / _lineHeight));
        // Compute chars per print row using the monospace font's character width.
        var charWidth = MakeFormattedText("M").Width;
        _safeCharCount = charWidth > 0 ? (int)(_printableWidth / charWidth) : int.MaxValue;

        // Hanging indent: half of one indent column.  Continuation rows of a
        // wrapped line are offset right by this amount and have their
        // available char count reduced accordingly so word-wrap stays inside
        // the printable area.  Clamp so we always leave at least 1 char of
        // wrap width even with very wide indents on narrow paper.
        _hangingIndentChars = Math.Max(0, indentWidth / 2);
        if (_hangingIndentChars >= _safeCharCount) {
            _hangingIndentChars = Math.Max(0, _safeCharCount - 1);
        }
        _hangingIndentPx = _hangingIndentChars * charWidth;

        _progress = progress;
        _cancellation = cancellation;

        // Try to resolve a GlyphTypeface for the monospace fast path.  A
        // multi-family fallback Typeface (e.g. "Cascadia Code, Consolas,
        // Courier New") will not resolve directly via TryGetGlyphTypeface
        // because WPF does fallback resolution inside FormattedText shaping,
        // not at typeface construction.  So we walk the family list and pick
        // the first single-family Typeface that resolves.
        GlyphTypeface? gtf = null;
        if (useGlyphRun) {
            if (!_typeface.TryGetGlyphTypeface(out gtf)) {
                foreach (var family in DefaultFontFamily.Split(',')) {
                    var trimmed = family.Trim();
                    if (trimmed.Length == 0) continue;
                    try {
                        var single = new Typeface(trimmed);
                        if (single.TryGetGlyphTypeface(out gtf)) break;
                    } catch {
                        // Bad family name — try the next one.
                    }
                }
            }
        }
        if (gtf is not null) {
            _glyphTypeface = gtf;
            _glyphBaseline = gtf.Baseline * _fontSize;
            // Log which concrete face the GlyphRun fast path landed on, so
            // we can tell if the fallback chain picked something unexpected.
            var familyName = gtf.FamilyNames.Values.FirstOrDefault() ?? "(unknown)";
            string fileName;
            try { fileName = System.IO.Path.GetFileName(gtf.FontUri.LocalPath); }
            catch { fileName = "(no file)"; }
            System.Diagnostics.Trace.WriteLine(
                $"[WpfPrintService] GlyphRun face: {familyName} — {fileName}");
            var map = gtf.CharacterToGlyphMap;
            ushort fallbackGlyph = 0;
            if (map.TryGetValue(' ', out var g)) fallbackGlyph = g;
            else if (map.TryGetValue('M', out g)) fallbackGlyph = g;
            _glyphAdvance = gtf.AdvanceWidths[fallbackGlyph] * _fontSize;
            for (var i = 32; i < 128; i++) {
                _asciiGlyphs[i] = map.TryGetValue(i, out var gi) ? gi : fallbackGlyph;
            }
        }

        _pageBreaks = ComputePageBreaks();
    }

    public override DocumentPage GetPage(int pageNumber) {
        if (pageNumber < 0 || pageNumber >= _pageBreaks.Count) {
            return DocumentPage.Missing;
        }

        // On cancellation, return blank pages so the spooler finishes quickly
        // instead of blocking for 30+ seconds cleaning up after an exception.
        if (_cancellation.IsCancellationRequested) {
            var blank = new DrawingVisual();
            using (blank.RenderOpen()) { }
            return new DocumentPage(blank,
                new Size(_pageWidth, _pageHeight),
                new Rect(0, 0, _pageWidth, _pageHeight),
                new Rect(0, 0, _pageWidth, _pageHeight));
        }

        var total = _pageBreaks.Count;
        var page1 = pageNumber + 1;
        var pct = 100.0 * page1 / total;

        // Start the render clock on the first page, not in the constructor.
        if (!_renderElapsed.IsRunning) _renderElapsed.Start();
        var now = _renderElapsed.Elapsed;

        // Maintain a rolling window of (time, page) samples so we can report
        // a rate based on the last few seconds rather than a cumulative mean.
        _rateSamples.Enqueue((now, page1));
        while (_rateSamples.Count > 1 && now - _rateSamples.Peek().Time > RateWindow) {
            _rateSamples.Dequeue();
        }

        // Throttle progress reports to at most one per ReportInterval so we
        // don't flood the UI dispatcher. Always report the final page.
        var isLastPage = page1 == total;
        if (now - _lastReportTime >= ReportInterval || isLastPage) {
            _lastReportTime = now;

            string status;
            // Need at least ~0.5s of window data before the rate is meaningful.
            var oldest = _rateSamples.Peek();
            var windowDt = (now - oldest.Time).TotalSeconds;
            if (_rateSamples.Count >= 2 && windowDt >= 0.5) {
                var windowDp = page1 - oldest.Page;
                var pagesPerSec = windowDp / windowDt;
                var remaining = TimeSpan.FromSeconds((total - page1) / Math.Max(pagesPerSec, 0.001));
                var eta = remaining.TotalMinutes >= 1
                    ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
                    : $"{remaining.Seconds}s";
                status = $"Page {page1:N0} of {total:N0}\n{pagesPerSec:N0} pages/sec, ~{eta} remaining";
            } else {
                status = $"Page {page1:N0} of {total:N0}\ncalculating rate\u2026";
            }
            _progress?.Report((status, pct));
        }

        var brk = _pageBreaks[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            var y = _marginTop;
            var visualLineIdx = 0;
            var lineIdx = brk.FirstLogicalLine;
            var wrapIdx = brk.FirstWrapLine;

            var charsPerRow = Math.Max(1, _safeCharCount);
            var continuationCharsPerRow = Math.Max(1, charsPerRow - _hangingIndentChars);
            while (visualLineIdx < _linesPerPage && lineIdx < _doc.Table.LineCount) {
                var line = _doc.Table.GetLine(lineIdx);

                // Word-break wrap.  Walk the line computing row boundaries
                // via NextRow, which matches ComputePageBreaks so page
                // breaks land where the paginator decided they would.
                // wrapIdx tells us how many rows at the start of this line
                // belong to the previous page and should be skipped.  Note
                // we still walk all rows from the start of the line — the
                // word-break positions depend on the line text and a
                // continuation row carried over from the previous page must
                // begin at the same character position the previous page
                // would have computed.
                var rowStart = 0;
                var skipped = 0;
                var rowInLine = 0;
                while (rowStart < line.Length && visualLineIdx < _linesPerPage) {
                    var rowChars = rowInLine == 0 ? charsPerRow : continuationCharsPerRow;
                    var (drawLen, nextStart) = NextRow(line, rowStart, rowChars);
                    if (skipped < wrapIdx) {
                        skipped++;
                    } else {
                        // Continuation rows are offset right by the hanging
                        // indent so wrapped text is visually distinct from
                        // the first row of each logical line.
                        var rowX = _marginLeft + (rowInLine == 0 ? 0 : _hangingIndentPx);
                        DrawRow(dc, line, rowStart, drawLen, rowX, y);
                        y += _lineHeight;
                        visualLineIdx++;
                    }
                    rowStart = nextStart;
                    rowInLine++;
                }
                // Empty lines still occupy one visual row.
                if (line.Length == 0 && wrapIdx == 0 && visualLineIdx < _linesPerPage) {
                    y += _lineHeight;
                    visualLineIdx++;
                }
                wrapIdx = 0;
                lineIdx++;
            }
        }

        return new DocumentPage(
            visual,
            new Size(_pageWidth, _pageHeight),
            new Rect(_marginLeft, _marginTop, _printableWidth, _printableHeight),
            new Rect(_marginLeft, _marginTop, _printableWidth, _printableHeight));
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageBreaks.Count;

    public override Size PageSize {
        get => new(_pageWidth, _pageHeight);
        set { /* required by abstract base */ }
    }

    public override IDocumentPaginatorSource? Source => null;

    // -----------------------------------------------------------------
    // Pagination
    // -----------------------------------------------------------------

    private List<PageBreak> ComputePageBreaks() {
        var breaks = new List<PageBreak> { new(0, 0) };
        var table = _doc.Table;
        var lineCount = table.LineCount;
        var visualLinesOnPage = 0;
        var lastReport = Stopwatch.GetTimestamp();
        var charsPerRow = Math.Max(1, _safeCharCount);

        for (var i = 0L; i < lineCount; i++) {
            // Report progress at most every 200ms to avoid flooding the UI.
            if (Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 200) {
                _cancellation.ThrowIfCancellationRequested();
                _progress?.Report(($"Measuring line {i:N0} of {lineCount:N0}\u2026",
                    100.0 * i / lineCount));
                lastReport = Stopwatch.GetTimestamp();
            }

            // Compute line content length (excluding newline) from the tree.
            var lineStart = table.LineStartOfs(i);
            var nextStart = i + 1 < lineCount
                ? table.LineStartOfs(i + 1)
                : _doc.Table.Length;
            // Subtract up to 2 chars for the newline (CRLF=2, CR/LF=1).
            // GetLine() strips newlines, so the measurement must match.
            // Conservative: subtract 2 for non-last lines (off by at most
            // 1 char for LF-only, negligible for pagination).
            var rawLen = (int)(nextStart - lineStart);
            var lineLen = i + 1 < lineCount ? Math.Max(0, rawLen - 2) : rawLen;

            // Short-line fast path: 1 row, no text read needed.
            if (lineLen <= charsPerRow) {
                visualLinesOnPage++;
                if (visualLinesOnPage > _linesPerPage) {
                    breaks.Add(new PageBreak(i, 0));
                    visualLinesOnPage = 1;
                }
                continue;
            }

            // Long line: read once and walk word-breaks.  We have to
            // materialize the line text here to find space positions; GetPage
            // does the same walk and must see the same breaks so pagination
            // stays consistent with rendering.  First row uses the full
            // charsPerRow; continuation rows use the hanging-indent reduction.
            var text = table.GetLine(i);
            var pos = 0;
            var w = 0;
            var continuationCharsPerRow = Math.Max(1, charsPerRow - _hangingIndentChars);
            while (pos < text.Length) {
                var rowChars = w == 0 ? charsPerRow : continuationCharsPerRow;
                var step = NextRow(text, pos, rowChars);
                visualLinesOnPage++;
                if (visualLinesOnPage > _linesPerPage) {
                    breaks.Add(new PageBreak(i, w));
                    visualLinesOnPage = 1;
                }
                pos = step.NextStart;
                w++;
            }
        }

        return breaks;
    }



    /// <summary>
    /// Draws one visual row of <paramref name="line"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>).  Uses the <see cref="GlyphRun"/> fast path when
    /// the glyph typeface resolved and every character is printable ASCII;
    /// otherwise falls back to <see cref="FormattedText"/> so tabs, box
    /// drawing, CJK, etc. still render correctly.
    /// </summary>
    private void DrawRow(DrawingContext dc, string line, int rowStart, int rowLen, double x, double y) {
        if (_glyphTypeface is null) {
            SlowPath(dc, line, rowStart, rowLen, x, y);
            return;
        }

        // Try to build the glyph array.  ASCII 32-126 is a direct table lookup;
        // everything else goes through CharacterToGlyphMap so any codepoint the
        // font has a glyph for still fast-paths.  Tabs and other control chars
        // fall through to FormattedText because their advance isn't uniform.
        var glyphs = new ushort[rowLen];
        var advances = new double[rowLen];
        var map = _glyphTypeface.CharacterToGlyphMap;
        for (var i = 0; i < rowLen; i++) {
            var c = line[rowStart + i];
            ushort g;
            if (c >= 32 && c < 128) {
                g = _asciiGlyphs[c];
            } else if (c < 32 || !map.TryGetValue(c, out g)) {
                SlowPath(dc, line, rowStart, rowLen, x, y);
                return;
            }
            glyphs[i] = g;
            advances[i] = _glyphAdvance;
        }

        var run = new GlyphRun(
            _glyphTypeface,
            bidiLevel: 0,
            isSideways: false,
            renderingEmSize: _fontSize,
            pixelsPerDip: 1.0f,
            glyphIndices: glyphs,
            baselineOrigin: new Point(x, y + _glyphBaseline),
            advanceWidths: advances,
            glyphOffsets: null,
            characters: null,
            deviceFontName: null,
            clusterMap: null,
            caretStops: null,
            language: null);
        dc.DrawGlyphRun(Brushes.Black, run);
    }

    /// <summary>
    /// Computes the break position for the next visual row starting at
    /// <paramref name="rowStart"/> in <paramref name="line"/>.  Word-break
    /// rules: if a space exists inside the row width, break at the last
    /// space — the space itself is dropped from the drawn row and the next
    /// row starts after it.  If no space is found (a very long unbroken
    /// token), fall back to a hard break at <paramref name="charsPerRow"/>.
    /// Used by both <see cref="ComputePageBreaks"/> and
    /// <see cref="GetPage"/> so pagination and rendering stay in sync.
    /// </summary>
    private static (int DrawLen, int NextStart) NextRow(string line, int rowStart, int charsPerRow) {
        var remaining = line.Length - rowStart;
        if (remaining <= charsPerRow) {
            return (remaining, line.Length);
        }
        var hardLimit = rowStart + charsPerRow;
        for (var i = hardLimit - 1; i > rowStart; i--) {
            if (line[i] == ' ') {
                return (i - rowStart, i + 1);
            }
        }
        return (charsPerRow, hardLimit);
    }

    private void SlowPath(DrawingContext dc, string line, int rowStart, int rowLen, double x, double y) {
        var segment = line.Substring(rowStart, rowLen);
        var ft = MakeFormattedText(segment);
        dc.DrawText(ft, new Point(x, y));
    }

    private FormattedText MakeFormattedText(string text) {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.Black,
            96.0);
    }

    private record struct PageBreak(long FirstLogicalLine, int FirstWrapLine);
}
