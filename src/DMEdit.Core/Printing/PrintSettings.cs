namespace DMEdit.Core.Printing;

/// <summary>Page orientation for printing.</summary>
public enum PageOrientation { Portrait, Landscape }

/// <summary>
/// Print margins in points (1 pt = 1/72 inch). CSS-style ordering.
/// </summary>
public record struct PrintMargins(double Top, double Right, double Bottom, double Left) {
    /// <summary>One-inch margins on all sides.</summary>
    public static PrintMargins Default => new(72, 72, 72, 72);
}

/// <summary>Inclusive 1-based page range.</summary>
public record struct PageRange(int From, int To);

/// <summary>
/// Describes a paper size. Dimensions are in points (1 pt = 1/72 inch).
/// The <see cref="Id"/> is an opaque platform identifier used to map back
/// to the native print ticket (e.g. <c>PageMediaSizeName</c> on Windows).
/// </summary>
public sealed class PaperSizeInfo {
    /// <summary>Human-readable name (e.g. "Letter", "A4").</summary>
    public required string Name { get; init; }

    /// <summary>Width in points (portrait orientation).</summary>
    public required double Width { get; init; }

    /// <summary>Height in points (portrait orientation).</summary>
    public required double Height { get; init; }

    /// <summary>
    /// Platform-specific identifier. On Windows this is the integer value of
    /// <c>PageMediaSizeName</c>. Null for fallback/built-in sizes.
    /// </summary>
    public int? Id { get; init; }

    /// <summary>
    /// True for paper sizes considered "common" (Letter, A4, Legal, etc.).
    /// Used by the print dialog to toggle between a short and full list.
    /// </summary>
    public bool IsCommon { get; init; }

    public override string ToString() => Name;

    /// <summary>US Letter — 8.5 × 11 in.</summary>
    public static PaperSizeInfo Letter => new() {
        Name = "Letter (8.5 × 11 in)", Width = 612, Height = 792, IsCommon = true,
    };

    /// <summary>ISO A4 — 210 × 297 mm.</summary>
    public static PaperSizeInfo A4 => new() {
        Name = "A4 (210 × 297 mm)", Width = 595, Height = 842, IsCommon = true,
    };

    /// <summary>US Legal — 8.5 × 14 in.</summary>
    public static PaperSizeInfo Legal => new() {
        Name = "Legal (8.5 × 14 in)", Width = 612, Height = 1008, IsCommon = true,
    };

    /// <summary>Fallback list used when no platform print service is available.</summary>
    public static IReadOnlyList<PaperSizeInfo> Defaults => [Letter, A4, Legal];
}

/// <summary>
/// All settings needed to print a document: paper size, orientation, margins, and
/// optional page range. Pure data, no UI dependency.
/// </summary>
public sealed class PrintSettings {
    public PaperSizeInfo Paper { get; set; } = PaperSizeInfo.Letter;
    public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;
    public PrintMargins Margins { get; set; } = PrintMargins.Default;

    /// <summary>
    /// Inclusive page range to print (1-based). Null means all pages.
    /// </summary>
    public PageRange? Range { get; set; }

    /// <summary>
    /// Returns the printable area dimensions in points, accounting for paper size,
    /// orientation, and margins.
    /// </summary>
    public (double Width, double Height) GetPrintableArea() {
        var (w, h) = (Paper.Width, Paper.Height);
        if (Orientation == PageOrientation.Landscape) {
            (w, h) = (h, w);
        }
        return (w - Margins.Left - Margins.Right,
                h - Margins.Top - Margins.Bottom);
    }

    /// <summary>
    /// Returns the full page dimensions in points, accounting for orientation.
    /// </summary>
    public (double Width, double Height) GetPageSize() {
        var (w, h) = (Paper.Width, Paper.Height);
        if (Orientation == PageOrientation.Landscape) {
            (w, h) = (h, w);
        }
        return (w, h);
    }
}
