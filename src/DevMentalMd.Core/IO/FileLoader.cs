using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Loads a file into a <see cref="Document"/>.
/// </summary>
/// <remarks>
/// Files ≤ 50 MB are read entirely into memory via <c>File.ReadAllText</c> (UTF-8 aware).
/// Larger files are opened as a memory-mapped <see cref="LazyFileBuffer"/> (UTF-16 LE only —
/// see <see cref="LazyFileBuffer"/> for the encoding limitation).
/// </remarks>
public static class FileLoader {
    private const long SmallThreshold = 50L * 1024 * 1024; // 50 MB

    /// <summary>
    /// Asynchronously loads the file at <paramref name="path"/> into a new <see cref="Document"/>.
    /// </summary>
    public static async Task<Document> LoadAsync(string path, CancellationToken ct = default) {
        return await Task.Run(() => Load(path), ct);
    }

    /// <summary>
    /// Synchronously loads the file at <paramref name="path"/> into a new <see cref="Document"/>.
    /// </summary>
    public static Document Load(string path) {
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= SmallThreshold) {
            // Small file: decode the entire file (handles UTF-8, UTF-16, etc.).
            var text = File.ReadAllText(path, Encoding.UTF8);
            return new Document(text);
        }
        // Large file: memory-mapped (UTF-16 LE only; see LazyFileBuffer remarks).
        var buf = LazyFileBuffer.Open(path);
        return new Document(new PieceTable(buf));
    }
}
