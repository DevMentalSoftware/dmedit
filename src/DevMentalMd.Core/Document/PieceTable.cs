using System.Text;
using DevMentalMd.Core.Buffers;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// Core document storage using the piece-table data structure.
/// Supports O(1) amortized inserts and deletes without copying large buffers.
/// Not thread-safe; all access must be from a single thread.
/// </summary>
public sealed class PieceTable {
    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly string _orig;
    private readonly IBuffer? _origBuf;  // non-null when constructed from IBuffer
    private string? _origBufCache;       // lazy materialized copy of _origBuf (string path only)

    private readonly StringBuilder _addBuf = new();
    private readonly List<Piece> _pieces = new();

    // Sorted list of logical character offsets at which each line begins.
    // Index 0 is always 0 (start of document). Rebuilt lazily after mutations.
    // Guarded by EagerIndexThreshold to avoid huge lists on very large documents.
    // 10M chars ≈ 20 MB UTF-16; at ~100 chars/line that's ~100K line-start entries (800 KB).
    private List<long>? _lineStartCache;

    private const long EagerIndexThreshold = 10_000_000L; // 10 M chars

    // Sentinel: a piece with this Len spans the entire _origBuf from its Start offset
    // without knowing the exact length upfront.  Only valid for Piece.Which == Original
    // and only when _origBuf != null.  Insert/Delete will replace it with real pieces.
    private const long WholeBufSentinel = long.MaxValue;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Constructs a piece-table from an in-memory string.</summary>
    public PieceTable(string originalContent) {
        _orig = originalContent;
        if (originalContent.Length > 0) {
            _pieces.Add(new Piece(BufferKind.Original, 0, originalContent.Length));
        }
    }

    /// <summary>
    /// Constructs a piece-table from an <see cref="IBuffer"/>.
    /// The piece-table does NOT take ownership of the buffer; the caller is responsible
    /// for disposing it after the piece-table is no longer needed.
    /// </summary>
    public PieceTable(IBuffer buf) {
        _orig = string.Empty;  // unused when _origBuf is set
        _origBuf = buf;
        // Add the entire buffer as the initial piece.  When length is not yet known
        // we use WholeBufSentinel so that Length is not computed on construction.
        var len = buf.LengthIsKnown ? buf.Length : WholeBufSentinel;
        if (len != 0) {
            _pieces.Add(new Piece(BufferKind.Original, 0, len));
        }
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// The underlying <see cref="IBuffer"/> when constructed from a buffer, or <c>null</c>
    /// when constructed from a string.
    /// </summary>
    public IBuffer? OrigBuffer => _origBuf;

    /// <summary>
    /// <c>true</c> when the document content is the unedited original buffer
    /// (no inserts or deletes have been applied).
    /// </summary>
    public bool IsOriginalContent =>
        _origBuf != null && _pieces.Count == 1 && _pieces[0].Which == BufferKind.Original;

    /// <summary>Total number of characters in the document.</summary>
    public long Length {
        get {
            var total = 0L;
            foreach (var p in _pieces) {
                total += p.Len == WholeBufSentinel ? _origBuf!.Length - p.Start : p.Len;
            }
            return total;
        }
    }

    /// <summary>
    /// Number of logical lines (always at least 1), or -1 if the document is too large
    /// for eager line indexing and no buffer-level line index is available.
    /// </summary>
    public long LineCount {
        get {
            // Fast path: unedited buffer — exact.
            if (_origBuf?.LineCount >= 0 && _pieces.Count == 1 &&
                _pieces[0].Which == BufferKind.Original) {
                return _origBuf.LineCount;
            }
            if (Length > EagerIndexThreshold) {
                // Too large for eager indexing. If the buffer has a line count,
                // use it as an approximation (off by ±edits, but prevents freeze).
                if (_origBuf?.LineCount >= 0) {
                    return _origBuf.LineCount;
                }
                return -1L;
            }
            return LineStarts.Count;
        }
    }

    // -------------------------------------------------------------------------
    // Mutation
    // -------------------------------------------------------------------------

    /// <summary>Inserts <paramref name="text"/> at logical offset <paramref name="ofs"/>.</summary>
    public void Insert(long ofs, string text) {
        if (text.Length == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        // Only check upper bound when Length is fully known.
        if (_origBuf == null || _origBuf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        var addStart = (long)_addBuf.Length;
        _addBuf.Append(text);
        var newPiece = new Piece(BufferKind.Add, addStart, text.Length);

        var (pieceIdx, ofsInPiece) = FindPiece(ofs);

        if (ofsInPiece == 0L) {
            _pieces.Insert(pieceIdx, newPiece);
        } else {
            var existing = _pieces[pieceIdx];
            // Split: left part keeps real length; right part may still be sentinel.
            var left = existing.TakeFirst(ofsInPiece);
            Piece right;
            if (existing.Len == WholeBufSentinel) {
                // Right part still covers the remainder of the buffer from its new start.
                right = new Piece(BufferKind.Original, existing.Start + ofsInPiece, WholeBufSentinel);
            } else {
                right = existing.SkipFirst(ofsInPiece);
            }
            _pieces[pieceIdx] = left;
            _pieces.Insert(pieceIdx + 1, newPiece);
            _pieces.Insert(pieceIdx + 2, right);
        }

        _lineStartCache = null;
    }

    /// <summary>Deletes <paramref name="len"/> characters starting at logical offset <paramref name="ofs"/>.</summary>
    public void Delete(long ofs, long len) {
        if (len == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (_origBuf == null || _origBuf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs + len, Length);
        }

        var (startPiece, startOfsInPiece) = FindPiece(ofs);
        var endOfs = ofs + len;
        var (endPiece, endOfsInPiece) = FindPiece(endOfs);

        Piece? leftRemainder = startOfsInPiece > 0
            ? _pieces[startPiece].TakeFirst(startOfsInPiece)
            : null;

        Piece? rightRemainder = null;
        if (endOfsInPiece > 0 && endPiece < _pieces.Count) {
            var ep = _pieces[endPiece];
            rightRemainder = ep.Len == WholeBufSentinel
                ? new Piece(BufferKind.Original, ep.Start + endOfsInPiece, WholeBufSentinel)
                : ep.SkipFirst(endOfsInPiece);
            if (rightRemainder.Value.IsEmpty) {
                rightRemainder = null;
            }
        }

        var removeCount = endPiece - startPiece + (endOfsInPiece > 0 && endPiece < _pieces.Count ? 1 : 0);
        _pieces.RemoveRange(startPiece, removeCount);

        var insertAt = startPiece;
        if (rightRemainder is { IsEmpty: false } r) {
            _pieces.Insert(insertAt, r);
        }
        if (leftRemainder is { IsEmpty: false } l) {
            _pieces.Insert(insertAt, l);
        }

        _lineStartCache = null;
    }

    // -------------------------------------------------------------------------
    // Text access
    // -------------------------------------------------------------------------

    /// <summary>Returns the full document text as a string.</summary>
    public string GetText() {
        var len = Length;
        var sb = new StringBuilder((int)Math.Min(len, int.MaxValue));
        foreach (var p in _pieces) {
            if (p.Len == WholeBufSentinel) {
                var bufLen = (int)(_origBuf!.Length - p.Start);
                var chars = new char[bufLen];
                _origBuf.CopyTo(p.Start, chars, bufLen);
                sb.Append(chars);
            } else {
                sb.Append(BufFor(p.Which), (int)p.Start, (int)p.Len);
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns a substring of the document.</summary>
    public string GetText(long start, int len) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (_origBuf == null || _origBuf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(start + len, Length);
        }

        var sb = new StringBuilder(len);
        VisitPieces(start, len, span => sb.Append(span));
        return sb.ToString();
    }

    /// <summary>
    /// Calls <paramref name="visitor"/> with successive character spans covering
    /// [<paramref name="start"/>, <paramref name="start"/>+<paramref name="len"/>).
    /// Avoids allocating a single large string.
    /// </summary>
    public void ForEachPiece(long start, long len, Action<ReadOnlySpan<char>> visitor) {
        VisitPieces(start, len, visitor);
    }

    // -------------------------------------------------------------------------
    // Line access
    // -------------------------------------------------------------------------

    /// <summary>Returns the logical character offset at which line <paramref name="lineIdx"/> begins.</summary>
    public long LineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        // Fast path: delegate to the underlying buffer when the table is unedited
        // (single original piece). Avoids building the eager line-start cache.
        if (_origBuf != null && _pieces.Count == 1 && _pieces[0].Which == BufferKind.Original) {
            var result = _origBuf.GetLineStart(lineIdx);
            if (result >= 0) {
                return result;
            }
        }
        // Approximate path: edited but document is too large for eager indexing.
        // Use the buffer's line starts (off by ±edit chars, but prevents freeze).
        if (_origBuf != null && Length > EagerIndexThreshold) {
            var result = _origBuf.GetLineStart(lineIdx);
            if (result >= 0) {
                return result;
            }
        }
        var lc = LineCount;
        if (lc >= 0) {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIdx, lc);
        }
        return LineStarts[(int)lineIdx];
    }

    /// <summary>Returns the zero-based line index that contains logical offset <paramref name="ofs"/>.</summary>
    public long LineFromOfs(long ofs) {
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        if (_origBuf == null || _origBuf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        // Fast path: unedited buffer with a line index.
        if (_origBuf != null && _pieces.Count == 1 && _pieces[0].Which == BufferKind.Original
            && _origBuf.LineCount >= 0) {
            return BinarySearchBufferLines(_origBuf, ofs);
        }

        // Approximate path: edited large document — binary search the buffer's line index.
        if (_origBuf != null && Length > EagerIndexThreshold && _origBuf.LineCount >= 0) {
            return BinarySearchBufferLines(_origBuf, ofs);
        }

        var starts = LineStarts;
        var lo = 0;
        var hi = starts.Count - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= ofs) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        return lo;
    }

    /// <summary>
    /// Binary search the buffer's line-start index to find which line contains <paramref name="ofs"/>.
    /// </summary>
    private static long BinarySearchBufferLines(IBuffer buf, long ofs) {
        var lc = buf.LineCount;
        var lo = 0L;
        var hi = lc - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            var start = buf.GetLineStart(mid);
            if (start >= 0 && start <= ofs) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        return lo;
    }

    /// <summary>Returns the text content of line <paramref name="lineIdx"/> without any trailing newline.</summary>
    public string GetLine(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        var lc = LineCount;
        if (lc >= 0) {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIdx, lc);
        }

        // Approximate path for large documents: use buffer's line-start index.
        if (_origBuf != null && Length > EagerIndexThreshold && _origBuf.LineCount >= 0) {
            var lineStart = _origBuf.GetLineStart(lineIdx);
            if (lineStart < 0) {
                return string.Empty;
            }
            long lineEnd;
            if (lineIdx + 1 < _origBuf.LineCount) {
                lineEnd = _origBuf.GetLineStart(lineIdx + 1);
                if (lineEnd < 0) {
                    lineEnd = Length;
                } else {
                    lineEnd--; // back up before newline
                    if (lineEnd > lineStart && CharAt(lineEnd - 1) == '\r') {
                        lineEnd--;
                    }
                }
            } else {
                lineEnd = Length;
            }
            var len = (int)Math.Max(0, lineEnd - lineStart);
            return len <= 0 ? string.Empty : GetText(lineStart, len);
        }

        var starts = LineStarts;
        var lineStart2 = starts[(int)lineIdx];
        long lineEnd2;
        if (lineIdx + 1 < starts.Count) {
            lineEnd2 = starts[(int)(lineIdx + 1)] - 1;
            if (lineEnd2 > lineStart2 && CharAt(lineEnd2 - 1) == '\r') {
                lineEnd2--;
            }
        } else {
            lineEnd2 = Length;
        }
        var len2 = (int)(lineEnd2 - lineStart2);
        return len2 <= 0 ? string.Empty : GetText(lineStart2, len2);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private string BufFor(BufferKind kind) {
        if (kind == BufferKind.Add) {
            return _addBuf.ToString();
        }
        // String-ctor path: _origBuf is null, use _orig directly.
        if (_origBuf == null) {
            return _orig;
        }
        // IBuffer-ctor path (small files only): materialize once and cache.
        if (_origBufCache != null) {
            return _origBufCache;
        }
        var len = (int)_origBuf.Length;
        var chars = new char[len];
        _origBuf.CopyTo(0, chars, len);
        _origBufCache = new string(chars);
        return _origBufCache;
    }

    /// <summary>
    /// Finds the piece and within-piece offset for a given logical document offset.
    /// Returns (pieces.Count, 0) when ofs == Length (i.e., end-of-document).
    /// </summary>
    private (int pieceIdx, long ofsInPiece) FindPiece(long ofs) {
        var remaining = ofs;
        for (var i = 0; i < _pieces.Count; i++) {
            var p = _pieces[i];
            // Sentinel piece: spans the whole remaining buffer — always matches.
            if (p.Len == WholeBufSentinel) {
                return (i, remaining);
            }
            if (remaining < p.Len) {
                return (i, remaining);
            }
            remaining -= p.Len;
        }
        return (_pieces.Count, 0L); // ofs == Length
    }

    private void VisitPieces(long start, long len, Action<ReadOnlySpan<char>> visitor) {
        if (len == 0) {
            return;
        }
        var (startPiece, startOfsInPiece) = FindPiece(start);
        var remaining = len;
        for (var i = startPiece; i < _pieces.Count && remaining > 0; i++) {
            var p = _pieces[i];
            var pieceOfs = i == startPiece ? startOfsInPiece : 0L;

            if (p.Len == WholeBufSentinel) {
                // Unknown-length IBuffer piece: read directly without materialising the whole buffer.
                // After a split, the piece covers [p.Start .. end-of-buffer), so clamp to actual available chars.
                var bufAvail = _origBuf!.Length - (p.Start + pieceOfs);
                var take = (int)Math.Min(remaining, bufAvail);
                if (take > 0) {
                    var chars = new char[take];
                    _origBuf.CopyTo(p.Start + pieceOfs, chars, take);
                    visitor(chars);
                }
                remaining -= take;
            } else {
                var avail = p.Len - pieceOfs;
                var take = (int)Math.Min(avail, remaining);
                var buf = BufFor(p.Which).AsSpan((int)(p.Start + pieceOfs), take);
                visitor(buf);
                remaining -= take;
            }
        }
    }

    private char CharAt(long ofs) {
        var (pieceIdx, ofsInPiece) = FindPiece(ofs);
        if (pieceIdx >= _pieces.Count) {
            throw new ArgumentOutOfRangeException(nameof(ofs));
        }
        var p = _pieces[pieceIdx];
        if (p.Len == WholeBufSentinel) {
            return _origBuf![p.Start + ofsInPiece];
        }
        return BufFor(p.Which)[(int)(p.Start + ofsInPiece)];
    }

    private List<long> LineStarts {
        get {
            if (_lineStartCache != null) {
                return _lineStartCache;
            }
            if (Length > EagerIndexThreshold) {
                throw new NotSupportedException(
                    "Document is too large for eager line indexing. " +
                    "Use an IBuffer that provides GetLineStart() instead.");
            }
            _lineStartCache = BuildLineStarts();
            return _lineStartCache;
        }
    }

    private List<long> BuildLineStarts() {
        var starts = new List<long> { 0L };
        var logicalOfs = 0L;
        var prevWasCr = false; // \r seen at end of last chunk — line start deferred
        foreach (var p in _pieces) {
            if (p.Len == WholeBufSentinel) {
                BuildLineStartsFromBuffer(starts, _origBuf!, p.Start, ref logicalOfs, ref prevWasCr);
            } else {
                var buf = BufFor(p.Which).AsSpan((int)p.Start, (int)p.Len);
                ScanForNewlines(buf, starts, ref logicalOfs, ref prevWasCr);
            }
        }
        // A trailing bare \r at end of document starts a new empty line.
        if (prevWasCr) {
            starts.Add(logicalOfs);
        }
        return starts;
    }

    private static void BuildLineStartsFromBuffer(
        List<long> starts, IBuffer buf, long bufStart, ref long logicalOfs, ref bool prevWasCr) {
        const int ChunkSize = 4096;
        var bufLen = buf.Length - bufStart;
        var chunkSize = (int)Math.Min(ChunkSize, Math.Min(bufLen, int.MaxValue));
        var chunk = new char[chunkSize];
        var scanned = 0L;
        while (scanned < bufLen) {
            var take = (int)Math.Min(chunk.Length, bufLen - scanned);
            buf.CopyTo(bufStart + scanned, chunk, take);
            ScanForNewlines(chunk.AsSpan(0, take), starts, ref logicalOfs, ref prevWasCr);
            scanned += take;
        }
    }

    /// <summary>
    /// Scans a character span for newlines, appending line-start offsets to
    /// <paramref name="starts"/>. <paramref name="prevWasCr"/> carries state across
    /// chunk/piece boundaries to handle \r\n pairs that straddle two adjacent buffers.
    /// </summary>
    private static void ScanForNewlines(
        ReadOnlySpan<char> buf, List<long> starts, ref long logicalOfs, ref bool prevWasCr) {

        var startIdx = 0;

        // Resolve the deferred \r from the end of the previous span.
        if (prevWasCr && buf.Length > 0) {
            prevWasCr = false;
            if (buf[0] == '\n') {
                // \r\n crossing span boundary: add line start AFTER the \n.
                starts.Add(logicalOfs + 1);
                startIdx = 1; // \n is fully consumed here
            } else {
                // Bare \r: line start is right at the start of this span.
                starts.Add(logicalOfs);
                // Don't advance startIdx — buf[0] must still be processed normally.
            }
        }

        for (var i = startIdx; i < buf.Length; i++) {
            var ch = buf[i];
            if (ch == '\n') {
                // Bare \n or second half of \r\n (already handled by \r branch).
                starts.Add(logicalOfs + i + 1);
            } else if (ch == '\r') {
                if (i + 1 < buf.Length) {
                    if (buf[i + 1] != '\n') {
                        // Bare \r (not followed by \n in this span).
                        starts.Add(logicalOfs + i + 1);
                    }
                    // else \r\n within span: skip \r, let \n add the line start.
                } else {
                    // \r at end of span — defer until next span.
                    prevWasCr = true;
                }
            }
        }

        logicalOfs += buf.Length;
    }
}
