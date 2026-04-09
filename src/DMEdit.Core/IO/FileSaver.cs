using System.Security.Cryptography;
using System.Text;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using EncodingInfo = DMEdit.Core.Documents.EncodingInfo;

namespace DMEdit.Core.IO;

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
    private const int WriteChunk = 1024 * 1024;

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
            var byteCount = EncodeChunk(encoder, nlBuf, nlLen, byteBuf, flush, path, enc, pos);
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
            var byteCount = EncodeChunk(encoder, nlBuf, nlLen, byteBuf, flush, path, enc, pos);
            hasher.AppendData(byteBuf, 0, byteCount);
            fs.Write(byteBuf, 0, byteCount);
            pos += take;
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>
    /// Encodes a normalized char chunk into bytes, wrapping any
    /// <see cref="EncoderFallbackException"/> with file path, encoding name,
    /// approximate document position, and the offending character.  Without
    /// this wrap the user sees a raw fallback exception that just says
    /// "Unable to translate Unicode character U+XXXX" — no indication which
    /// file, which encoding, or roughly where in the document the failure
    /// occurred.  Inner exception is preserved for debugging.
    ///
    /// <para>Note: with the encodings the editor currently exposes
    /// (<see cref="EncodingInfo.GetDotNetEncoding"/>), the fallback never
    /// fires — UTF-8/16 encode every Unicode codepoint, and ASCII /
    /// Windows-1252 use replacement fallback by default.  This wrap is
    /// defense for any future strict-fallback encoding (e.g. an opt-in
    /// "fail on data loss" mode) and is testable via direct invocation
    /// with a custom Encoding configured for exception fallback.</para>
    /// </summary>
    internal static int EncodeChunk(
            Encoder encoder, char[] nlBuf, int nlLen, byte[] byteBuf, bool flush,
            string path, Encoding enc, long chunkStart) {
        try {
            return encoder.GetBytes(nlBuf.AsSpan(0, nlLen), byteBuf, flush);
        } catch (EncoderFallbackException ex) {
            var ch = ex.CharUnknown != '\0'
                ? $"U+{(int)ex.CharUnknown:X4}"
                : ex.CharUnknownHigh != '\0'
                    ? $"U+{char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow):X6}"
                    : "(unknown)";
            // Index is the offset within nlBuf where the failure occurred.
            // Add chunkStart for an approximate document position — exact only
            // when no line-ending normalization shifted the offsets.
            var approxPos = chunkStart + Math.Max(0, ex.Index);
            throw new IOException(
                $"Cannot save '{path}' as encoding '{enc.WebName}': " +
                $"character {ch} near offset {approxPos} cannot be represented. " +
                $"Try a Unicode encoding (UTF-8, UTF-16) instead.", ex);
        }
    }

    /// <summary>
    /// Copies <paramref name="src"/>[0..<paramref name="srcLen"/>] into
    /// <paramref name="dst"/>, replacing every line ending sequence
    /// (\r\n, \n, \r) with <paramref name="nl"/>. Returns the number
    /// of chars written to <paramref name="dst"/>.
    /// <paramref name="prevCr"/> carries \r state across chunk boundaries.
    /// </summary>
    /// <remarks>
    /// Note on duplication: this contains a CR/LF/CRLF state machine that
    /// parallels <see cref="Documents.LineScanner"/>.  The difference is
    /// that LineScanner emits line-length metadata while this function
    /// emits a transformed character stream — there is no productive way
    /// to delegate the one to the other.  If the state machine ever
    /// changes, update both sites; the canonical behaviour for metadata
    /// lives in <see cref="Documents.LineScanner"/>.
    /// </remarks>
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
