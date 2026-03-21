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
/// <para>
/// Line ending normalization is applied during the write — the document
/// content in memory may contain mixed endings, but the output file will
/// use the style specified by <see cref="Document.LineEndingInfo"/>.
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
        var nl = doc.LineEndingInfo.NewlineString;
        var tmpPath = path + ".tmp";

        try {
            string sha1;
            if (table.IsOriginalContent && table.Buffer is { LengthIsKnown: true } buf) {
                sha1 = WriteToFile(buf, tmpPath, encInfo, nl, ct);
            } else {
                sha1 = WriteToFile(table, tmpPath, encInfo, nl, ct);
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

    /// <summary>
    /// Allocates the normalization output buffer. CRLF can expand output
    /// (every \n becomes \r\n), so we need 2x. LF/CR can only shrink or
    /// stay the same size, so WriteChunk suffices.
    /// </summary>
    private static int NlBufSize(string nl) =>
        nl.Length > 1 ? WriteChunk * 2 : WriteChunk;

    private static string WriteToFile(IBuffer buf, string path,
            EncodingInfo encInfo, string nl, CancellationToken ct) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        WritePreamble(fs, hasher, encInfo);

        var enc = encInfo.GetDotNetEncoding();
        var encoder = enc.GetEncoder();
        var charBuf = new char[WriteChunk];
        var nlSize = NlBufSize(nl);
        var nlBuf = new char[nlSize];
        var byteBuf = new byte[enc.GetMaxByteCount(nlSize)];
        var len = buf.Length;
        var pos = 0L;
        var prevCr = false;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(WriteChunk, len - pos);
            buf.CopyTo(pos, charBuf.AsSpan(0, take), take);
            var flush = pos + take >= len;
            var nlLen = NormalizeLineEndings(charBuf, take, nlBuf, nl, ref prevCr);
            var byteCount = encoder.GetBytes(nlBuf.AsSpan(0, nlLen), byteBuf, flush);
            hasher.AppendData(byteBuf, 0, byteCount);
            fs.Write(byteBuf, 0, byteCount);
            pos += take;
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static string WriteToFile(PieceTable table, string path,
            EncodingInfo encInfo, string nl, CancellationToken ct) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        WritePreamble(fs, hasher, encInfo);

        var enc = encInfo.GetDotNetEncoding();
        var encoder = enc.GetEncoder();
        var charBuf = new char[WriteChunk];
        var nlSize = NlBufSize(nl);
        var nlBuf = new char[nlSize];
        var byteBuf = new byte[enc.GetMaxByteCount(nlSize)];
        var len = table.Length;
        var pos = 0L;
        var prevCr = false;

        while (pos < len) {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(WriteChunk, len - pos);
            var filled = 0;
            table.ForEachPiece(pos, take, span => {
                span.CopyTo(charBuf.AsSpan(filled));
                filled += span.Length;
            });
            var flush = pos + take >= len;
            var nlLen = NormalizeLineEndings(charBuf, filled, nlBuf, nl, ref prevCr);
            var byteCount = encoder.GetBytes(nlBuf.AsSpan(0, nlLen), byteBuf, flush);
            hasher.AppendData(byteBuf, 0, byteCount);
            fs.Write(byteBuf, 0, byteCount);
            pos += take;
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>
    /// Copies <paramref name="src"/>[0..<paramref name="srcLen"/>] into
    /// <paramref name="dst"/>, replacing every line ending sequence
    /// (\r\n, \n, \r) with <paramref name="nl"/>. Returns the number
    /// of chars written to <paramref name="dst"/>.
    /// <paramref name="prevCr"/> carries \r state across chunk boundaries.
    /// </summary>
    private static int NormalizeLineEndings(
            char[] src, int srcLen, char[] dst, string nl, ref bool prevCr) {
        var w = 0;
        for (var i = 0; i < srcLen; i++) {
            var ch = src[i];
            if (prevCr) {
                prevCr = false;
                if (ch == '\n') {
                    // \r\n pair — nl was already emitted at the \r
                    continue;
                }
            }
            if (ch == '\r') {
                prevCr = true;
                foreach (var c in nl) dst[w++] = c;
            } else if (ch == '\n') {
                foreach (var c in nl) dst[w++] = c;
            } else {
                dst[w++] = ch;
            }
        }
        return w;
    }

    private static void WritePreamble(FileStream fs, IncrementalHash hasher, EncodingInfo encInfo) {
        var preamble = encInfo.GetPreamble();
        if (preamble is { Length: > 0 }) {
            hasher.AppendData(preamble, 0, preamble.Length);
            fs.Write(preamble, 0, preamble.Length);
        }
    }
}
