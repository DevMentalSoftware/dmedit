namespace DMEdit.Core.Printing;

/// <summary>
/// Portable print job description. Built by the Avalonia print dialog and
/// handed to the platform-specific print service for execution.
/// </summary>
public sealed class PrintJobTicket {
    /// <summary>Full name of the target printer (as returned by <see cref="PrinterInfo.Name"/>).</summary>
    public required string PrinterName { get; init; }

    /// <summary>Page layout settings (paper, orientation, margins).</summary>
    public required PrintSettings Settings { get; init; }

    /// <summary>Number of copies to print.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>
    /// Font family name to render the document with.  Null = let the print
    /// service pick a default monospace family.  Should match the family
    /// the editor displays so the printout looks like the editor view.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// Font size in typographic points (1/72 inch).  Null = service default.
    /// The platform print path is responsible for converting to its native
    /// unit (e.g. 1/96 inch DIPs in WPF).
    /// </summary>
    public double? FontSizePoints { get; set; }

    /// <summary>
    /// Editor indent width in columns (e.g. 4 for "4 spaces per indent").
    /// Used to compute the hanging indent applied to wrapped continuation
    /// rows: continuation rows are offset right by half of one indent.
    /// </summary>
    public int IndentWidth { get; set; } = 4;

    /// <summary>
    /// Hidden diagnostic toggle: when true (default), the WPF print path
    /// draws each monospace row via <c>GlyphRun</c> for performance.  When
    /// false, it falls back to the legacy <c>FormattedText</c> path — useful
    /// only for troubleshooting visual regressions or bisecting a print bug.
    /// Surfaced via <see cref="AppSettings.UseGlyphRunPrinting"/>.
    /// </summary>
    public bool UseGlyphRun { get; set; } = true;
}
