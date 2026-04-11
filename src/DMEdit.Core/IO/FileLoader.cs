using System.IO.Compression;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;

namespace DMEdit.Core.IO;

/// <summary>
/// Result of loading a file. Contains the <see cref="Document"/>, a display name
/// for the title bar, and whether the source was a zip archive.
/// </summary>
public sealed record LoadResult(Document Document, string DisplayName, bool WasZipped, IBuffer? Buffer = null) {
    /// <summary>
    /// For zip files, the name of the inner entry (e.g., "model.xml").
    /// <c>null</c> for non-zip files.
    /// </summary>
    public string? InnerEntryName { get; init; }

    /// <summary>
    /// SHA-1 hash of the raw content bytes at load time (hex, lowercase).
    /// Used for session persistence to detect external modifications.
    /// Computed during the background scan and becomes available after
    /// <see cref="Loaded"/> completes. For zip files, this is the hash of
    /// the decompressed entry bytes, not the outer archive.
    /// <c>null</c> for untitled documents or while still loading.
    /// </summary>
    public string? BaseSha1 { get; internal set; }

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
    // Shared async-buffer wiring
    // -----------------------------------------------------------------

    /// <summary>
    /// Wraps <paramref name="buf"/> in a <see cref="Document"/>, builds a
    /// <see cref="LoadResult"/> with a <see cref="LoadResult.Loaded"/> task,
    /// and registers the standard <c>LoadComplete</c> handler that:
    /// <list type="bullet">
    ///   <item>Reconciles the piece-table's initial piece against the final
    ///     buffer length (closes the partial-piece race that bites if the
    ///     editor renders before the scan completes).</item>
    ///   <item>Copies detected line-ending / indent / encoding into the doc.</item>
    ///   <item>Stores the SHA-1 in the result.</item>
    ///   <item>Completes the load TCS.</item>
    /// </list>
    /// Both the paged and zipped load paths route through this so the wiring
    /// stays in one place.
    /// </summary>
    private static LoadResult RegisterAsyncBuffer(
        IProgressBuffer buf,
        string displayName,
        bool wasZipped,
        string? innerEntryName,
        CancellationToken ct) {

        var doc = new Document(new PieceTable(buf));
        doc.EncodingInfo = new EncodingInfo(FileEncoding.Unknown);

        var tcs = new TaskCompletionSource();
        var result = new LoadResult(doc, displayName, wasZipped, Buffer: buf) {
            Loaded = tcs.Task,
            InnerEntryName = innerEntryName,
        };

        buf.LoadComplete += () => {
            // Reconcile the partial-piece race FIRST: any subsequent reader
            // (including the metadata copies below, which may indirectly query
            // doc length) sees the full buffer.
            doc.Table.ReconcileInitialPiece();
            doc.LineEndingInfo = buf.DetectedLineEnding;
            doc.IndentInfo = buf.DetectedIndent;
            doc.EncodingInfo = buf.DetectedEncoding;
            result.BaseSha1 = buf.Sha1;
            tcs.TrySetResult();
        };

        buf.StartLoading(ct);
        return result;
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
        return RegisterAsyncBuffer(
            paged,
            displayName: Path.GetFileName(path),
            wasZipped: false,
            innerEntryName: null,
            ct);
    }

    // -----------------------------------------------------------------
    // Zip support
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> if the file starts with the PK zip local file header
    /// magic bytes (<c>50 4B 03 04</c>).
    /// </summary>
    internal static bool IsZipFile(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> magic = stackalloc byte[4];
        var read = fs.Read(magic);
        return read >= 4
            && magic[0] == 0x50 && magic[1] == 0x4B
            && magic[2] == 0x03 && magic[3] == 0x04;
    }

    private static (StreamingFileBuffer buf, string innerName) OpenZipEntry(string path) {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
            var uncompressedLen = entry.Length; // 0 if unknown
            var estimatedLen = uncompressedLen > 0 ? uncompressedLen : fs.Length * 4;
            var decompStream = entry.Open();
            var buf = new StreamingFileBuffer(decompStream, estimatedLen, owner: zip);

            zip = null; // ownership transferred to StreamingFileBuffer
            return (buf, entry.Name);
        } catch {
            zip?.Dispose();
            throw;
        }
    }

    private static LoadResult LoadZip(string path, CancellationToken ct) {
        var (buf, innerName) = OpenZipEntry(path);
        // Tab title shows just the outer zip file name to keep tabs narrow.
        // The inner entry name is exposed via LoadResult.InnerEntryName so
        // the UI can surface it in a tooltip.
        return RegisterAsyncBuffer(
            buf,
            displayName: Path.GetFileName(path),
            wasZipped: true,
            innerEntryName: innerName,
            ct);
    }

}
