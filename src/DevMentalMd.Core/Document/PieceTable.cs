using System.Collections.Concurrent;
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

    // IntFenwickTree-based line index: stores line lengths (including terminators).
    // Provides O(log L) prefix-sum queries for LineStartOfs/LineFromOfs and
    // O(log L) incremental updates for non-newline edits.  Individual values
    // are derived from the tree via ValueAt() — no parallel list is kept.
    private IntFenwickTree? _lineTree;
    private int _maxLineLen = -1;

    // Background pre-build support: PreBuildLineIndex() scans the buffer on a
    // background thread and stores the result here.  Edits that land before
    // the install are queued in _deferredEdits and replayed on the UI thread
    // when InstallPendingLineTree() runs.
    private volatile ConcurrentQueue<DeferredEdit>? _deferredEdits;
    private IntFenwickTree? _pendingLineTree;

    private readonly record struct DeferredEdit(
        bool IsInsert, CharOffset Ofs, string? Text, long DeleteLen, bool HasNewline);

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
            if (_lineTree != null) return _lineTree.Count;
            if (_pendingLineTree != null) {
                InstallPendingLineTree();
                if (_lineTree != null) return _lineTree.Count;
            }
            // Background build still running — use buffer's approximate count.
            if (_deferredEdits != null && _buf.LineCount >= 0) return _buf.LineCount;
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
        } else if (affectedLine >= 0 && hasNewlines) {
            // Newlines inserted: splice line lengths array, O(L) rebuild.
            SpliceInsertLines(affectedLine, lineStart, oldLineLen, ofs, text);
        } else if (_deferredEdits is { } insertQ) {
            // Background build in progress — queue for replay.
            insertQ.Enqueue(new DeferredEdit(true, ofs, text, 0, hasNewlines));
        } else {
            // Tree not yet built: will be built lazily when needed.
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

        // Pre-mutation: scan deleted range for newlines to decide update strategy.
        var hasNewline = false;
        LineIndex startLine = -1;
        LineIndex endLine = -1;
        if (_lineTree != null) {
            // Scan deleted range for newline characters.
            VisitPieces(ofs, len, span => {
                if (!hasNewline && span.IndexOfAny('\n', '\r') >= 0) hasNewline = true;
            });
            startLine = (LineIndex)LineFromOfs(ofs);
            if (hasNewline) {
                // Use ofs+len (first surviving char after deletion) to find the
                // last line that participates in the merge.  Using ofs+len-1
                // would stay on startLine when deleting a line's own terminator.
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
            _maxLineLen = -1; // conservative: may have shortened the longest line
        } else if (startLine >= 0 && hasNewline) {
            // Newlines deleted: merge lines, O(L) rebuild.
            SpliceDeleteLines(startLine, endLine, ofs, len);
        } else if (_deferredEdits is { } deleteQ) {
            // Background build in progress — queue for replay.
            deleteQ.Enqueue(new DeferredEdit(false, ofs, null, len, hasNewline));
        } else {
            _lineTree = null;
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
    public string GetText(CharOffset start, int len) {
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
    public CharOffset LineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        if (IsOriginalContent) return _buf.GetLineStart(lineIdx);
        // Use the tree if available; otherwise fall back to the buffer's
        // approximate index during the background build window.
        if (_lineTree != null) {
            if (lineIdx >= _lineTree.Count) return -1L;
            return lineIdx == 0 ? 0 : _lineTree.PrefixSum((int)lineIdx - 1);
        }
        if (_pendingLineTree != null) {
            InstallPendingLineTree();
            if (_lineTree != null) {
                if (lineIdx >= _lineTree.Count) return -1L;
                return lineIdx == 0 ? 0 : _lineTree.PrefixSum((int)lineIdx - 1);
            }
        }
        // Background build still running — use buffer's approximate index.
        if (_deferredEdits != null && _buf.LineCount >= 0) {
            return _buf.GetLineStart(lineIdx);
        }
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
        if (IsOriginalContent && _buf.LineCount >= 0) {
            return BinarySearchBufferLines(_buf, ofs);
        }
        // Use the tree if available.
        if (_lineTree != null) {
            if (_lineTree.Count == 0) return 0;
            var idx = _lineTree.FindByPrefixSum(ofs + 1);  // ofs is char offset → long
            return idx >= 0 ? idx : _lineTree.Count - 1;
        }
        if (_pendingLineTree != null) {
            InstallPendingLineTree();
            if (_lineTree != null) {
                var idx = _lineTree.FindByPrefixSum(ofs + 1);
                return idx >= 0 ? idx : _lineTree.Count - 1;
            }
        }
        // Background build still running — use buffer's approximate index.
        if (_deferredEdits != null && _buf.LineCount >= 0) {
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

    /// <summary>
    /// Pre-builds the line index on a background thread.  Reads the buffer
    /// directly (thread-safe — the buffer is immutable after load).  Edits
    /// that arrive while the scan is running are queued in
    /// <see cref="_deferredEdits"/> and replayed when
    /// <see cref="InstallPendingLineTree"/> runs on the UI thread.
    /// </summary>
    public void PreBuildLineIndex() {
        if (_lineTree != null) return;
        _deferredEdits = new ConcurrentQueue<DeferredEdit>();

        // Scan buffer directly — does NOT touch the piece list,
        // so concurrent UI-thread edits to pieces are safe.
        var lengths = ScanBufferForLineLengths();
        var tree = IntFenwickTree.FromValues(CollectionsMarshal.AsSpan(lengths));

        _pendingLineTree = tree;
    }

    /// <summary>
    /// Installs the pre-built line tree, replaying any edits that were
    /// queued while the background scan was running.  Must be called on
    /// the UI thread.
    /// </summary>
    public void InstallPendingLineTree() {
        if (_pendingLineTree == null) return;

        // If the tree was already built lazily (because an edit triggered
        // a LineTree access before we got here), discard the pending tree.
        if (_lineTree != null) {
            _pendingLineTree = null;
            _deferredEdits = null;
            return;
        }

        var tree = _pendingLineTree;
        var queue = _deferredEdits;

        // Replay any edits that landed during the background scan.
        if (queue != null) {
            while (queue.TryDequeue(out var edit)) {
                ReplayDeferredEdit(tree, edit);
            }
        }

        _lineTree = tree;
        _pendingLineTree = null;
        _deferredEdits = null;
        _maxLineLen = -1;
        AssertLineTreeValid();
    }

    private IntFenwickTree LineTree {
        get {
            if (_lineTree != null) return _lineTree;
            // If a background build completed, install it now (UI thread).
            if (_pendingLineTree != null) {
                InstallPendingLineTree();
                if (_lineTree != null) return _lineTree;
            }
            BuildLineTree();
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

        _lineTree = IntFenwickTree.FromValues(CollectionsMarshal.AsSpan(lengths));
    }

    /// <summary>
    /// Scans the underlying buffer directly (without going through the
    /// piece list) to compute line lengths.  Thread-safe because the
    /// buffer is immutable after loading.
    /// </summary>
    private List<int> ScanBufferForLineLengths() {
        var bufLen = _buf.Length;
        var lengths = new List<int>();
        var currentLineLen = 0;
        var prevWasCr = false;

        if (bufLen == 0) {
            lengths.Add(0);
            return lengths;
        }

        const int chunkSize = 65536;
        var chars = new char[chunkSize];
        for (long pos = 0; pos < bufLen; pos += chunkSize) {
            var take = (int)Math.Min(chunkSize, bufLen - pos);
            _buf.CopyTo(pos, chars, take);
            var span = chars.AsSpan(0, take);

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
        }
        lengths.Add(currentLineLen);
        return lengths;
    }

    /// <summary>
    /// Replays a single deferred edit against the given line lengths and tree.
    /// Uses the same logic as the post-mutation sections of Insert/Delete.
    /// </summary>
    private static void ReplayDeferredEdit(IntFenwickTree tree, DeferredEdit edit) {
        if (edit.IsInsert) {
            var ofs = edit.Ofs;             // char offset → long
            var text = edit.Text!;
            var line = FindLineByPrefixSum(tree, ofs);
            if (!edit.HasNewline) {
                tree.Update(line, text.Length);
            } else {
                var lineStart = line == 0 ? 0L : tree.PrefixSum(line - 1);
                var oldLineLen = tree.ValueAt(line);
                var prefixLen = (int)(ofs - lineStart);
                var suffixLen = oldLineLen - prefixLen;

                var newLines = new List<int>();
                var cur = prefixLen;
                var prevCr = false;
                for (var i = 0; i < text.Length; i++) {
                    var ch = text[i];
                    if (prevCr) {
                        prevCr = false;
                        if (ch == '\n') { newLines[^1]++; continue; }
                    }
                    cur++;
                    if (ch == '\n') { newLines.Add(cur); cur = 0; }
                    else if (ch == '\r') { newLines.Add(cur); cur = 0; prevCr = true; }
                }
                cur += suffixLen;
                newLines.Add(cur);

                // Extract, splice, rebuild.
                var values = tree.ExtractValues();
                var spliced = new int[values.Length + newLines.Count - 1];
                values.AsSpan(0, line).CopyTo(spliced);
                CollectionsMarshal.AsSpan(newLines).CopyTo(spliced.AsSpan(line));
                values.AsSpan(line + 1).CopyTo(spliced.AsSpan(line + newLines.Count));
                tree.Rebuild(spliced);
            }
        } else {
            var ofs = edit.Ofs;
            var len = edit.DeleteLen;
            var startLine = FindLineByPrefixSum(tree, ofs);
            if (!edit.HasNewline) {
                tree.Update(startLine, (int)-len);
            } else {
                var totalLen = tree.TotalSum();
                var endLine = FindLineByPrefixSum(tree, Math.Min(ofs + len, totalLen));

                var startLineStart = startLine == 0 ? 0L : tree.PrefixSum(startLine - 1);
                var endLineStart = endLine == 0 ? 0L : tree.PrefixSum(endLine - 1);
                var endLineLen = tree.ValueAt(endLine);

                var mergedLen = (int)((ofs - startLineStart) +
                    (endLineLen - ((ofs + len) - endLineStart)));

                // Extract, merge, rebuild.
                var values = tree.ExtractValues();
                var removeCount = endLine - startLine + 1;
                var spliced = new int[values.Length - removeCount + 1];
                values.AsSpan(0, startLine).CopyTo(spliced);
                spliced[startLine] = mergedLen;
                values.AsSpan(endLine + 1).CopyTo(spliced.AsSpan(startLine + 1));
                tree.Rebuild(spliced);
            }
        }
    }

    /// <summary>
    /// Static helper: finds the 0-based line index for a character offset.
    /// <paramref name="ofs"/> is a CharOffset — must not be truncated to int.
    /// Returns a LineIndex.
    /// </summary>
    private static LineIndex FindLineByPrefixSum(IntFenwickTree tree, CharOffset ofs) {
        if (tree.Count == 0) return 0;
        var idx = tree.FindByPrefixSum(ofs + 1);  // target is char offset + 1 → long
        return idx >= 0 ? idx : tree.Count - 1;
    }

    /// <summary>
    /// Splices the line-lengths array after a newline-containing insert.
    /// The affected line is split into multiple new lines based on the
    /// newline positions in the inserted text.  O(L) tree rebuild.
    /// </summary>
    private void SpliceInsertLines(LineIndex line, CharOffset lineStart, int oldLineLen,
                                   CharOffset insertOfs, string text) {
        var prefixLen = (int)(insertOfs - lineStart);  // within-line offset → int
        var suffixLen = oldLineLen - prefixLen;

        var newLines = new List<int>();
        var cur = prefixLen;
        var prevCr = false;
        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (prevCr) {
                prevCr = false;
                if (ch == '\n') { newLines[^1]++; continue; }
            }
            cur++;
            if (ch == '\n') { newLines.Add(cur); cur = 0; }
            else if (ch == '\r') { newLines.Add(cur); cur = 0; prevCr = true; }
        }
        cur += suffixLen;
        newLines.Add(cur);

        // Extract, splice, rebuild.
        var values = _lineTree!.ExtractValues();
        var spliced = new int[values.Length + newLines.Count - 1];
        values.AsSpan(0, line).CopyTo(spliced);
        CollectionsMarshal.AsSpan(newLines).CopyTo(spliced.AsSpan(line));
        values.AsSpan(line + 1).CopyTo(spliced.AsSpan(line + newLines.Count));
        _lineTree.Rebuild(spliced);
        _maxLineLen = -1;
    }

    /// <summary>
    /// Splices the line-lengths array after a newline-crossing delete.
    /// Lines startLine..endLine merge into a single line.  O(L) tree rebuild.
    /// </summary>
    private void SpliceDeleteLines(LineIndex startLine, LineIndex endLine, CharOffset deleteOfs, long deleteLen) {
        var startLineStart = startLine == 0 ? 0L : _lineTree!.PrefixSum(startLine - 1);
        var endLineStart = endLine == 0 ? 0L : _lineTree!.PrefixSum(endLine - 1);
        var endLineLen = _lineTree!.ValueAt(endLine);

        var mergedLen = (int)((deleteOfs - startLineStart) +
            (endLineLen - ((deleteOfs + deleteLen) - endLineStart)));

        // Extract, merge, rebuild.
        var values = _lineTree.ExtractValues();
        var removeCount = endLine - startLine + 1;
        var spliced = new int[values.Length - removeCount + 1];
        values.AsSpan(0, startLine).CopyTo(spliced);
        spliced[startLine] = mergedLen;
        values.AsSpan(endLine + 1).CopyTo(spliced.AsSpan(startLine + 1));
        _lineTree.Rebuild(spliced);
        _maxLineLen = -1;
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
    /// Debug-only validation: checks that the IntFenwickTree's total sum matches
    /// the document Length.  Catches tree corruption from incremental update bugs.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertLineTreeValid() {
        if (_lineTree == null) return;
        var docLen = Length;
        var treeTotal = _lineTree.TotalSum();
        Debug.Assert(treeTotal == docLen,
            $"Line tree total {treeTotal} != Length {docLen} (delta {treeTotal - docLen})");
    }
}
