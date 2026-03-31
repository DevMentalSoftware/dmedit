using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DMEdit.Core.Documents;
using DMEdit.Core.Printing;
using FlowDirection = System.Windows.FlowDirection;
using PageOrientation = DMEdit.Core.Printing.PageOrientation;

namespace DMEdit.Print.Windows;

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

    public void Print(Document doc, PrintJobTicket ticket) {
        Exception? error = null;

        var thread = new Thread(() => {
            try {
                PrintOnWpfThread(doc, ticket);
            } catch (Exception ex) {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null) {
            throw new InvalidOperationException("Print failed.", error);
        }
    }

    private static void PrintOnWpfThread(Document doc, PrintJobTicket ticket) {
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
            doc, wpfPageW, wpfPageH, wpfMarginT, wpfMarginR, wpfMarginB, wpfMarginL);

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
    private readonly List<PageBreak> _pageBreaks;

    public PlainTextPaginator(
        Document doc,
        double pageWidth, double pageHeight,
        double marginTop, double marginRight, double marginBottom, double marginLeft) {
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
        _pageBreaks = ComputePageBreaks();
    }

    public override DocumentPage GetPage(int pageNumber) {
        if (pageNumber < 0 || pageNumber >= _pageBreaks.Count) {
            return DocumentPage.Missing;
        }

        var brk = _pageBreaks[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            var y = _marginTop;
            var visualLineIdx = 0;
            var lineIdx = brk.FirstLogicalLine;
            var wrapIdx = brk.FirstWrapLine;

            while (visualLineIdx < _linesPerPage && lineIdx < _doc.Table.LineCount) {
                var line = _doc.Table.GetLine(lineIdx);
                var wrappedLines = WrapLine(line);

                for (var w = wrapIdx; w < wrappedLines.Count && visualLineIdx < _linesPerPage; w++) {
                    var ft = MakeFormattedText(wrappedLines[w]);
                    ft.MaxTextWidth = _printableWidth;
                    dc.DrawText(ft, new Point(_marginLeft, y));
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
        var lineCount = _doc.Table.LineCount;
        var visualLinesOnPage = 0;

        for (var i = 0L; i < lineCount; i++) {
            var line = _doc.Table.GetLine(i);
            var wrappedCount = CountWrappedLines(line);

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

    private int CountWrappedLines(string line) {
        if (string.IsNullOrEmpty(line)) {
            return 1;
        }
        var ft = MakeFormattedText(line);
        ft.MaxTextWidth = _printableWidth;
        return Math.Max(1, (int)Math.Ceiling(ft.Height / _lineHeight));
    }

    private List<string> WrapLine(string line) {
        if (string.IsNullOrEmpty(line)) {
            return [""];
        }
        var ft = MakeFormattedText(line);
        ft.MaxTextWidth = _printableWidth;
        var totalVisual = Math.Max(1, (int)Math.Ceiling(ft.Height / _lineHeight));
        if (totalVisual <= 1) {
            return [line];
        }

        var result = new List<string>();
        var remaining = line.AsSpan();
        while (remaining.Length > 0 && result.Count < totalVisual) {
            var breakPos = FindBreakPosition(remaining);
            if (breakPos <= 0) {
                breakPos = 1;
            }
            result.Add(remaining[..breakPos].ToString());
            remaining = remaining[breakPos..];
        }
        if (remaining.Length > 0) {
            result.Add(remaining.ToString());
        }
        return result;
    }

    private int FindBreakPosition(ReadOnlySpan<char> text) {
        var lo = 1;
        var hi = text.Length;
        var best = text.Length;

        while (lo <= hi) {
            var mid = (lo + hi) / 2;
            var ft = MakeFormattedText(text[..mid].ToString());
            if (ft.Width <= _printableWidth) {
                best = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }

        if (best < text.Length) {
            var wordBreak = best;
            while (wordBreak > 0 && !char.IsWhiteSpace(text[wordBreak - 1])) {
                wordBreak--;
            }
            if (wordBreak > 0) {
                best = wordBreak;
            }
        }

        return best;
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
