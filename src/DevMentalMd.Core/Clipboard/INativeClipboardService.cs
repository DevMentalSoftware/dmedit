using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Clipboard;

/// <summary>
/// Platform-specific clipboard service that avoids managed string allocation
/// by streaming text directly between the piece table and native clipboard memory.
/// </summary>
public interface INativeClipboardService {
    /// <summary>
    /// Copies [<paramref name="start"/>, <paramref name="start"/>+<paramref name="len"/>)
    /// from <paramref name="table"/> directly to the platform clipboard without
    /// allocating a managed string. Returns false if the operation failed.
    /// </summary>
    bool Copy(PieceTable table, long start, long len);

    /// <summary>
    /// Pastes clipboard text by streaming chunks directly into
    /// <paramref name="table"/>'s add buffer via
    /// <see cref="PieceTable.AppendToAddBuffer"/>. Returns the total number of
    /// characters pasted, or -1 if the clipboard contains no text.
    /// </summary>
    long Paste(PieceTable table) => Paste(table, null, default);

    /// <summary>
    /// Pastes with progress reporting and cancellation support.
    /// <paramref name="progress"/> receives (chars pasted so far, total chars).
    /// </summary>
    long Paste(PieceTable table, Action<long, long>? progress, CancellationToken cancel);

    /// <summary>
    /// Returns the number of characters on the clipboard, or -1 if no text.
    /// Does not consume the clipboard data.
    /// </summary>
    long GetClipboardCharCount() => -1;

    /// <summary>
    /// Pastes clipboard text by encoding to UTF-8 and writing to
    /// <paramref name="stream"/>. Returns total characters written,
    /// or -1 if the clipboard contains no text.
    /// </summary>
    long PasteToStream(Stream stream, Action<long, long>? progress, CancellationToken cancel) =>
        throw new NotSupportedException();
}
