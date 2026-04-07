using DMEdit.Core.Documents;

namespace DMEdit.Core.Printing;

/// <summary>
/// Outcome of a print job.  Exceptions are captured as strings (not
/// <see cref="Exception"/> objects) so nothing crosses the STA print thread
/// boundary — the caller can safely format and display the message.
/// </summary>
/// <param name="Success">True when the print job completed normally.</param>
/// <param name="Cancelled">True when the user cancelled the job.</param>
/// <param name="ErrorMessage">Short, user-facing summary of the failure (null on success / cancel).</param>
/// <param name="ErrorDetails">Full stack trace / diagnostic detail for dev-mode display.</param>
public readonly record struct PrintResult(
    bool Success,
    bool Cancelled,
    string? ErrorMessage,
    string? ErrorDetails) {

    public static PrintResult Ok() => new(true, false, null, null);
    public static PrintResult CancelledResult() => new(false, true, null, null);
    public static PrintResult Failed(string message, string details) =>
        new(false, false, message, details);
}

/// <summary>
/// Platform-specific print service interface. Implemented by the Windows
/// print DLL and discovered at runtime via reflection.
/// </summary>
public interface ISystemPrintService {
    /// <summary>Returns the printers available on the system.</summary>
    IReadOnlyList<PrinterInfo> GetPrinters();

    /// <summary>
    /// Returns the paper sizes supported by the named printer.
    /// Each entry carries an opaque <see cref="PaperSizeInfo.Id"/> that is
    /// mapped back to the native print ticket when printing.
    /// </summary>
    IReadOnlyList<PaperSizeInfo> GetPaperSizes(string printerName);

    /// <summary>
    /// Prints <paramref name="doc"/> using the settings in <paramref name="ticket"/>.
    /// Called on a background thread; the implementation manages any STA
    /// thread requirements internally.  Returns a <see cref="PrintResult"/>
    /// describing success, cancellation, or failure details.
    /// </summary>
    PrintResult Print(Document doc, PrintJobTicket ticket,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default);
}
