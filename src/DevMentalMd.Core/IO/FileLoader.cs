using System.IO.Compression;
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
/// Non-zip files ≤ 10 MB are read entirely into memory via <c>File.ReadAllText</c> (UTF-8).
/// Files between 10 MB and <c>pagedThreshold</c> use a <see cref="StreamingFileBuffer"/>
/// (reads in 1 MB binary chunks on a background thread, ~2× file size in memory).
/// Files larger than <c>pagedThreshold</c> use a <see cref="PagedFileBuffer"/> that keeps
/// only a bounded number of decoded pages in memory (~16 MB), re-reading from disk on demand.
/// </para>
/// </remarks>
public static class FileLoader {
    private const long SmallThreshold = 10L * 1024 * 1024; // 10 MB

    /// <summary>
    /// Default paged-buffer threshold: 50 MB. Files above this size use
    /// <see cref="PagedFileBuffer"/> (bounded ~16 MB memory) instead of
    /// <see cref="StreamingFileBuffer"/> (~2× file size in memory).
    /// </summary>
    public const long DefaultPagedThreshold = 50L * 1024 * 1024;

    /// <summary>
    /// Asynchronously loads the file at <paramref name="path"/> into a new <see cref="LoadResult"/>.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="pagedThreshold">
    /// Files larger than this (in bytes) use the paged buffer.
    /// Pass <c>-1</c> to disable paged loading. Default: 50 MB.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<LoadResult> LoadAsync(string path, CancellationToken ct = default) {

        if (IsZipFile(path)) {
            return LoadZip(path, ct);
        }
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= SmallThreshold) {
            var doc = await Task.Run(() => {
                var text = File.ReadAllText(path, Encoding.UTF8);
                return new Document(text);
            }, ct);
            return new LoadResult(doc, Path.GetFileName(path), WasZipped: false);
        }
        var paged = new PagedFileBuffer(path, byteLen);
        paged.StartLoading(ct);
        return new LoadResult(
            new Document(new PieceTable(paged)),
            Path.GetFileName(path),
            WasZipped: false);
    }

    /// <summary>
    /// Synchronously loads the file at <paramref name="path"/> into a new <see cref="LoadResult"/>.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="pagedThreshold">
    /// Files larger than this (in bytes) use the paged buffer.
    /// Pass <c>-1</c> to disable paged loading. Default: 50 MB.
    /// </param>
    public static LoadResult Load(string path, long pagedThreshold = DefaultPagedThreshold) {
        if (IsZipFile(path)) {
            return LoadZip(path, CancellationToken.None);
        }
        var byteLen = new FileInfo(path).Length;
        if (byteLen <= SmallThreshold) {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return new LoadResult(new Document(text), Path.GetFileName(path), WasZipped: false);
        }
        // Very large file: paged buffer (bounded ~16 MB memory).
        if (pagedThreshold > 0 && byteLen > pagedThreshold) {
            var paged = new PagedFileBuffer(path, byteLen);
            paged.StartLoading(CancellationToken.None);
            return new LoadResult(
                new Document(new PieceTable(paged)),
                Path.GetFileName(path),
                WasZipped: false);
        }
        // Large file: streaming background load (~2× file size in memory).
        var buf = new StreamingFileBuffer(path, byteLen);
        buf.StartLoading(CancellationToken.None);
        return new LoadResult(
            new Document(new PieceTable(buf)),
            Path.GetFileName(path),
            WasZipped: false);
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

    private static LoadResult LoadZip(string path, CancellationToken ct) {
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

            if (uncompressedLen > 0 && uncompressedLen <= SmallThreshold) {
                // Small entry: read entirely into a string.
                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                zip.Dispose(); // done with the archive
                return new LoadResult(new Document(text), displayName, WasZipped: true) {
                    InnerEntryName = entry.Name
                };
            }

            // Large entry (or unknown size): stream through StreamingFileBuffer.
            // The StreamingFileBuffer takes ownership of the zip+stream and disposes them
            // when loading completes or when the buffer is disposed.
            var estimatedLen = uncompressedLen > 0 ? uncompressedLen : fs.Length * 4; // heuristic
            var decompStream = entry.Open();
            var buf = new StreamingFileBuffer(decompStream, estimatedLen, owner: zip);
            buf.StartLoading(ct);
            zip = null; // ownership transferred to StreamingFileBuffer
            return new LoadResult(
                new Document(new PieceTable(buf)),
                displayName,
                WasZipped: true) {
                InnerEntryName = entry.Name
            };
        } catch {
            zip?.Dispose();
            throw;
        }
    }
}
