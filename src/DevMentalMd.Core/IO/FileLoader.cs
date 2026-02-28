using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Loads a file into a <see cref="Document"/>.
/// </summary>
/// <remarks>
/// Files ≤ 10 MB are read entirely into memory via <c>File.ReadAllText</c> (UTF-8).
/// Larger files use a <see cref="StreamingFileBuffer"/> that reads in 1 MB binary chunks
/// on a background thread, decoding UTF-8 incrementally. The first chunk is available
/// almost immediately so the UI stays responsive.
/// </remarks>
public static class FileLoader {
    private const long SmallThreshold = 10L * 1024 * 1024; // 10 MB

    /// <summary>
    /// Asynchronously loads the file at <paramref name="path"/> into a new <see cref="Document"/>.
    /// </summary>
    public static async Task<Document> LoadAsync(string path, CancellationToken ct = default) {
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= SmallThreshold) {
            return await Task.Run(() => {
                var text = File.ReadAllText(path, Encoding.UTF8);
                return new Document(text);
            }, ct);
        }
        // Large file: streaming background load.
        var buf = new StreamingFileBuffer(path, byteLen);
        buf.StartLoading(ct);
        return new Document(new PieceTable(buf));
    }

    /// <summary>
    /// Synchronously loads the file at <paramref name="path"/> into a new <see cref="Document"/>.
    /// </summary>
    public static Document Load(string path) {
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= SmallThreshold) {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return new Document(text);
        }
        // Large file: streaming background load.
        var buf = new StreamingFileBuffer(path, byteLen);
        buf.StartLoading(CancellationToken.None);
        return new Document(new PieceTable(buf));
    }
}
