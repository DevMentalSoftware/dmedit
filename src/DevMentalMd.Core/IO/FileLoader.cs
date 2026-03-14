using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Result of loading a file. Contains the <see cref="Document"/>, a display name
/// for the title bar, and whether the source was a zip archive.
/// </summary>
public sealed record LoadResult(Document Document, string DisplayName, bool WasZipped) {
    /// <summary>
    /// For zip files, the name of the inner entry (e.g., "model.xml").
    /// <c>null</c> for non-zip files.
    /// </summary>
    public string? InnerEntryName { get; init; }

    /// <summary>
    /// SHA-1 hash of the raw file bytes at load time (hex, lowercase).
    /// Used for session persistence to detect external modifications.
    /// For non-zip files this is computed during the background scan and
    /// becomes available after <see cref="Loaded"/> completes; for zip files
    /// it is computed up front (the outer archive is typically small).
    /// <c>null</c> for untitled documents or while still loading.
    /// </summary>
    public string? BaseSha1 { get; set; }

    /// <summary>
    /// Completes when the background scan finishes (line endings, SHA-1,
    /// full line index). Already completed for synchronously loaded files.
    /// </summary>
    public Task Loaded { get; init; } = Task.CompletedTask;
}

/// <summary>
/// Loads a file into a <see cref="Document"/>.
/// </summary>
/// <remarks>
/// <para>
/// Zip files are auto-detected by magic bytes (PK header). A single-entry zip is
/// transparently decompressed and streamed into a <see cref="StreamingFileBuffer"/>.
/// Multi-entry zips are rejected with an <see cref="IOException"/>.
/// </para>
/// <para>
/// Non-zip files always use a <see cref="PagedFileBuffer"/> that reads in 1 MB
/// chunks on a background thread, building the line index, line-ending counters,
/// and SHA-1 hash in a single pass. Only a bounded number of decoded pages are
/// kept in memory (~16 MB), with the rest re-read from disk on demand.
/// </para>
/// </remarks>
public static class FileLoader {
    /// <summary>
    /// Asynchronously loads the file at <paramref name="path"/> into a new <see cref="LoadResult"/>.
    /// SHA-1 and line-ending detection happen during the background scan; the
    /// <see cref="LoadResult.BaseSha1"/> and <see cref="Document.LineEndingInfo"/>
    /// are populated when loading completes.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<LoadResult> LoadAsync(string path, CancellationToken ct = default) {
        if (IsZipFile(path)) {
            return Task.FromResult(LoadZip(path, ct));
        }
        return Task.FromResult(LoadPagedAsync(path, ct));
    }

    // -----------------------------------------------------------------
    // Paged loading (single pass: decode + line index + SHA-1)
    // -----------------------------------------------------------------

    /// <summary>
    /// Starts the paged buffer and returns immediately. The returned
    /// <see cref="LoadResult.Loaded"/> task completes when the background
    /// scan finishes and SHA-1 / line-ending info have been populated.
    /// </summary>
    private static LoadResult LoadPagedAsync(string path, CancellationToken ct) {
        var byteLen = new FileInfo(path).Length;
        var paged = new PagedFileBuffer(path, byteLen);

        var doc = new Document(new PieceTable(paged));
        var tcs = new TaskCompletionSource();

        var result = new LoadResult(doc, Path.GetFileName(path), WasZipped: false) {
            Loaded = tcs.Task
        };

        paged.LoadComplete += () => {
            doc.LineEndingInfo = paged.DetectedLineEnding;
            result.BaseSha1 = paged.Sha1;
            tcs.TrySetResult();
        };

        paged.StartLoading(ct);
        return result;
    }

    // -----------------------------------------------------------------
    // SHA-1 hashing (still needed for zip outer file)
    // -----------------------------------------------------------------

    /// <summary>
    /// Computes the SHA-1 hash of a file on disk. Returns lowercase hex.
    /// Used by session restore to verify base-file identity.
    /// </summary>
    public static string ComputeSha1File(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA1.HashData(fs);
        return Convert.ToHexStringLower(hash);
    }

    // -----------------------------------------------------------------
    // Zip support
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> if the file starts with the PK zip local file header
    /// magic bytes (<c>50 4B 03 04</c>).
    /// </summary>
    internal static bool IsZipFile(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        var read = fs.Read(magic);
        return read >= 4
            && magic[0] == 0x50 && magic[1] == 0x4B
            && magic[2] == 0x03 && magic[3] == 0x04;
    }

    private static (StreamingFileBuffer buf, Document doc, LoadResult result) OpenZipEntry(
        string path, string sha1) {

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive? zip = null;
        try {
            zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count == 0) {
                throw new IOException($"Zip archive is empty: {path}");
            }
            if (zip.Entries.Count > 1) {
                throw new IOException(
                    $"Zip archive contains {zip.Entries.Count} entries; " +
                    $"only single-entry zips are supported: {path}");
            }

            var entry = zip.Entries[0];
            var displayName = $"{Path.GetFileName(path)} \u2192 {entry.Name}";

            var uncompressedLen = entry.Length; // 0 if unknown
            var estimatedLen = uncompressedLen > 0 ? uncompressedLen : fs.Length * 4;
            var decompStream = entry.Open();
            var buf = new StreamingFileBuffer(decompStream, estimatedLen, owner: zip);

            var doc = new Document(new PieceTable(buf));
            var result = new LoadResult(doc, displayName, WasZipped: true) {
                InnerEntryName = entry.Name,
                BaseSha1 = sha1
            };

            zip = null; // ownership transferred to StreamingFileBuffer
            return (buf, doc, result);
        } catch {
            zip?.Dispose();
            throw;
        }
    }

    private static LoadResult LoadZip(string path, CancellationToken ct) {
        var sha1 = ComputeSha1File(path);
        var (buf, doc, result) = OpenZipEntry(path, sha1);

        var tcs = new TaskCompletionSource();
        result = result with { Loaded = tcs.Task };

        buf.LoadComplete += () => {
            doc.LineEndingInfo = buf.DetectedLineEnding;
            tcs.TrySetResult();
        };

        buf.StartLoading(ct);
        return result;
    }

}
