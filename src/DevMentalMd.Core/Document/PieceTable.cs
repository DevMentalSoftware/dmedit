using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Collections;

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

    // FenwickTree-based line index: stores line lengths (including terminators).
    // Provides O(log L) prefix-sum queries for LineStartOfs/LineFromOfs and
    // O(log L) incremental updates for non-newline edits.  Rebuilt lazily in
    // O(N chars) only when newlines are inserted/deleted.
    private FenwickTree? _lineTree;
    private List<double>? _lineLengths;
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
    /// Number of logical lines (always at least 1).
    /// </summary>
    public long LineCount {
        get {
            if (IsOriginalContent && _buf.LineCount >= 0) return _buf.LineCount;
            return LineTree.Count;
        }
    }

    /// <summary>
    /// Length of the longest logical line in the document (including its newline
    /// terminator), or <c>-1</c> when not available.
    /// </summary>
    public long MaxLineLength {
        get {
            if (IsOriginalContent && _buf.LongestLine >= 0) return _buf.LongestLine;
            if (_maxLineLen < 0) {
                // Recompute from line lengths.
                var tree = LineTree;
                var max = 0.0;
                for (var i = 0; i < tree.Count; i++) {
                    var v = tree.ValueAt(i);
                    if (v > max) max = v;
                }
                _maxLineLen = (int)max;
            }
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

        // Pre-mutation: capture line info for incremental tree update.
        var hasNewlines = text.AsSpan().IndexOfAny('\n', '\r') >= 0;
        var affectedLine = -1;
        long lineStart = 0;
        double oldLineLen = 0;
        if (_lineTree != null) {
            affectedLine = (int)LineFromOfs(ofs);
            lineStart = affectedLine == 0 ? 0 : (long)_lineTree.PrefixSum(affectedLine - 1);
            oldLineLen = _lineLengths![affectedLine];
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

        // Post-mutation: update line tree.
        if (affectedLine >= 0 && !hasNewlines) {
            // No newlines, tree exists: O(log L) incremental update.
            _lineLengths![affectedLine] += text.Length;
            _lineTree!.Update(affectedLine, text.Length);
            if (_lineLengths[affectedLine] > _maxLineLen)
                _maxLineLen = (int)_lineLengths[affectedLine];
        } else if (affectedLine >= 0 && hasNewlines) {
            // Newlines inserted: splice line lengths array, O(L) rebuild.
            SpliceInsertLines(affectedLine, lineStart, oldLineLen, ofs, text);
        } else {
            // Tree not yet built: will be built lazily when needed.
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
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

        // Pre-mutation: scan deleted range for newlines to decide update strategy.
        var hasNewline = false;
        var startLine = -1;
        var endLine = -1;
        if (_lineTree != null) {
            // Scan deleted range for newline characters.
            VisitPieces(ofs, len, span => {
                if (!hasNewline && span.IndexOfAny('\n', '\r') >= 0) hasNewline = true;
            });
            startLine = (int)LineFromOfs(ofs);
            if (hasNewline) {
                // Use ofs+len (first surviving char after deletion) to find the
                // last line that participates in the merge.  Using ofs+len-1
                // would stay on startLine when deleting a line's own terminator.
                endLine = (int)LineFromOfs(Math.Min(ofs + len, Length));
            }
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

        // Post-mutation: update line tree.
        if (startLine >= 0 && !hasNewline) {
            // No newlines deleted: O(log L) incremental update.
            _lineLengths![startLine] -= len;
            _lineTree!.Update(startLine, -len);
            _maxLineLen = -1; // conservative: may have shortened the longest line
        } else if (startLine >= 0 && hasNewline) {
            // Newlines deleted: merge lines, O(L) rebuild.
            SpliceDeleteLines(startLine, endLine, ofs, len);
        } else {
            _lineTree = null;
            _lineLengths = null;
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
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
        var tree = LineTree;
        if (lineIdx >= tree.Count) return -1L;
        return lineIdx == 0 ? 0 : (long)tree.PrefixSum((int)lineIdx - 1);
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
        var tree = LineTree;
        if (tree.Count == 0) return 0;
        var idx = tree.FindByPrefixSum(ofs + 1);
        return idx >= 0 ? idx : tree.Count - 1;
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
        // in VisitPieces/CharAt/BuildLineTree — BufFor should never be called
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

    private FenwickTree LineTree {
        get {
            if (_lineTree != null) return _lineTree;
            BuildLineTree();
            return _lineTree!;
        }
    }

    private void BuildLineTree() {
        var lengths = new List<double>();
        var currentLineLen = 0.0;
        var prevWasCr = false;
        var totalLen = Length;

        if (totalLen == 0) {
            lengths.Add(0);
        } else {
            VisitPieces(0, totalLen, span => {
                for (var i = 0; i < span.Length; i++) {
                    var ch = span[i];
                    if (prevWasCr) {
                        prevWasCr = false;
                        if (ch == '\n') {
                            // \r\n pair: add the \n to the previous line's length
                            lengths[^1]++;
                            continue;
                        }
                    }
                    currentLineLen++;
                    if (ch == '\n') {
                        lengths.Add(currentLineLen);
                        currentLineLen = 0;
                    } else if (ch == '\r') {
                        lengths.Add(currentLineLen);
                        currentLineLen = 0;
                        prevWasCr = true;
                    }
                }
            });
            // Last line (may be empty, e.g. after a trailing newline)
            lengths.Add(currentLineLen);
        }

        var maxLen = 0.0;
        foreach (var l in lengths) {
            if (l > maxLen) maxLen = l;
        }
        _maxLineLen = (int)maxLen;

        _lineLengths = lengths;
        _lineTree = FenwickTree.FromValues(CollectionsMarshal.AsSpan(lengths));
    }

    /// <summary>
    /// Splices the line-lengths array after a newline-containing insert.
    /// The affected line is split into multiple new lines based on the
    /// newline positions in the inserted text.  O(L) tree rebuild.
    /// </summary>
    private void SpliceInsertLines(int line, long lineStart, double oldLineLen,
                                   long insertOfs, string text) {
        var prefixLen = (double)(insertOfs - lineStart);   // chars before insert on this line
        var suffixLen = oldLineLen - prefixLen;            // chars after insert on this line

        // Scan inserted text for newline positions to compute new line lengths.
        var newLines = new List<double>();
        var cur = prefixLen;  // first new line starts with the prefix
        var prevCr = false;
        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (prevCr) {
                prevCr = false;
                if (ch == '\n') {
                    newLines[^1]++; // extend \r to \r\n
                    continue;
                }
            }
            cur++;
            if (ch == '\n') {
                newLines.Add(cur);
                cur = 0;
            } else if (ch == '\r') {
                newLines.Add(cur);
                cur = 0;
                prevCr = true;
            }
        }
        // Last segment: add the suffix from the original line.
        cur += suffixLen;
        newLines.Add(cur);

        // Replace the single entry at 'line' with the new entries.
        _lineLengths!.RemoveAt(line);
        _lineLengths.InsertRange(line, newLines);
        _lineTree!.Rebuild(CollectionsMarshal.AsSpan(_lineLengths));
        _maxLineLen = -1;
    }

    /// <summary>
    /// Splices the line-lengths array after a newline-crossing delete.
    /// Lines startLine..endLine merge into a single line.  O(L) tree rebuild.
    /// </summary>
    private void SpliceDeleteLines(int startLine, int endLine, long deleteOfs, long deleteLen) {
        var startLineStart = startLine == 0 ? 0L : (long)_lineTree!.PrefixSum(startLine - 1);
        var endLineStart = endLine == 0 ? 0L : (long)_lineTree!.PrefixSum(endLine - 1);
        var endLineLen = _lineLengths![endLine];

        // New merged line: prefix of startLine + suffix of endLine.
        var prefixLen = deleteOfs - startLineStart;
        var suffixLen = endLineLen - ((deleteOfs + deleteLen) - endLineStart);
        var mergedLen = prefixLen + suffixLen;

        // Remove lines startLine..endLine, replace with merged.
        _lineLengths.RemoveRange(startLine, endLine - startLine + 1);
        _lineLengths.Insert(startLine, mergedLen);
        _lineTree!.Rebuild(CollectionsMarshal.AsSpan(_lineLengths));
        _maxLineLen = -1;
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

    /// <summary>
    /// Debug-only validation: checks that the FenwickTree's total sum matches
    /// the document Length and that _lineLengths count matches the tree count.
    /// Catches tree corruption from incremental update bugs.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertLineTreeValid() {
        if (_lineTree == null || _lineLengths == null) return;
        var docLen = Length;
        var treeTotal = (long)_lineTree.TotalSum();
        Debug.Assert(treeTotal == docLen,
            $"Line tree total {treeTotal} != Length {docLen} (delta {treeTotal - docLen})");
        Debug.Assert(_lineLengths.Count == _lineTree.Count,
            $"_lineLengths.Count {_lineLengths.Count} != tree.Count {_lineTree.Count}");
    }
}
