using DMEdit.Core.Documents;

namespace DMEdit.Core.Printing;

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
    /// thread requirements internally.
    /// </summary>
    /// <returns>True if printing completed, false if cancelled or failed.</returns>
    bool Print(Document doc, PrintJobTicket ticket,
        IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken cancellation = default);
}
