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

    public bool Print(Document doc, PrintJobTicket ticket,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default) {
        Exception? error = null;

        var thread = new Thread(() => {
            try {
                PrintOnWpfThread(doc, ticket, progress, cancellation);
            } catch (Exception ex) {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null && error is not OperationCanceledException) {
            return false;
        }
        return !cancellation.IsCancellationRequested;
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
            progress, cancellation);

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
    private const double DefaultFontSize = 11.0;

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
    private readonly List<PageBreak> _pageBreaks;
    private readonly IProgress<(string Message, double Percent)>? _progress;
    private readonly CancellationToken _cancellation;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public PlainTextPaginator(
        Document doc,
        double pageWidth, double pageHeight,
        double marginTop, double marginRight, double marginBottom, double marginLeft,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default) {
        _doc = doc;
        _typeface = new Typeface(DefaultFontFamily);
        _fontSize = DefaultFontSize;

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
        _progress = progress;
        _cancellation = cancellation;
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
        var elapsed = _elapsed.Elapsed;
        var pagesPerSec = page1 / Math.Max(elapsed.TotalSeconds, 0.001);
        var remaining = TimeSpan.FromSeconds((total - page1) / pagesPerSec);
        var eta = remaining.TotalMinutes >= 1
            ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
            : $"{remaining.Seconds}s";
        _progress?.Report(($"Page {page1:N0} of {total:N0}\n{pagesPerSec:N0} pages/sec, ~{eta} remaining",
            pct));

        var brk = _pageBreaks[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            var y = _marginTop;
            var visualLineIdx = 0;
            var lineIdx = brk.FirstLogicalLine;
            var wrapIdx = brk.FirstWrapLine;

            var charsPerRow = Math.Max(1, _safeCharCount);
            while (visualLineIdx < _linesPerPage && lineIdx < _doc.Table.LineCount) {
                var line = _doc.Table.GetLine(lineIdx);

                // Split line into fixed-width rows using monospace arithmetic.
                var rowStart = wrapIdx * charsPerRow;
                while (rowStart < line.Length && visualLineIdx < _linesPerPage) {
                    var rowLen = Math.Min(charsPerRow, line.Length - rowStart);
                    var segment = line.Substring(rowStart, rowLen);
                    var ft = MakeFormattedText(segment);
                    dc.DrawText(ft, new Point(_marginLeft, y));
                    y += _lineHeight;
                    visualLineIdx++;
                    rowStart += charsPerRow;
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

            // Compute line length from the line index tree — no text read needed.
            var lineStart = table.LineStartOfs(i);
            var nextStart = i + 1 < lineCount
                ? table.LineStartOfs(i + 1)
                : _doc.Table.Length;
            var lineLen = (int)(nextStart - lineStart);

            var wrappedCount = lineLen <= charsPerRow
                ? 1
                : (int)Math.Ceiling((double)lineLen / charsPerRow);

            for (var w = 0; w < wrappedCount; w++) {
                visualLinesOnPage++;
                if (visualLinesOnPage > _linesPerPage) {
                    breaks.Add(new PageBreak(i, w));
                    visualLinesOnPage = 1;
                }
            }
        }

        return breaks;
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
