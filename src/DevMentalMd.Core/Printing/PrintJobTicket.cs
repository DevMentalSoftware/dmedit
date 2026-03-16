namespace DevMentalMd.Core.Printing;

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
}
