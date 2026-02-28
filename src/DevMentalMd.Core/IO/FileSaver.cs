using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Saves a <see cref="Document"/> to a file as UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fast path</b> — unedited buffer-backed documents (the common "open → save" flow):
/// writes directly from the <see cref="IBuffer"/> in 1 MB chunks, bypassing
/// <see cref="PieceTable"/> entirely. This avoids per-chunk allocations and is
/// thread-safe against concurrent reads on the UI thread.
/// </para>
/// <para>
/// <b>General path</b> — edited or string-backed documents: streams through
/// <see cref="PieceTable.ForEachPiece"/> in 1 MB chunks.
/// </para>
/// </remarks>
public static class FileSaver {
    private const int WriteChunk = 1_048_576; // 1 MB

    /// <summary>
    /// Asynchronously saves <paramref name="doc"/> to <paramref name="path"/> as UTF-8.
    /// </summary>
    public static async Task SaveAsync(Document doc, string path, CancellationToken ct = default) {
        await Task.Run(() => Save(doc, path, ct), ct);
    }

    /// <summary>
    /// Synchronously saves <paramref name="doc"/> to <paramref name="path"/> as UTF-8.
    /// </summary>
    public static void Save(Document doc, string path, CancellationToken ct = default) {
        var table = doc.Table;

        // Fast path: unedited buffer-backed document — read straight from the buffer.
        // This is thread-safe (IBuffer reads don't mutate state) and avoids the
        // per-chunk allocation in PieceTable.VisitPieces for WholeBufSentinel pieces.
        if (table.IsOriginalContent && table.OrigBuffer is { LengthIsKnown: true } buf) {
            SaveFromBuffer(buf, path, ct);
            return;
        }

        // General path: stream through the piece-table.
        SaveFromPieceTable(table, path, ct);
    }

    private static void SaveFromBuffer(IBuffer buf, string path, CancellationToken ct) {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8, bufferSize: WriteChunk);
        var charBuf = new char[WriteChunk];
        var len = buf.Length;
        var pos = 0L;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(WriteChunk, len - pos);
            buf.CopyTo(pos, charBuf.AsSpan(0, take), take);
            writer.Write(charBuf, 0, take);
            pos += take;
        }
    }

    private static void SaveFromPieceTable(PieceTable table, string path, CancellationToken ct) {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8, bufferSize: WriteChunk);
        var charBuf = new char[WriteChunk];
        var len = table.Length;
        var pos = 0L;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(WriteChunk, len - pos);
            var filled = 0;
            table.ForEachPiece(pos, take, span => {
                span.CopyTo(charBuf.AsSpan(filled));
                filled += span.Length;
            });
            writer.Write(charBuf, 0, take);
            pos += take;
        }
    }
}
