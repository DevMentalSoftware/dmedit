using System.Text;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// Core document storage using the piece-table data structure.
/// Supports O(1) amortized inserts and deletes without copying large buffers.
/// Not thread-safe; all access must be from a single thread.
/// </summary>
public sealed class PieceTable {
    private readonly string _orig;
    private readonly StringBuilder _addBuf = new();
    private readonly List<Piece> _pieces = new();

    // Sorted list of logical character offsets at which each line begins.
    // Index 0 is always 0 (start of document). Rebuilt lazily after mutations.
    private List<int>? _lineStartCache;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public PieceTable(string originalContent) {
        _orig = originalContent;
        if (originalContent.Length > 0) {
            _pieces.Add(new Piece(BufferKind.Original, 0, originalContent.Length));
        }
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    /// <summary>Total number of characters in the document.</summary>
    public int Length {
        get {
            var total = 0;
            foreach (var p in _pieces) {
                total += p.Len;
            }
            return total;
        }
    }

    /// <summary>Number of logical lines (always at least 1).</summary>
    public int LineCount => LineStarts.Count;

    // -------------------------------------------------------------------------
    // Mutation
    // -------------------------------------------------------------------------

    /// <summary>Inserts <paramref name="text"/> at logical offset <paramref name="ofs"/>.</summary>
    public void Insert(int ofs, string text) {
        if (text.Length == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);

        var addStart = _addBuf.Length;
        _addBuf.Append(text);
        var newPiece = new Piece(BufferKind.Add, addStart, text.Length);

        var (pieceIdx, ofs_in_piece) = FindPiece(ofs);

        if (ofs_in_piece == 0) {
            // Insert before the piece at pieceIdx (or at end)
            _pieces.Insert(pieceIdx, newPiece);
        } else {
            // Split the piece at pieceIdx around ofs_in_piece
            var existing = _pieces[pieceIdx];
            var left = existing.TakeFirst(ofs_in_piece);
            var right = existing.SkipFirst(ofs_in_piece);
            _pieces[pieceIdx] = left;
            _pieces.Insert(pieceIdx + 1, newPiece);
            _pieces.Insert(pieceIdx + 2, right);
        }

        _lineStartCache = null;
    }

    /// <summary>Deletes <paramref name="len"/> characters starting at logical offset <paramref name="ofs"/>.</summary>
    public void Delete(int ofs, int len) {
        if (len == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs + len, Length);

        // Clip pieces to the range [ofs, ofs+len) by rebuilding the affected portion.
        var (startPiece, startOfsInPiece) = FindPiece(ofs);
        var endOfs = ofs + len;
        var (endPiece, endOfsInPiece) = FindPiece(endOfs);

        // Pieces to retain before and after the deleted range
        Piece? leftRemainder = startOfsInPiece > 0
            ? _pieces[startPiece].TakeFirst(startOfsInPiece)
            : null;
        Piece? rightRemainder = endOfsInPiece > 0 && endPiece < _pieces.Count
            ? _pieces[endPiece].SkipFirst(endOfsInPiece)
            : null;

        // Remove all pieces touched by the deletion
        var removeCount = endPiece - startPiece + (endOfsInPiece > 0 && endPiece < _pieces.Count ? 1 : 0);
        _pieces.RemoveRange(startPiece, removeCount);

        // Re-insert trimmed remainders
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
        var sb = new StringBuilder(Length);
        foreach (var p in _pieces) {
            sb.Append(BufFor(p.Which), p.Start, p.Len);
        }
        return sb.ToString();
    }

    /// <summary>Returns a substring of the document.</summary>
    public string GetText(int start, int len) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + len, Length);

        var sb = new StringBuilder(len);
        VisitPieces(start, len, (span) => sb.Append(span));
        return sb.ToString();
    }

    /// <summary>
    /// Calls <paramref name="visitor"/> with successive character spans covering
    /// [<paramref name="start"/>, <paramref name="start"/>+<paramref name="len"/>).
    /// Avoids allocating a single large string.
    /// </summary>
    public void ForEachPiece(int start, int len, Action<ReadOnlySpan<char>> visitor) {
        VisitPieces(start, len, visitor);
    }

    // -------------------------------------------------------------------------
    // Line access
    // -------------------------------------------------------------------------

    /// <summary>Returns the logical character offset at which line <paramref name="lineIdx"/> begins.</summary>
    public int LineStartOfs(int lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIdx, LineCount);
        return LineStarts[lineIdx];
    }

    /// <summary>Returns the zero-based line index that contains logical offset <paramref name="ofs"/>.</summary>
    public int LineFromOfs(int ofs) {
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);

        var starts = LineStarts;
        // Binary search for the last line start <= ofs
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

    /// <summary>Returns the text content of line <paramref name="lineIdx"/> without any trailing newline.</summary>
    public string GetLine(int lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIdx, LineCount);

        var starts = LineStarts;
        var lineStart = starts[lineIdx];
        int lineEnd;
        if (lineIdx + 1 < starts.Count) {
            // End is before the newline character of the next line start
            lineEnd = starts[lineIdx + 1] - 1;
            // Handle \r\n — back up one more if the char before the newline is \r
            if (lineEnd > lineStart && CharAt(lineEnd - 1) == '\r') {
                lineEnd--;
            }
        } else {
            lineEnd = Length;
        }
        var len = lineEnd - lineStart;
        return len <= 0 ? string.Empty : GetText(lineStart, len);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private string BufFor(BufferKind kind) => kind == BufferKind.Original ? _orig : _addBuf.ToString();

    /// <summary>
    /// Finds the piece and within-piece offset for a given logical document offset.
    /// Returns (pieces.Count, 0) when ofs == Length (i.e., end-of-document).
    /// </summary>
    private (int pieceIdx, int ofsInPiece) FindPiece(int ofs) {
        var remaining = ofs;
        for (var i = 0; i < _pieces.Count; i++) {
            if (remaining < _pieces[i].Len) {
                return (i, remaining);
            }
            remaining -= _pieces[i].Len;
        }
        return (_pieces.Count, 0); // ofs == Length
    }

    private void VisitPieces(int start, int len, Action<ReadOnlySpan<char>> visitor) {
        if (len == 0) {
            return;
        }
        var (startPiece, startOfsInPiece) = FindPiece(start);
        var remaining = len;
        for (var i = startPiece; i < _pieces.Count && remaining > 0; i++) {
            var p = _pieces[i];
            var pieceOfs = i == startPiece ? startOfsInPiece : 0;
            var avail = p.Len - pieceOfs;
            var take = Math.Min(avail, remaining);
            var buf = BufFor(p.Which).AsSpan(p.Start + pieceOfs, take);
            visitor(buf);
            remaining -= take;
        }
    }

    private char CharAt(int ofs) {
        var (pieceIdx, ofsInPiece) = FindPiece(ofs);
        if (pieceIdx >= _pieces.Count) {
            throw new ArgumentOutOfRangeException(nameof(ofs));
        }
        var p = _pieces[pieceIdx];
        return BufFor(p.Which)[p.Start + ofsInPiece];
    }

    private List<int> LineStarts {
        get {
            if (_lineStartCache != null) {
                return _lineStartCache;
            }
            _lineStartCache = BuildLineStarts();
            return _lineStartCache;
        }
    }

    private List<int> BuildLineStarts() {
        var starts = new List<int> { 0 };
        var logicalOfs = 0;
        foreach (var p in _pieces) {
            var buf = BufFor(p.Which).AsSpan(p.Start, p.Len);
            for (var i = 0; i < buf.Length; i++) {
                var ch = buf[i];
                if (ch == '\n') {
                    starts.Add(logicalOfs + i + 1);
                } else if (ch == '\r') {
                    // \r\n counts as one newline; bare \r counts as one newline
                    var next = i + 1 < buf.Length ? buf[i + 1] :
                               (logicalOfs + i + 1 < Length ? CharAt(logicalOfs + i + 1) : '\0');
                    if (next != '\n') {
                        starts.Add(logicalOfs + i + 1);
                    }
                    // If \r\n, the \n handler above will add the line start
                }
            }
            logicalOfs += p.Len;
        }
        return starts;
    }
}
