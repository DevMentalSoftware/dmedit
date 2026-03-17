using System.Text;
using DevMentalMd.Core.Buffers;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// Core document storage using the piece-table data structure.
/// Supports O(1) amortized inserts and deletes without copying large buffers.
/// Not thread-safe; all access must be from a single thread.
/// </summary>
public sealed class PieceTable {

    private readonly IBuffer _buf;

    private readonly StringBuilder _addBuf = new();
    private string? _addBufCache;
    private readonly List<Piece> _pieces = new();

    // Lazily-built line-start cache for post-mutation correctness.
    // When the piece table is unedited (IsOriginalContent), line operations
    // delegate directly to the IBuffer.  After Insert/Delete, this cache is
    // invalidated (set to null) and rebuilt on next access by scanning the
    // modified text via VisitPieces.
    private List<long>? _lineStartCache;
    private int _maxLineLen = -1;

    // Sentinel: a piece with this Len spans the entire _buf from its Start offset
    // without knowing the exact length upfront.  Only valid for Piece.Which == Original.
    // Insert/Delete will replace it with real pieces.
    private const long WholeBufSentinel = long.MaxValue;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Constructs a piece-table from an in-memory string.</summary>
    /// Used only for testing edge cases
    public PieceTable(string originalContent) : this(new StringBuffer(originalContent)) {
    }

    /// <summary>
    /// Constructs a piece-table from an <see cref="IBuffer"/>.
    /// The piece-table does NOT take ownership of the buffer; the caller is responsible
    /// for disposing it after the piece-table is no longer needed.
    /// </summary>
    public PieceTable(IBuffer buf) {
        _buf = buf;
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
    public IBuffer Buffer => _buf;

    /// <summary>
    /// <c>true</c> when the document content is the unedited original buffer
    /// (no inserts or deletes have been applied).
    /// </summary>
    public bool IsOriginalContent =>
        _pieces.Count == 1 && _pieces[0].Which == BufferKind.Original;

    /// <summary>Total number of characters in the document.</summary>
    public long Length {
        get {
            var total = 0L;
            foreach (var p in _pieces) {
                total += p.Len == WholeBufSentinel ? _buf!.Length - p.Start : p.Len;
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
            if (IsOriginalContent && _buf.LineCount >= 0) return _buf.LineCount;
            return LineStarts.Count;
        }
    }

    /// <summary>
    /// Length of the longest logical line in the document (including its newline
    /// terminator), or <c>-1</c> when not available.
    /// </summary>
    public long MaxLineLength {
        get {
            if (IsOriginalContent && _buf.LongestLine >= 0) return _buf.LongestLine;
            _ = LineStarts; // force cache build which sets _maxLineLen
            return _maxLineLen;
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
        if (_buf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        var addStart = (long)_addBuf.Length;
        _addBuf.Append(text);
        _addBufCache = null;
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
        _maxLineLen = -1;
    }

    /// <summary>Deletes <paramref name="len"/> characters starting at logical offset <paramref name="ofs"/>.</summary>
    public void Delete(long ofs, long len) {
        if (len == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (_buf.LengthIsKnown) {
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
        _maxLineLen = -1;
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
                var bufLen = (int)(_buf!.Length - p.Start);
                var chars = new char[bufLen];
                _buf.CopyTo(p.Start, chars, bufLen);
                sb.Append(chars);
            } else if (p.Which == BufferKind.Original) {
                var chars = new char[(int)p.Len];
                _buf.CopyTo(p.Start, chars, (int)p.Len);
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
        if (_buf.LengthIsKnown) {
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

    /// <summary>
    /// Returns the logical character offset at which line <paramref name="lineIdx"/> begins,
    /// or <c>-1</c> if the line start is not yet available (buffer still loading).
    /// </summary>
    public long LineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        if (IsOriginalContent) return _buf.GetLineStart(lineIdx);
        var starts = LineStarts;
        if (lineIdx >= starts.Count) return -1L;
        return starts[(int)lineIdx];
    }

    /// <summary>Returns the zero-based line index that contains logical offset <paramref name="ofs"/>.</summary>
    public long LineFromOfs(long ofs) {
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        if (_buf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        if (IsOriginalContent && _buf.LineCount >= 0) {
            return BinarySearchBufferLines(_buf, ofs);
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

        var lineStart = LineStartOfs(lineIdx);
        if (lineStart < 0) return string.Empty;

        long lineEnd;
        if (lineIdx + 1 < lc) {
            lineEnd = LineStartOfs(lineIdx + 1);
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

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private string BufFor(BufferKind kind) {
        // Original pieces are handled via _buf.CopyTo()
        // in VisitPieces/CharAt/BuildLineStarts — BufFor should never be called
        // for Original.
        ArgumentOutOfRangeException.ThrowIfNotEqual(kind, BufferKind.Add);
        return _addBufCache ??= _addBuf.ToString();
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
                var bufAvail = _buf!.Length - (p.Start + pieceOfs);
                var take = (int)Math.Min(remaining, bufAvail);
                if (take > 0) {
                    var chars = new char[take];
                    _buf.CopyTo(p.Start + pieceOfs, chars, take);
                    visitor(chars);
                }
                remaining -= take;
            } else if (p.Which == BufferKind.Original) {
                // Read directly from the buffer — avoids materializing the entire
                // buffer as a string (which would double memory for large files).
                var avail = p.Len - pieceOfs;
                var take = (int)Math.Min(avail, remaining);
                var chars = new char[take];
                _buf.CopyTo(p.Start + pieceOfs, chars, take);
                visitor(chars);
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

    /// <summary>
    /// Lazily-built line-start list for edited documents. When the piece table
    /// is unedited, callers use the buffer directly. After mutations this is
    /// rebuilt by scanning the modified text via <see cref="VisitPieces"/>.
    /// </summary>
    private List<long> LineStarts {
        get {
            if (_lineStartCache != null) return _lineStartCache;
            // For unedited content, build from the buffer's line index.
            if (IsOriginalContent && _buf.LineCount >= 0) {
                var lc = (int)_buf.LineCount;
                var starts = new List<long>(lc);
                for (var i = 0; i < lc; i++) starts.Add(_buf.GetLineStart(i));
                _lineStartCache = starts;
                _maxLineLen = _buf.LongestLine;
                return starts;
            }
            _lineStartCache = BuildLineStarts();
            return _lineStartCache;
        }
    }

    private List<long> BuildLineStarts() {
        var starts = new List<long> { 0L };
        var logicalOfs = 0L;
        var prevWasCr = false;

        VisitPieces(0, Length, span => {
            for (var i = 0; i < span.Length; i++) {
                var ch = span[i];
                if (prevWasCr) {
                    prevWasCr = false;
                    if (ch == '\n') {
                        // \r\n pair crossing chunk boundary — adjust the
                        // tentative line start to be after the \n.
                        starts[^1] = logicalOfs + i + 1;
                        continue;
                    }
                    // Bare \r — line start already recorded. Fall through
                    // to process current char normally.
                }
                if (ch == '\n') {
                    starts.Add(logicalOfs + i + 1);
                } else if (ch == '\r') {
                    // Tentative line start — may be adjusted if next char is \n.
                    starts.Add(logicalOfs + i + 1);
                    prevWasCr = true;
                }
            }
            logicalOfs += span.Length;
        });

        // Compute max line length
        var maxLen = 0;
        for (var i = 1; i < starts.Count; i++) {
            var len = (int)(starts[i] - starts[i - 1]);
            if (len > maxLen) maxLen = len;
        }
        var lastLen = (int)(logicalOfs - starts[^1]);
        if (lastLen > maxLen) maxLen = lastLen;
        _maxLineLen = maxLen;

        return starts;
    }

    internal char CharAt(long ofs) {
        var (pieceIdx, ofsInPiece) = FindPiece(ofs);
        if (pieceIdx >= _pieces.Count) {
            throw new ArgumentOutOfRangeException(nameof(ofs));
        }
        var p = _pieces[pieceIdx];
        if (p.Len == WholeBufSentinel) {
            return _buf![p.Start + ofsInPiece];
        }
        if (p.Which == BufferKind.Original) {
            return _buf[p.Start + ofsInPiece];
        }
        return BufFor(p.Which)[(int)(p.Start + ofsInPiece)];
    }
}
