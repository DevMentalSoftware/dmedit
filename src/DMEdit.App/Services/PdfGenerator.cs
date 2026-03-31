using System.IO;
using DMEdit.Core.Documents;
using DMEdit.Core.Printing;
using SkiaSharp;

namespace DMEdit.App.Services;

/// <summary>
/// Generates a PDF from a plain-text <see cref="Document"/> using SkiaSharp's
/// <c>SKDocument</c> streaming API. Pages are rendered one at a time — the
/// entire document is never held in memory as layout objects.
/// </summary>
public static class PdfGenerator {

    private const string DefaultFontFamily = "Cascadia Code, Consolas, Courier New";
    private const float DefaultFontSize = 11f;

    /// <summary>
    /// Renders the document to a PDF file at <paramref name="outputPath"/>.
    /// </summary>
    public static void RenderToPdf(Document doc, PrintSettings settings, string outputPath) {
        var (pageW, pageH) = settings.GetPageSize();
        var (printW, printH) = settings.GetPrintableArea();

        using var paint = new SKPaint {
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = DefaultFontSize,
            Typeface = ResolveTypeface(),
        };

        var lineHeight = GetLineHeight(paint);
        var linesPerPage = Math.Max(1, (int)(printH / lineHeight));
        var pageBreaks = ComputePageBreaks(doc, paint, (float)printW, linesPerPage);

        var pageCount = pageBreaks.Count;
        var from = settings.Range.HasValue ? Math.Max(0, settings.Range.Value.From - 1) : 0;
        var to = settings.Range.HasValue ? Math.Min(pageCount, settings.Range.Value.To) : pageCount;

        using var stream = new SKFileWStream(outputPath);
        using var pdfDoc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata {
            Creation = DateTime.Now,
            Producer = "DevMental Edit",
        });

        for (var p = from; p < to; p++) {
            using var canvas = pdfDoc.BeginPage((float)pageW, (float)pageH);
            RenderPage(canvas, doc, pageBreaks[p], linesPerPage, paint,
                (float)settings.Margins.Left, (float)settings.Margins.Top,
                (float)printW, lineHeight);
            pdfDoc.EndPage();
        }

        pdfDoc.Close();
    }

    // -----------------------------------------------------------------
    // Page rendering
    // -----------------------------------------------------------------

    private static void RenderPage(
        SKCanvas canvas, Document doc, PageBreak brk,
        int linesPerPage, SKPaint paint,
        float marginLeft, float marginTop,
        float printWidth, float lineHeight) {

        var baseline = -paint.FontMetrics.Ascent;
        var y = marginTop + baseline;
        var visualLine = 0;
        var lineIdx = brk.FirstLogicalLine;
        var wrapIdx = brk.FirstWrapLine;

        while (visualLine < linesPerPage && lineIdx < doc.Table.LineCount) {
            var line = doc.Table.GetLine(lineIdx);
            var wrappedLines = WrapLine(line, paint, printWidth);

            for (var w = wrapIdx; w < wrappedLines.Count && visualLine < linesPerPage; w++) {
                canvas.DrawText(wrappedLines[w], marginLeft, y, paint);
                y += lineHeight;
                visualLine++;
            }
            wrapIdx = 0;
            lineIdx++;
        }
    }

    // -----------------------------------------------------------------
    // Pagination
    // -----------------------------------------------------------------

    private static List<PageBreak> ComputePageBreaks(
        Document doc, SKPaint paint, float printWidth, int linesPerPage) {

        var breaks = new List<PageBreak> { new(0, 0) };
        var lineCount = doc.Table.LineCount;
        var visualLinesOnPage = 0;

        for (var i = 0L; i < lineCount; i++) {
            var line = doc.Table.GetLine(i);
            var wrappedCount = CountWrappedLines(line, paint, printWidth);

            for (var w = 0; w < wrappedCount; w++) {
                visualLinesOnPage++;
                if (visualLinesOnPage > linesPerPage) {
                    breaks.Add(new PageBreak(i, w));
                    visualLinesOnPage = 1;
                }
            }
        }

        return breaks;
    }

    // -----------------------------------------------------------------
    // Word wrapping
    // -----------------------------------------------------------------

    private static int CountWrappedLines(string line, SKPaint paint, float maxWidth) {
        if (string.IsNullOrEmpty(line)) {
            return 1;
        }
        if (paint.MeasureText(line) <= maxWidth) {
            return 1;
        }
        var count = 0;
        var remaining = line.AsSpan();
        while (remaining.Length > 0) {
            var fit = BreakAtWord(remaining, paint, maxWidth);
            remaining = remaining[fit..];
            count++;
        }
        return Math.Max(1, count);
    }

    private static List<string> WrapLine(string line, SKPaint paint, float maxWidth) {
        if (string.IsNullOrEmpty(line)) {
            return [""];
        }
        if (paint.MeasureText(line) <= maxWidth) {
            return [line];
        }
        var result = new List<string>();
        var remaining = line.AsSpan();
        while (remaining.Length > 0) {
            var fit = BreakAtWord(remaining, paint, maxWidth);
            result.Add(remaining[..fit].ToString());
            remaining = remaining[fit..];
        }
        return result;
    }

    /// <summary>
    /// Returns the number of characters from <paramref name="text"/> that fit
    /// within <paramref name="maxWidth"/>, breaking at a word boundary when possible.
    /// </summary>
    private static int BreakAtWord(ReadOnlySpan<char> text, SKPaint paint, float maxWidth) {
        var fit = (int)paint.BreakText(text, maxWidth);
        if (fit <= 0) {
            return 1; // at least one character to avoid infinite loop
        }
        if (fit >= text.Length) {
            return text.Length;
        }
        // Try to break at a word boundary.
        var wordBreak = fit;
        while (wordBreak > 0 && !char.IsWhiteSpace(text[wordBreak - 1])) {
            wordBreak--;
        }
        return wordBreak > 0 ? wordBreak : fit;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static float GetLineHeight(SKPaint paint) {
        var m = paint.FontMetrics;
        return m.Descent - m.Ascent + m.Leading;
    }

    private static SKTypeface ResolveTypeface() {
        foreach (var name in DefaultFontFamily.Split(',', StringSplitOptions.TrimEntries)) {
            var tf = SKTypeface.FromFamilyName(name);
            if (tf is not null && tf.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                return tf;
            }
        }
        return SKTypeface.Default;
    }

    private record struct PageBreak(long FirstLogicalLine, int FirstWrapLine);
}
