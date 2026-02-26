using System.Text;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Saves a <see cref="Document"/> to a file without materialising the full document
/// content as a single string.
/// </summary>
public static class FileSaver {
    private const int ChunkSize = 65536; // 64 KB

    /// <summary>
    /// Asynchronously saves <paramref name="doc"/> to <paramref name="path"/> as UTF-8.
    /// </summary>
    public static async Task SaveAsync(Document doc, string path, CancellationToken ct = default) {
        await Task.Run(() => Save(doc, path, ct), ct);
    }

    /// <summary>
    /// Synchronously saves <paramref name="doc"/> to <paramref name="path"/> as UTF-8.
    /// Streams content through the piece-table in 64 KB chunks.
    /// </summary>
    public static void Save(Document doc, string path, CancellationToken ct = default) {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        var buf = new char[ChunkSize];
        var table = doc.Table;
        var len = table.Length;
        var pos = 0L;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(ChunkSize, len - pos);
            var filled = 0;
            table.ForEachPiece(pos, take, span => {
                span.CopyTo(buf.AsSpan(filled));
                filled += span.Length;
            });
            writer.Write(buf, 0, take);
            pos += take;
        }
    }
}
