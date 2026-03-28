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

    // LineIndexTree-based line index: stores line lengths (including terminators).
    // Provides O(log L) prefix-sum queries for LineStartOfs/LineFromOfs and
    // O(log L) incremental updates for non-newline edits.  Individual values
    // are derived from the tree via ValueAt() — no parallel list is kept.
    // Lines longer than MaxPseudoLine are split into pseudo-lines so the
    // layout engine never sees a single enormous line.
    private LineIndexTree? _lineTree;
    private int _maxLineLen = -1;

    /// <summary>
    /// Maximum characters per line in the line tree.  Lines exceeding this
    /// are split into pseudo-lines during <see cref="BuildLineTree"/> and
    /// after incremental edits.  The document text is never modified —
    /// pseudo-newlines exist only in the line tree.
    /// </summary>
    public const int MaxPseudoLine = 500;

    // Sentinel: a piece with this Len spans the entire _buf from its Start offset
    // without knowing the exact length upfront.  Only valid for Piece.Which == Original.
    // Insert/Delete will replace it with real pieces.
    private const long WholeBufSentinel = long.MaxValue;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Creates an empty piece-table (for untitled/new documents).</summary>
    public PieceTable() : this(EmptyBuffer.Instance) {
        BuildLineTree();
    }

    /// <summary>Constructs a piece-table from an in-memory string (tests only).</summary>
    internal PieceTable(string originalContent) : this(new StringBuffer(originalContent)) {
        BuildLineTree();
    }

    /// <summary>Constructs a piece-table from an <see cref="IBuffer"/>.</summary>
    /// <remarks>
    /// For paged file content, pass a <see cref="PagedFileBuffer"/>.
    /// Call <see cref="InstallLineTree"/> or <see cref="EnsureLineTree"/> after
    /// the buffer is fully loaded and before any edits.
    /// </remarks>

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
            if (_lineTree != null) return _lineTree.Count;
            if (IsOriginalContent && _buf.LineCount >= 0
                && _buf.LongestLine >= 0 && _buf.LongestLine <= MaxPseudoLine)
                return _buf.LineCount;
            return LineTree.Count;
        }
    }

    /// <summary>
    /// Length of the longest logical line in the document (including its newline
    /// terminator), or <c>-1</c> when not available.
    /// </summary>
    public long MaxLineLength {
        get {
            if (_lineTree == null && IsOriginalContent && _buf.LongestLine >= 0) return _buf.LongestLine;
            if (_maxLineLen < 0) {
                // Recompute from line lengths.
                var tree = LineTree;
                var max = 0;
                for (var i = 0; i < tree.Count; i++) {
                    var v = tree.ValueAt(i);
                    if (v > max) max = v;
                }
                _maxLineLen = max;
            }
            return _maxLineLen;
        }
    }

    // -------------------------------------------------------------------------
    // Mutation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-inserts previously captured pieces at logical offset <paramref name="ofs"/>.
    /// Only manipulates the piece list — the caller must update the line tree
    /// separately (e.g. via <see cref="RestoreLines"/>).
    /// </summary>
    public void InsertPieces(CharOffset ofs, Piece[] pieces) {
        if (pieces.Length == 0) return;
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);

        var (pieceIdx, ofsInPiece) = FindPiece(ofs);

        if (ofsInPiece == 0L) {
            _pieces.InsertRange(pieceIdx, pieces);
        } else {
            var existing = _pieces[pieceIdx];
            var left = existing.TakeFirst(ofsInPiece);
            Piece right;
            if (existing.Len == WholeBufSentinel) {
                right = new Piece(BufferKind.Original, existing.Start + ofsInPiece, WholeBufSentinel);
            } else {
                right = existing.SkipFirst(ofsInPiece);
            }
            _pieces[pieceIdx] = left;
            _pieces.InsertRange(pieceIdx + 1, pieces);
            _pieces.Insert(pieceIdx + 1 + pieces.Length, right);
        }
    }

    /// <summary>
    /// Atomically re-inserts pieces and restores line tree entries.
    /// Used by <see cref="DeleteEdit.Revert"/> to guarantee the piece list
    /// and line tree are never in an inconsistent state.
    /// </summary>
    public void InsertPiecesAndRestoreLines(
        CharOffset ofs, Piece[] pieces,
        int lineInfoStart, int[]? lineInfoLengths, long len) {

        InsertPieces(ofs, pieces);
        if (lineInfoLengths != null) {
            RestoreLines(lineInfoStart, lineInfoLengths);
        } else {
            ReinsertedNonNewlineChars(ofs, len);
        }
    }

    /// <summary>Inserts <paramref name="text"/> at logical offset <paramref name="ofs"/>.</summary>
    public void Insert(CharOffset ofs, string text) {
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
        LineIndex affectedLine = -1;
        CharOffset lineStart = 0;
        int oldLineLen = 0;
        if (_lineTree != null) {
            affectedLine = (LineIndex)LineFromOfs(ofs);
            lineStart = affectedLine == 0 ? 0 : _lineTree.PrefixSum(affectedLine - 1);
            oldLineLen = _lineTree.ValueAt(affectedLine);
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
            _lineTree!.Update(affectedLine, text.Length);
            var newLen = _lineTree.ValueAt(affectedLine);
            if (newLen > _maxLineLen)
                _maxLineLen = newLen;
            SplitLongLine(affectedLine);
        } else if (affectedLine >= 0 && hasNewlines) {
            // Newlines inserted: splice line lengths array, O(L) rebuild.
            SpliceInsertLines(affectedLine, lineStart, oldLineLen, ofs, text);
            // SpliceInsertLines replaced 1 line with nlCount lines; count
            // them by scanning forward from affectedLine.
            var nlCount = (int)(LineFromOfs(Math.Min(ofs + text.Length, Length)) - affectedLine + 1);
            SplitLongLines(affectedLine, nlCount);
        } else {
            // affectedLine < 0 means _lineTree was null when we checked above.
            // This should never happen once the tree is installed.
            Debug.Assert(false, "Insert called without a line tree.");
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
    }

    /// <summary>Deletes <paramref name="len"/> characters starting at logical offset <paramref name="ofs"/>.</summary>
    public void Delete(CharOffset ofs, long len) {
        if (len == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (_buf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs + len, Length);
        }

        // Pre-mutation: use the tree's line structure to determine if the
        // deletion crosses a line boundary — O(log L), no character scanning.
        var hasNewline = false;
        LineIndex startLine = -1;
        LineIndex endLine = -1;
        if (_lineTree != null) {
            startLine = (LineIndex)LineFromOfs(ofs);
            // Check if the deletion extends past the end of startLine.
            var lineEnd = (startLine == 0 ? 0L : _lineTree.PrefixSum(startLine - 1))
                        + _lineTree.ValueAt(startLine);
            if (ofs + len >= lineEnd) {
                hasNewline = true;
                endLine = (LineIndex)LineFromOfs(Math.Min(ofs + len, Length));
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
            _lineTree!.Update(startLine, (int)-len);
            // Max may have decreased, but overestimating is harmless (extra
            // horizontal scroll space).  Avoid O(n log n) full recompute.
        } else if (startLine >= 0 && hasNewline) {
            // Newlines deleted: merge lines, O(L) rebuild.
            SpliceDeleteLines(startLine, endLine, ofs, len);
            SplitLongLine(startLine); // merged line may exceed limit
        } else {
            // startLine < 0 means _lineTree was null when we checked above.
            // This should never happen once the tree is installed — edits
            // require a built tree for correct line-tree maintenance.
            Debug.Assert(false, "Delete called without a line tree.");
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
    }

    // -------------------------------------------------------------------------
    // Text access
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the full document text as a string.
    /// Test-only — production code must use <see cref="GetText(long, int)"/>
    /// or <see cref="ForEachPiece"/> to avoid materializing the entire document.
    /// </summary>
    internal string GetText() {
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

    /// <summary>
    /// Maximum length allowed for <see cref="GetText"/>.  Guards against
    /// accidental materialization of multi-GB documents into a single string.
    /// Code that needs larger ranges should use <see cref="ForEachPiece"/>.
    /// </summary>
    public const int MaxGetTextLength = MaxPseudoLine + 2; // +2 for \r\n terminator

    /// <summary>Returns a substring of the document.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="len"/> exceeds <see cref="MaxGetTextLength"/>.
    /// </exception>
    public string GetText(CharOffset start, int len) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (len > MaxGetTextLength) {
            if (System.Diagnostics.Debugger.IsAttached)
                System.Diagnostics.Debugger.Break();
            ArgumentOutOfRangeException.ThrowIfGreaterThan(len, MaxGetTextLength,
                $"GetText len ({len}) exceeds MaxGetTextLength ({MaxGetTextLength}). " +
                "Use ForEachPiece for large ranges.");
        }
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
    public CharOffset LineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        if (_lineTree != null) {
            if (lineIdx >= _lineTree.Count) return -1L;
            return lineIdx == 0 ? 0 : _lineTree.PrefixSum((int)lineIdx - 1);
        }
        if (IsOriginalContent && _buf.LongestLine >= 0 && _buf.LongestLine <= MaxPseudoLine)
            return _buf.GetLineStart(lineIdx);
        var tree = LineTree;
        if (lineIdx >= tree.Count) return -1L;
        return lineIdx == 0 ? 0 : tree.PrefixSum((int)lineIdx - 1);
    }

    /// <summary>Returns the zero-based line index that contains logical offset <paramref name="ofs"/>.</summary>
    public long LineFromOfs(CharOffset ofs) {
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        if (_buf.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }
        if (_lineTree != null) {
            if (_lineTree.Count == 0) return 0;
            var idx = _lineTree.FindByPrefixSum(ofs + 1);
            return idx >= 0 ? idx : _lineTree.Count - 1;
        }
        if (IsOriginalContent && _buf.LineCount >= 0
            && _buf.LongestLine >= 0 && _buf.LongestLine <= MaxPseudoLine) {
            return BinarySearchBufferLines(_buf, ofs);
        }
        var tree = LineTree;
        if (tree.Count == 0) return 0;
        var idx2 = tree.FindByPrefixSum(ofs + 1);
        return idx2 >= 0 ? idx2 : tree.Count - 1;
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
            } else if (lineEnd > lineStart) {
                // Strip trailing newline if present.  Pseudo-line boundaries
                // have no newline character, so check the actual content.
                var ch = CharAt(lineEnd - 1);
                if (ch == '\n') {
                    lineEnd--;
                    if (lineEnd > lineStart && CharAt(lineEnd - 1) == '\r') {
                        lineEnd--;
                    }
                } else if (ch == '\r') {
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

    /// <summary>Maximum char[] allocation per visitor callback (1 MB of chars = 2 MB).</summary>
    private const int MaxVisitChunk = 1024 * 1024;

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
                var bufAvail = _buf!.Length - (p.Start + pieceOfs);
                var pieceRemaining = Math.Min(remaining, bufAvail);
                while (pieceRemaining > 0) {
                    var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                    var chars = new char[take];
                    _buf.CopyTo(p.Start + pieceOfs, chars, take);
                    visitor(chars);
                    pieceOfs += take;
                    pieceRemaining -= take;
                    remaining -= take;
                }
            } else if (p.Which == BufferKind.Original) {
                var avail = p.Len - pieceOfs;
                var pieceRemaining = Math.Min(avail, remaining);
                while (pieceRemaining > 0) {
                    var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                    var chars = new char[take];
                    _buf.CopyTo(p.Start + pieceOfs, chars, take);
                    visitor(chars);
                    pieceOfs += take;
                    pieceRemaining -= take;
                    remaining -= take;
                }
            } else {
                var avail = p.Len - pieceOfs;
                var pieceRemaining = (int)Math.Min(avail, remaining);
                while (pieceRemaining > 0) {
                    var take = Math.Min(pieceRemaining, MaxVisitChunk);
                    var buf = BufFor(p.Which).AsSpan((int)(p.Start + pieceOfs), take);
                    visitor(buf);
                    pieceOfs += take;
                    pieceRemaining -= take;
                    remaining -= take;
                }
            }
        }
    }

    /// <summary>
    /// Installs a pre-built line tree from externally computed line lengths
    /// (e.g. from PagedFileBuffer's scan).  Call on the UI thread before
    /// unblocking input.
    /// </summary>
    public void InstallLineTree(ReadOnlySpan<int> lineLengths) {
        // Check if any entry exceeds MaxPseudoLine and needs splitting.
        var needsSplit = false;
        foreach (var len in lineLengths) {
            if (len > MaxPseudoLine) { needsSplit = true; break; }
        }

        if (needsSplit) {
            // Expand into a mutable list, splitting long entries.
            var expanded = new List<int>(lineLengths.Length);
            foreach (var len in lineLengths) {
                if (len <= MaxPseudoLine) {
                    expanded.Add(len);
                } else {
                    var remaining = len;
                    while (remaining > MaxPseudoLine) {
                        expanded.Add(MaxPseudoLine);
                        remaining -= MaxPseudoLine;
                    }
                    if (remaining > 0) expanded.Add(remaining);
                }
            }
            _lineTree = LineIndexTree.FromValues(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(expanded));
            _maxLineLen = MaxPseudoLine;
        } else {
            _lineTree = LineIndexTree.FromValues(lineLengths);
            var max = 0;
            foreach (var len in lineLengths) {
                if (len > max) max = len;
            }
            _maxLineLen = max;
        }
        AssertLineTreeValid();
    }

    /// <summary>
    /// Ensures the line tree is built.  Call before any operation sequence
    /// (such as session edit replay) that requires line-tree maintenance
    /// to be active during Insert/Delete/CaptureLineInfo.
    /// </summary>
    public void EnsureLineTree() => _ = LineTree;

    private LineIndexTree LineTree {
        get {
            if (_lineTree != null) return _lineTree;
            BuildLineTree();
            Debug.Assert(_maxLineLen <= MaxPseudoLine,
                $"BuildLineTree produced _maxLineLen={_maxLineLen} > MaxPseudoLine={MaxPseudoLine}");
            return _lineTree!;
        }
    }

    private void BuildLineTree() {
        var lengths = new List<int>();
        var currentLineLen = 0;
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
            lengths.Add(currentLineLen);
        }

        var maxLen = 0;
        foreach (var l in lengths) {
            if (l > maxLen) maxLen = l;
        }
        _maxLineLen = maxLen;

        _lineTree = LineIndexTree.FromValues(CollectionsMarshal.AsSpan(lengths));

        // Split any lines that exceed MaxPseudoLine.  For the PieceTable(string)
        // constructor this handles long lines; for PagedFileBuffer files,
        // ScanNewlines already splits, so this is a no-op.
        if (_maxLineLen > MaxPseudoLine) {
            SplitLongLines(0, _lineTree.Count);
        }
    }


    /// <summary>
    /// Static helper: finds the 0-based line index for a character offset.
    /// <paramref name="ofs"/> is a CharOffset — must not be truncated to int.
    /// Returns a LineIndex.
    /// </summary>
    private static LineIndex FindLineByPrefixSum(LineIndexTree tree, CharOffset ofs) {
        if (tree.Count == 0) return 0;
        var idx = tree.FindByPrefixSum(ofs + 1);  // target is char offset + 1 → long
        return idx >= 0 ? idx : tree.Count - 1;
    }

    /// <summary>
    /// Splices the line tree after a newline-containing insert.
    /// The affected line is split into multiple new lines based on the
    /// newline positions in the inserted text.  O(k log L) via treap
    /// InsertRange/RemoveAt.
    /// </summary>
    private void SpliceInsertLines(LineIndex line, CharOffset lineStart, int oldLineLen,
                                   CharOffset insertOfs, string text) {
        var prefixLen = (int)(insertOfs - lineStart);
        var suffixLen = oldLineLen - prefixLen;

        // Compute replacement line lengths from the inserted text.
        Span<int> buf = stackalloc int[Math.Min(text.Length + 1, 64)];
        var newLines = text.Length + 1 <= 64 ? buf : new int[text.Length + 1];
        var nlCount = 0;
        var cur = prefixLen;
        var prevCr = false;
        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (prevCr) {
                prevCr = false;
                if (ch == '\n') { newLines[nlCount - 1]++; continue; }
            }
            cur++;
            if (ch == '\n') { newLines[nlCount++] = cur; cur = 0; }
            else if (ch == '\r') { newLines[nlCount++] = cur; cur = 0; prevCr = true; }
        }
        cur += suffixLen;
        newLines[nlCount++] = cur;

        // Replace the single line with the new lines.
        _lineTree!.RemoveAt(line);
        _lineTree.InsertRange(line, newLines[..nlCount]);
        // The old line was split — each new line is ≤ oldLineLen.
        // Max can only increase if a new line exceeds it (unlikely but check).
        if (_maxLineLen >= 0) {
            for (var i = 0; i < nlCount; i++) {
                if (newLines[i] > _maxLineLen) _maxLineLen = newLines[i];
            }
        }
    }

    /// <summary>
    /// Splices the line tree after a newline-crossing delete.
    /// Lines startLine..endLine merge into a single line.
    /// O(k log L) via treap RemoveRange/InsertAt.
    /// </summary>
    private void SpliceDeleteLines(LineIndex startLine, LineIndex endLine, CharOffset deleteOfs, long deleteLen) {
        var startLineStart = startLine == 0 ? 0L : _lineTree!.PrefixSum(startLine - 1);
        var endLineStart = endLine == 0 ? 0L : _lineTree!.PrefixSum(endLine - 1);
        var endLineLen = _lineTree!.ValueAt(endLine);

        var mergedLen = (int)((deleteOfs - startLineStart) +
            (endLineLen - ((deleteOfs + deleteLen) - endLineStart)));

        _lineTree.RemoveRange(startLine, endLine - startLine + 1);
        _lineTree.InsertAt(startLine, mergedLen);
        // Merged line might be longer than the current max.
        if (_maxLineLen >= 0 && mergedLen > _maxLineLen) {
            _maxLineLen = mergedLen;
        }
    }

    /// <summary>
    /// If the line at <paramref name="lineIdx"/> exceeds <see cref="MaxPseudoLine"/>,
    /// splits it into chunks of MaxPseudoLine (last chunk gets the remainder).
    /// </summary>
    private void SplitLongLine(int lineIdx) {
        var len = _lineTree!.ValueAt(lineIdx);
        if (len <= MaxPseudoLine) return;

        var chunkCount = (len + MaxPseudoLine - 1) / MaxPseudoLine;
        Span<int> chunks = chunkCount <= 64
            ? stackalloc int[chunkCount]
            : new int[chunkCount];
        var remaining = len;
        for (var i = 0; i < chunkCount; i++) {
            chunks[i] = Math.Min(MaxPseudoLine, remaining);
            remaining -= chunks[i];
        }

        _lineTree.RemoveAt(lineIdx);
        _lineTree.InsertRange(lineIdx, chunks);
        // Max line length is now capped at MaxPseudoLine (plus possible
        // newline terminator on the last real line's final chunk).
        if (_maxLineLen >= 0 && _maxLineLen > MaxPseudoLine) {
            _maxLineLen = -1; // conservative recompute
        }
    }

    /// <summary>
    /// Checks lines in the range [startLine, startLine+count) and splits
    /// any that exceed <see cref="MaxPseudoLine"/>.
    /// </summary>
    private void SplitLongLines(int startLine, int count) {
        for (var i = startLine + count - 1; i >= startLine; i--) {
            SplitLongLine(i);
        }
    }

    /// <summary>
    /// Captures line tree information for the range [ofs, ofs+len) so undo can
    /// restore the line tree without scanning characters.  Returns null when
    /// the delete is within a single line (no line structure change).
    /// </summary>
    public (int StartLine, int[] LineLengths)? CaptureLineInfo(CharOffset ofs, long len) {
        Debug.Assert(_lineTree != null, "CaptureLineInfo called without a line tree.");
        if (_lineTree == null) return null; // Release safety net
        var startLine = (int)LineFromOfs(ofs);
        // Check if the deletion crosses line boundaries.
        var lineEnd = (startLine == 0 ? 0L : _lineTree.PrefixSum(startLine - 1))
                    + _lineTree.ValueAt(startLine);
        if (ofs + len < lineEnd) {
            // Single-line delete — no line structure change.
            return null;
        }
        var endLine = (int)LineFromOfs(Math.Min(ofs + len, Length));
        var count = endLine - startLine + 1;
        var lengths = new int[count];
        for (var i = 0; i < count; i++) {
            lengths[i] = _lineTree.ValueAt(startLine + i);
        }
        return (startLine, lengths);
    }

    /// <summary>
    /// Restores line tree entries that were removed by a previous deletion.
    /// Called during undo to reverse <see cref="SpliceDeleteLines"/>.
    /// The merged line at <paramref name="startLine"/> is replaced with the
    /// original lines.
    /// </summary>
    public void RestoreLines(int startLine, int[] lineLengths) {
        Debug.Assert(_lineTree != null, "RestoreLines called without a line tree.");
        if (_lineTree == null) return; // Release safety net
        // Remove the merged line that SpliceDeleteLines created.
        _lineTree.RemoveAt(startLine);
        // Re-insert the original lines.
        _lineTree.InsertRange(startLine, lineLengths);
        _maxLineLen = -1; // conservative recompute
        SplitLongLines(startLine, lineLengths.Length);
    }

    /// <summary>
    /// Updates the line tree after re-inserting non-newline characters at
    /// <paramref name="ofs"/>.  Used by undo for single-line deletes where
    /// the line just got shorter and needs its length restored.
    /// </summary>
    public void ReinsertedNonNewlineChars(CharOffset ofs, long len) {
        Debug.Assert(_lineTree != null, "ReinsertedNonNewlineChars called without a line tree.");
        if (_lineTree == null) return; // Release safety net
        var line = (int)LineFromOfs(ofs);
        _lineTree.Update(line, (int)len);
        var newLen = _lineTree.ValueAt(line);
        if (newLen > _maxLineLen) _maxLineLen = newLen;
        SplitLongLine(line);
    }

    /// <summary>
    /// Captures the piece descriptors covering [ofs, ofs+len) so the text can
    /// be reconstructed later without reading from the buffer now.  Both the
    /// Original buffer and the Add buffer are immutable/append-only, so the
    /// returned pieces remain valid indefinitely.
    /// </summary>
    public Piece[] CapturePieces(CharOffset ofs, long len) {
        if (len == 0) return [];
        var (startPiece, startOfsInPiece) = FindPiece(ofs);
        var endOfs = ofs + len;
        var (endPiece, endOfsInPiece) = FindPiece(endOfs);

        var result = new List<Piece>();
        for (var i = startPiece; i <= endPiece && i < _pieces.Count; i++) {
            var p = _pieces[i];
            var pLen = p.Len == WholeBufSentinel ? _buf.Length - p.Start : p.Len;
            var skip = (i == startPiece) ? startOfsInPiece : 0L;
            var limit = (i == endPiece) ? endOfsInPiece : pLen;
            var take = limit - skip;
            if (take <= 0) continue;
            result.Add(new Piece(p.Which, p.Start + skip, take));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Reads text from previously captured pieces by going directly to the
    /// underlying buffers.  O(total chars) but deferred — only called if the
    /// text is actually needed (e.g. undo).
    /// </summary>
    public string ReadPieces(Piece[] pieces) {
        var totalLen = 0L;
        foreach (var p in pieces) totalLen += p.Len;
        var sb = new StringBuilder((int)Math.Min(totalLen, int.MaxValue));
        foreach (var p in pieces) {
            if (p.Which == BufferKind.Original) {
                // Read in chunks to avoid single huge char[] allocation.
                var ofs = p.Start;
                var pieceRemaining = p.Len;
                while (pieceRemaining > 0) {
                    var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                    var chars = new char[take];
                    _buf.CopyTo(ofs, chars, take);
                    sb.Append(chars);
                    ofs += take;
                    pieceRemaining -= take;
                }
            } else {
                sb.Append(BufFor(p.Which), (int)p.Start, (int)p.Len);
            }
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Bulk replace
    // -------------------------------------------------------------------------

    /// <summary>Current length of the append-only add buffer.</summary>
    public long AddBufferLength => _addBuf.Length;

    /// <summary>Snapshots the current piece list for later restore (undo).</summary>
    public Piece[] SnapshotPieces() => _pieces.ToArray();

    /// <summary>Snapshots the current line tree lengths for later restore (undo).</summary>
    public int[] SnapshotLineLengths() {
        var tree = LineTree;
        return tree.ExtractValues();
    }

    /// <summary>
    /// Replaces the piece list wholesale (used by bulk-replace undo).
    /// Caller must also restore the line tree via <see cref="InstallLineTree"/>.
    /// </summary>
    public void RestorePieces(Piece[] pieces) {
        _pieces.Clear();
        _pieces.AddRange(pieces);
        _addBufCache = null;
    }

    /// <summary>
    /// Truncates the add buffer to <paramref name="len"/> characters.
    /// Used by bulk-replace undo to discard appended replacement text.
    /// </summary>
    public void TrimAddBuffer(long len) {
        if (len < _addBuf.Length) {
            _addBuf.Length = (int)len;
            _addBufCache = null;
        }
    }

    /// <summary>
    /// Uniform bulk replace: all matches have the same length and the same
    /// replacement string. Match positions must be sorted ascending and
    /// non-overlapping.  O(pieces + matches) with one line tree rebuild.
    /// </summary>
    public void BulkReplace(long[] matchPositions, int matchLen, string replacement) {
        if (matchPositions.Length == 0) return;

        // Append replacement once to the add buffer.
        var addOfs = _addBuf.Length;
        if (replacement.Length > 0) {
            _addBuf.Append(replacement);
            _addBufCache = null;
        }

        var replacementPiece = replacement.Length > 0
            ? new Piece(BufferKind.Add, addOfs, replacement.Length)
            : default;

        BulkReplaceCore(matchPositions.Length,
            i => matchPositions[i],
            _ => matchLen,
            _ => replacementPiece);
    }

    /// <summary>
    /// Varying bulk replace: matches have different lengths and/or different
    /// replacements. Matches must be sorted ascending by Pos and non-overlapping.
    /// O(pieces + matches) with one line tree rebuild.
    /// </summary>
    public void BulkReplace((long Pos, int Len)[] matches, string[] replacements) {
        if (matches.Length == 0) return;

        // Append each replacement to the add buffer, recording spans.
        var addSpans = new (long Ofs, int Len)[replacements.Length];
        for (var i = 0; i < replacements.Length; i++) {
            var r = replacements[i];
            addSpans[i] = (_addBuf.Length, r.Length);
            if (r.Length > 0) {
                _addBuf.Append(r);
            }
        }
        _addBufCache = null;

        BulkReplaceCore(matches.Length,
            i => matches[i].Pos,
            i => matches[i].Len,
            i => addSpans[i].Len > 0
                ? new Piece(BufferKind.Add, addSpans[i].Ofs, addSpans[i].Len)
                : default);
    }

    /// <summary>
    /// Core algorithm shared by uniform and varying bulk replace.
    /// Walks the piece list and match list simultaneously, building a new
    /// piece list with match regions replaced by add-buffer pieces.
    /// </summary>
    private void BulkReplaceCore(
        int matchCount,
        Func<int, long> matchPos,
        Func<int, int> matchLenFunc,
        Func<int, Piece> replacementPiece) {

        var newPieces = new List<Piece>(_pieces.Count + matchCount);

        // Cursor into the existing piece list.
        var pieceIdx = 0;
        var ofsInPiece = 0L; // how far into _pieces[pieceIdx] we've consumed
        var docOfs = 0L;     // current document offset

        for (var m = 0; m < matchCount; m++) {
            var mPos = matchPos(m);
            var mLen = matchLenFunc(m);

            // 1. Copy pieces for the gap before this match.
            CopyPiecesUpTo(mPos, newPieces, ref pieceIdx, ref ofsInPiece, ref docOfs);

            // 2. Skip pieces covering the match region.
            SkipPieces(mLen, ref pieceIdx, ref ofsInPiece, ref docOfs);

            // 3. Insert the replacement piece.
            var rp = replacementPiece(m);
            if (!rp.IsEmpty) {
                newPieces.Add(rp);
            }
        }

        // 4. Copy remaining pieces after the last match.
        CopyPiecesUpTo(long.MaxValue, newPieces, ref pieceIdx, ref ofsInPiece, ref docOfs);

        _pieces.Clear();
        _pieces.AddRange(newPieces);
        _addBufCache = null;
        BuildLineTree();
    }

    /// <summary>
    /// Copies piece fragments from the current position up to (but not including)
    /// the target document offset into <paramref name="dest"/>.
    /// </summary>
    private void CopyPiecesUpTo(long targetOfs, List<Piece> dest,
        ref int pieceIdx, ref long ofsInPiece, ref long docOfs) {

        while (pieceIdx < _pieces.Count && docOfs < targetOfs) {
            var p = _pieces[pieceIdx];
            var pLen = p.Len == WholeBufSentinel ? _buf.Length - p.Start : p.Len;
            var remaining = pLen - ofsInPiece;
            var available = Math.Min(remaining, targetOfs - docOfs);

            if (available == remaining) {
                // Take the rest of this piece.
                if (ofsInPiece == 0) {
                    dest.Add(p.Len == WholeBufSentinel
                        ? new Piece(p.Which, p.Start, pLen)
                        : p);
                } else {
                    dest.Add(new Piece(p.Which, p.Start + ofsInPiece, remaining));
                }
                pieceIdx++;
                ofsInPiece = 0;
                docOfs += remaining;
            } else {
                // Take a prefix of this piece.
                dest.Add(new Piece(p.Which, p.Start + ofsInPiece, available));
                ofsInPiece += available;
                docOfs += available;
            }
        }
    }

    /// <summary>
    /// Advances the piece cursor past <paramref name="skipLen"/> characters
    /// without copying anything.
    /// </summary>
    private void SkipPieces(long skipLen, ref int pieceIdx, ref long ofsInPiece, ref long docOfs) {
        var toSkip = skipLen;
        while (toSkip > 0 && pieceIdx < _pieces.Count) {
            var p = _pieces[pieceIdx];
            var pLen = p.Len == WholeBufSentinel ? _buf.Length - p.Start : p.Len;
            var remaining = pLen - ofsInPiece;

            if (toSkip >= remaining) {
                toSkip -= remaining;
                docOfs += remaining;
                pieceIdx++;
                ofsInPiece = 0;
            } else {
                ofsInPiece += toSkip;
                docOfs += toSkip;
                toSkip = 0;
            }
        }
    }

    internal char CharAt(CharOffset ofs) {
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
    /// Debug-only validation: checks that the LineIndexTree's total sum matches
    /// the document Length.  Catches tree corruption from incremental update bugs.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertLineTreeValid() {
        Debug.Assert(_lineTree != null);
        var docLen = Length;
        var treeTotal = _lineTree.TotalSum();
        Debug.Assert(treeTotal == docLen,
            $"Line tree total {treeTotal} != Length {docLen} (delta {treeTotal - docLen})");
    }

    /// <summary>
    /// Minimal zero-length buffer for empty/untitled documents.
    /// </summary>
    private sealed class EmptyBuffer : IBuffer {
        public static readonly EmptyBuffer Instance = new();
        public long Length => 0;
        public bool LengthIsKnown => true;
        public long LineCount => 1;
        public int LongestLine => 0;
        public long GetLineStart(long lineIdx) => lineIdx == 0 ? 0 : -1;
        public char this[long offset] => throw new ArgumentOutOfRangeException(nameof(offset));
        public void CopyTo(long offset, Span<char> destination, int len) =>
            throw new ArgumentOutOfRangeException(nameof(offset));
        public void Dispose() { }
    }
}
