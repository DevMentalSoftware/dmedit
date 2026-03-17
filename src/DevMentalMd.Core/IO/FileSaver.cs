using System.Security.Cryptography;
using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;
using EncodingInfo = DevMentalMd.Core.Documents.EncodingInfo;

namespace DevMentalMd.Core.IO;

/// <summary>
/// Saves a <see cref="Document"/> to a file using the document's
/// <see cref="EncodingInfo"/>. Returns the SHA-1 hash (lowercase hex)
/// of the written bytes so the caller can update <c>BaseSha1</c>
/// without a second read pass.
/// </summary>
/// <remarks>
/// <para>
/// Always writes to a temporary file first, then renames to the target.
/// This prevents corruption if the process crashes mid-write and avoids
/// the self-referential save problem where <see cref="PagedFileBuffer"/>
/// reads from the same file being overwritten.
/// </para>
/// <para>
/// <b>Fast path</b> — unedited buffer-backed documents: writes directly
/// from the <see cref="IBuffer"/> in 1 MB chunks, bypassing
/// <see cref="PieceTable"/>.
/// </para>
/// <para>
/// <b>General path</b> — edited or string-backed documents: streams
/// through <see cref="PieceTable.ForEachPiece"/> in 1 MB chunks.
/// </para>
/// </remarks>
public static class FileSaver {
    private const int WriteChunk = 1_048_576; // 1 MB

    /// <summary>
    /// Asynchronously saves <paramref name="doc"/> to <paramref name="path"/>.
    /// Returns the SHA-1 hash (lowercase hex) of the written file.
    /// </summary>
    public static async Task<string> SaveAsync(
            Document doc, string path,
            bool backupOnSave = false,
            CancellationToken ct = default) {
        return await Task.Run(() => Save(doc, path, backupOnSave, ct), ct);
    }

    /// <summary>
    /// Synchronously saves <paramref name="doc"/> to <paramref name="path"/>.
    /// Writes to a temp file then renames to prevent corruption.
    /// Returns the SHA-1 hash (lowercase hex) of the written file.
    /// </summary>
    public static string Save(
            Document doc, string path,
            bool backupOnSave = false,
            CancellationToken ct = default) {
        var table = doc.Table;
        var encInfo = doc.EncodingInfo;
        var tmpPath = path + ".tmp";

        try {
            string sha1;
            if (table.IsOriginalContent && table.Buffer is { LengthIsKnown: true } buf) {
                sha1 = WriteToFile(buf, tmpPath, encInfo, ct);
            } else {
                sha1 = WriteToFile(table, tmpPath, encInfo, ct);
            }

            if (backupOnSave && File.Exists(path)) {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(tmpPath, path, overwrite: true);
            return sha1;
        } catch {
            try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    private static string WriteToFile(IBuffer buf, string path, EncodingInfo encInfo, CancellationToken ct) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        WritePreamble(fs, hasher, encInfo);

        var enc = encInfo.GetDotNetEncoding();
        var encoder = enc.GetEncoder();
        var charBuf = new char[WriteChunk];
        var byteBuf = new byte[enc.GetMaxByteCount(WriteChunk)];
        var len = buf.Length;
        var pos = 0L;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(WriteChunk, len - pos);
            buf.CopyTo(pos, charBuf.AsSpan(0, take), take);
            var flush = pos + take >= len;
            var byteCount = encoder.GetBytes(charBuf.AsSpan(0, take), byteBuf, flush);
            hasher.AppendData(byteBuf, 0, byteCount);
            fs.Write(byteBuf, 0, byteCount);
            pos += take;
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static string WriteToFile(PieceTable table, string path, EncodingInfo encInfo, CancellationToken ct) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        WritePreamble(fs, hasher, encInfo);

        var enc = encInfo.GetDotNetEncoding();
        var encoder = enc.GetEncoder();
        var charBuf = new char[WriteChunk];
        var byteBuf = new byte[enc.GetMaxByteCount(WriteChunk)];
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
            var flush = pos + take >= len;
            var byteCount = encoder.GetBytes(charBuf.AsSpan(0, take), byteBuf, flush);
            hasher.AppendData(byteBuf, 0, byteCount);
            fs.Write(byteBuf, 0, byteCount);
            pos += take;
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static void WritePreamble(FileStream fs, IncrementalHash hasher, EncodingInfo encInfo) {
        var preamble = encInfo.GetPreamble();
        if (preamble is { Length: > 0 }) {
            hasher.AppendData(preamble, 0, preamble.Length);
            fs.Write(preamble, 0, preamble.Length);
        }
    }
}
