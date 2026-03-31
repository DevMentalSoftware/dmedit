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

    private readonly List<IBuffer> _buffers = new();
    private ChunkedUtf8Buffer _addBuf = new();
    private int _addBufIdx;
    private bool _initialPieceResolved;

    private readonly List<Piece> _pieces = new();

    // LineIndexTree-based line index: stores line lengths (including terminators).
    // Provides O(log L) prefix-sum queries for LineStartOfs/LineFromOfs and
    // O(log L) incremental updates for non-newline edits.  Individual values
    // are derived from the tree via ValueAt() — no parallel list is kept.
    // Lines longer than MaxPseudoLine are split into pseudo-lines so the
    // layout engine never sees a single enormous line.
    private LineIndexTree? _lineTree;
    private int _maxLineLen = -1;

    // Reserved for future per-line terminator overrides from edits.
    private Dictionary<int, LineTerminatorType>? _terminatorOverrides;

    /// <summary>
    /// Maximum content characters per pseudo-line.  Set from AppSettings
    /// at startup.  
    /// </summary>
    public static int MaxPseudoLine { get; set; } = 500;

    // Instance override for tests. -1 means use the static.
    private readonly int _maxPseudoLine = -1;
    internal int EffectiveMaxPseudoLine => _maxPseudoLine > 0 ? _maxPseudoLine : MaxPseudoLine;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Creates an empty piece-table (for untitled/new documents).</summary>
    public PieceTable(int maxPseudoLine = -1) : this(EmptyBuffer.Instance, maxPseudoLine) {
        BuildLineTree();
    }

    /// <summary>Constructs a piece-table from an in-memory string (tests, small files).</summary>
    internal PieceTable(string originalContent, int maxPseudoLine = -1)
        : this(new StringBuffer(originalContent), maxPseudoLine) {
        BuildLineTree();
    }

    /// <summary>
    /// Constructs a piece-table from an <see cref="IBuffer"/>.
    /// The piece-table does NOT take ownership of the buffer; the caller is responsible
    /// for disposing it after the piece-table is no longer needed.
    /// </summary>
    public PieceTable(IBuffer buf, int maxPseudoLine = -1) {
        _maxPseudoLine = maxPseudoLine;
        _buffers.Add(buf);          // index 0 = original buffer
        _buffers.Add(_addBuf);      // index 1 = active add buffer
        _addBufIdx = 1;
        // Create the initial piece when the buffer's length is already known.
        // For paged/streaming buffers (LengthIsKnown == false), the piece is
        // deferred until EnsureInitialPiece is called lazily on first access.
        if (buf.LengthIsKnown) {
            if (buf.Length > 0) {
                _pieces.Add(new Piece(0, 0, buf.Length));
            }
            _initialPieceResolved = true;
        }
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    /// <summary>The original file buffer (index 0).</summary>
    public IBuffer Buffer => _buffers[0];

    /// <summary>The active in-memory add buffer.</summary>
    public ChunkedUtf8Buffer AddBuffer => _addBuf;

    /// <summary>The buffer list, exposed for session persistence.</summary>
    public IReadOnlyList<IBuffer> Buffers => _buffers;

    /// <summary>Index of the active add buffer in the buffer list.</summary>
    public int AddBufferIndex => _addBufIdx;

    /// <summary>
    /// Replaces the active add buffer (used by session restore to load
    /// a previously persisted buffer before replaying edits).
    /// </summary>
    public void SetAddBuffer(ChunkedUtf8Buffer buffer) {
        _addBuf = buffer;
        _buffers[_addBufIdx] = buffer;
    }

    /// <summary>
    /// Adds a buffer to the buffer list and returns its index.
    /// Used for loading persisted add buffers or adding large paste buffers.
    /// </summary>
    public int RegisterBuffer(IBuffer buffer) {
        _buffers.Add(buffer);
        return _buffers.Count - 1;
    }

    /// <summary>
    /// <c>true</c> when the document content is the unedited original buffer
    /// (no inserts or deletes have been applied).
    /// </summary>
    public bool IsOriginalContent =>
        _pieces.Count == 1 && _pieces[0].BufIdx == 0;

    /// <summary>Total number of buffer characters in the document.</summary>
    public long Length {
        get {
            EnsureInitialPiece();
            var total = 0L;
            foreach (var p in _pieces) {
                total += p.Len;
            }
            return total;
        }
    }

    /// <summary>
    /// Total document length in doc-space (includes virtual pseudo-terminators).
    /// Equals <see cref="Length"/> when the document has no pseudo-lines.
    /// </summary>
    public DocOffset DocLength => LineTree.DocTotalSum();

    /// <summary>
    /// Number of logical lines (always at least 1).
    /// </summary>
    public long LineCount {
        get {
            if (_lineTree != null) return _lineTree.Count;
            return LineTree.Count;
        }
    }

    /// <summary>
    /// Length of the longest logical line in the document (including its newline
    /// terminator), or <c>-1</c> when not available.
    /// </summary>
    public long MaxLineLength {
        get {
            // Before the line tree is built (e.g. during streaming load),
            // use the buffer's pre-computed value clamped to MaxPseudoLine.
            if (_maxLineLen < 0 && _lineTree == null) {
                var bufMax = Buffer.LongestLine;
                if (bufMax >= 0) {
                    var mpl = EffectiveMaxPseudoLine;
                    return Math.Min(bufMax, mpl + 1);
                }
                return -1;
            }
            if (_maxLineLen < 0) {
                // Tree is built but _maxLineLen was invalidated — recompute.
                // Use doc-space values so pseudo-lines include the virtual
                // terminator, matching how real lines include their real
                // terminator (LF/CRLF) for horizontal extent calculation.
                var tree = _lineTree!;
                var max = 0;
                for (var i = 0; i < tree.Count; i++) {
                    var v = tree.DocValueAt(i);
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
    public void InsertPieces(BufOffset ofs, Piece[] pieces) {
        if (pieces.Length == 0) return;

        ArgumentOutOfRangeException.ThrowIfNegative(ofs);

        var (pieceIdx, ofsInPiece) = FindPiece(ofs);

        if (ofsInPiece == 0L) {
            _pieces.InsertRange(pieceIdx, pieces);
        } else {
            var existing = _pieces[pieceIdx];
            var left = existing.TakeFirst(ofsInPiece);
            var right = existing.SkipFirst(ofsInPiece);
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
        BufOffset ofs, Piece[] pieces,
        int lineInfoStart, int[]? lineInfoLengths, int[]? docLineInfoLengths, long len) {

        InsertPieces(ofs, pieces);
        if (lineInfoLengths != null) {
            RestoreLines(lineInfoStart, lineInfoLengths,
                docLineInfoLengths ?? lineInfoLengths);
        } else {
            ReinsertedNonNewlineChars(ofs, len);
        }
    }

    /// <summary>Inserts <paramref name="text"/> at logical offset <paramref name="ofs"/>.</summary>
    public void Insert(BufOffset ofs, string text) {
        if (text.Length == 0) {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        // Only check upper bound when Length is fully known.
        if (Buffer.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        // Pre-mutation: capture line info for incremental tree update.
        var hasNewlines = text.AsSpan().IndexOfAny('\n', '\r') >= 0;
        LineIndex affectedLine = -1;
        BufOffset lineStart = 0;
        int oldLineLen = 0;
        if (_lineTree != null) {
            affectedLine = (LineIndex)LineFromOfs(ofs);
            lineStart = affectedLine == 0 ? 0 : _lineTree.PrefixSum(affectedLine - 1);
            oldLineLen = _lineTree.ValueAt(affectedLine);
        }

        var addStart = _addBuf.Append(text);
        var newPiece = new Piece(_addBufIdx, addStart, text.Length);

        var (pieceIdx, ofsInPiece) = FindPiece(ofs);

        if (ofsInPiece == 0L) {
            _pieces.Insert(pieceIdx, newPiece);
        } else {
            var existing = _pieces[pieceIdx];
            var left = existing.TakeFirst(ofsInPiece);
            var right = existing.SkipFirst(ofsInPiece);
            _pieces[pieceIdx] = left;
            _pieces.Insert(pieceIdx + 1, newPiece);
            _pieces.Insert(pieceIdx + 2, right);
        }

        // Post-mutation: update line tree.
        if (affectedLine >= 0 && !hasNewlines) {
            // No newlines, tree exists: O(log L) incremental update.
            _lineTree!.Update(affectedLine, text.Length);
            var newLen = _lineTree.DocValueAt(affectedLine);
            if (newLen > _maxLineLen)
                _maxLineLen = newLen;
            SplitLongLine(affectedLine);
        } else if (affectedLine >= 0 && hasNewlines) {
            // Newlines inserted: splice line lengths via chunked buffer read.
            SpliceInsertLines(affectedLine, lineStart, oldLineLen, ofs,
                _addBuf, addStart, text.Length);
            var nlCount = (int)(LineFromOfs(Math.Min(ofs + text.Length, Length)) - affectedLine + 1);
            SplitLongLines(affectedLine, nlCount);
        } else {
            Debug.Assert(false, "Insert called without a line tree.");
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
    }

    /// <summary>Deletes <paramref name="len"/> characters starting at logical offset <paramref name="ofs"/>.</summary>
    public void Delete(BufOffset ofs, long len) {
        if (len == 0) {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (Buffer.LengthIsKnown) {
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
            rightRemainder = _pieces[endPiece].SkipFirst(endOfsInPiece);
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
            var chars = new char[(int)p.Len];
            _buffers[p.BufIdx].CopyTo(p.Start, chars, (int)p.Len);
            sb.Append(chars);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maximum length allowed for <see cref="GetText"/>.  Guards against
    /// accidental materialization of multi-GB documents into a single string.
    /// Code that needs larger ranges should use <see cref="ForEachPiece"/>.
    /// </summary>
    public int MaxGetTextLength => EffectiveMaxPseudoLine + 2; // +2 for \r\n terminator

    /// <summary>Returns a substring of the document.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="len"/> exceeds <see cref="MaxGetTextLength"/>.
    /// </exception>
    public string GetText(BufOffset start, int len) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(len);
        if (len > MaxGetTextLength) {
            if (System.Diagnostics.Debugger.IsAttached)
                System.Diagnostics.Debugger.Break();
            ArgumentOutOfRangeException.ThrowIfGreaterThan(len, MaxGetTextLength,
                $"GetText len ({len}) exceeds MaxGetTextLength ({MaxGetTextLength}). " +
                "Use ForEachPiece for large ranges.");
        }
        if (Buffer.LengthIsKnown) {
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

    /// <summary>
    /// Copies [<paramref name="start"/>, <paramref name="start"/>+<paramref name="len"/>)
    /// directly into <paramref name="dest"/>. The span must be at least <paramref name="len"/>
    /// characters long. No managed string is allocated.
    /// </summary>
    public void CopyTo(long start, long len, Span<char> dest) {
        if (len == 0) return;
        var (startPiece, startOfsInPiece) = FindPiece(start);
        var remaining = len;
        var destOfs = 0;
        for (var i = startPiece; i < _pieces.Count && remaining > 0; i++) {
            var p = _pieces[i];
            var pieceOfs = i == startPiece ? startOfsInPiece : 0L;
            var buf = _buffers[p.BufIdx];
            var avail = p.Len - pieceOfs;
            var pieceRemaining = Math.Min(avail, remaining);
            while (pieceRemaining > 0) {
                var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                buf.CopyTo(p.Start + pieceOfs, dest.Slice(destOfs, take), take);
                destOfs += take;
                pieceOfs += take;
                pieceRemaining -= take;
                remaining -= take;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Line access
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the logical character offset at which line <paramref name="lineIdx"/> begins,
    /// or <c>-1</c> if the line start is not yet available (buffer still loading).
    /// </summary>
    public BufOffset LineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        if (_lineTree != null) {
            if (lineIdx >= _lineTree.Count) return -1L;
            return lineIdx == 0 ? 0 : _lineTree.PrefixSum((int)lineIdx - 1);
        }
        var tree = LineTree;
        if (lineIdx >= tree.Count) return -1L;
        return lineIdx == 0 ? 0 : tree.PrefixSum((int)lineIdx - 1);
    }

    /// <summary>Returns the zero-based line index that contains logical offset <paramref name="ofs"/>.</summary>
    public long LineFromOfs(BufOffset ofs) {
        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        if (Buffer.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }
        var tree = LineTree;
        if (tree.Count == 0) return 0;
        var idx = tree.FindByPrefixSum(ofs + 1);
        return idx >= 0 ? idx : tree.Count - 1;
    }

    /// <summary>
    /// Returns the terminator type of the given line.
    /// Checks override map first, then RLE baseline, then derives from characters.
    /// </summary>
    public LineTerminatorType GetLineTerminator(int lineIdx) {
        // Override from edits?
        if (_terminatorOverrides != null
            && _terminatorOverrides.TryGetValue(lineIdx, out var ovr)) {
            return ovr;
        }
        // Derive from the last 1-2 characters of the line.
        return DeriveTerminatorType(lineIdx);
    }

    /// <summary>
    /// Determines the terminator type by reading the last 1–2 characters of a line.
    /// Used as fallback when no RLE baseline or override is available.
    /// </summary>
    private LineTerminatorType DeriveTerminatorType(int lineIdx) {
        var tree = LineTree;
        if (lineIdx >= tree.Count) return LineTerminatorType.None;
        var fullLen = tree.ValueAt(lineIdx);
        if (fullLen == 0) return LineTerminatorType.None;

        var lineStart = lineIdx == 0 ? 0L : tree.PrefixSum(lineIdx - 1);

        // Last line with no terminator?
        if (lineIdx + 1 >= tree.Count) return LineTerminatorType.None;

        // Check if this is a pseudo-split (no newline chars at boundary).
        var lastCharOfs = lineStart + fullLen - 1;
        if (lastCharOfs >= Length) return LineTerminatorType.None;
        var lastChar = CharAt(lastCharOfs);
        if (lastChar == '\n') {
            if (fullLen >= 2) {
                var prevChar = CharAt(lastCharOfs - 1);
                if (prevChar == '\r') return LineTerminatorType.CRLF;
            }
            return LineTerminatorType.LF;
        }
        if (lastChar == '\r') return LineTerminatorType.CR;
        return LineTerminatorType.Pseudo;
    }

    /// <summary>
    /// Returns the content length of a line (excluding the dead zone / terminator).
    /// </summary>
    public int LineContentLength(int lineIdx) {
        var tree = LineTree;
        if (lineIdx < 0 || lineIdx >= tree.Count) return 0;
        var fullLen = tree.ValueAt(lineIdx);
        return fullLen - GetLineTerminator(lineIdx).DeadZoneWidth();
    }

    // -------------------------------------------------------------------------
    // Doc-space line access (for navigation / caret positioning)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the doc-space offset at which line <paramref name="lineIdx"/> begins.
    /// Pseudo-lines have a virtual 1-char gap between them, so doc-space offsets
    /// are greater than or equal to buf-space offsets.
    /// </summary>
    public DocOffset DocLineStartOfs(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        var tree = LineTree; // forces build if not yet built
        if (lineIdx >= tree.Count) return -1L;
        return lineIdx == 0 ? 0 : tree.DocPrefixSum((int)lineIdx - 1);
    }

    /// <summary>
    /// Returns the zero-based line index that contains doc-space offset
    /// <paramref name="docOfs"/>.
    /// </summary>
    public long LineFromDocOfs(DocOffset docOfs) {
        ArgumentOutOfRangeException.ThrowIfNegative(docOfs);
        var tree = LineTree;
        if (tree.Count == 0) return 0;
        var idx = tree.FindByDocPrefixSum(docOfs + 1);
        return idx >= 0 ? idx : tree.Count - 1;
    }

    /// <summary>
    /// Translates a doc-space offset to a buf-space offset.  O(log n).
    /// Within a line's content, positions map 1:1.  A position on a
    /// pseudo-line's virtual terminator maps to the buf position just
    /// past the line's content (i.e. the start of the next pseudo-line
    /// in buf-space).
    /// </summary>
    public BufOffset DocOfsToBufOfs(DocOffset docOfs) {
        var tree = LineTree; // forces build — throws implicitly if tree can't be built
        if (tree.Count == 0) return docOfs;
        if (docOfs <= 0) return 0;
        var docTotal = tree.DocTotalSum();
        if (docOfs >= docTotal) return Length;
        var line = (int)LineFromDocOfs(docOfs);
        var docLineStart = line == 0 ? 0L : tree.DocPrefixSum(line - 1);
        var bufLineStart = line == 0 ? 0L : tree.PrefixSum(line - 1);
        var localOfs = docOfs - docLineStart;
        var bufLineLen = tree.ValueAt(line);
        return bufLineStart + Math.Min(localOfs, bufLineLen);
    }

    /// <summary>
    /// Translates a buf-space offset to a doc-space offset.  O(log n).
    /// </summary>
    public DocOffset BufOfsToDocOfs(BufOffset bufOfs) {
        var tree = LineTree; // forces build
        if (tree.Count == 0) return bufOfs;
        if (bufOfs <= 0) return 0;
        var bufTotal = tree.TotalSum();
        if (bufOfs >= bufTotal) return DocLength;
        var line = (int)LineFromOfs(bufOfs);
        var bufLineStart = line == 0 ? 0L : tree.PrefixSum(line - 1);
        var docLineStart = line == 0 ? 0L : tree.DocPrefixSum(line - 1);
        var localOfs = bufOfs - bufLineStart;
        return docLineStart + localOfs;
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

    /// <summary>
    /// Finds the piece and within-piece offset for a given logical document offset.
    /// Returns (pieces.Count, 0) when ofs == Length (i.e., end-of-document).
    /// </summary>
    private (int pieceIdx, long ofsInPiece) FindPiece(long ofs) {
        EnsureInitialPiece();
        var remaining = ofs;
        for (var i = 0; i < _pieces.Count; i++) {
            if (remaining < _pieces[i].Len) {
                return (i, remaining);
            }
            remaining -= _pieces[i].Len;
        }
        return (_pieces.Count, 0L); // ofs == Length
    }

    /// <summary>
    /// Lazily creates the initial piece for the original buffer when its
    /// length was not known at construction time (paged, streaming, or
    /// procedural buffers).
    /// </summary>
    private void EnsureInitialPiece() {
        if (!_initialPieceResolved) {
            _initialPieceResolved = true;
            var len = Buffer.Length;
            if (len > 0) {
                _pieces.Add(new Piece(0, 0, len));
            }
        }
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
            var buf = _buffers[p.BufIdx];
            var avail = p.Len - pieceOfs;
            var pieceRemaining = Math.Min(avail, remaining);
            while (pieceRemaining > 0) {
                var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                var chars = new char[take];
                buf.CopyTo(p.Start + pieceOfs, chars, take);
                visitor(chars);
                pieceOfs += take;
                pieceRemaining -= take;
                remaining -= take;
            }
        }
    }

    /// <summary>
    /// Installs a pre-built line tree from externally computed line lengths
    /// (e.g. from PagedFileBuffer's scan).  Call on the UI thread before
    /// unblocking input.
    /// </summary>
    public void InstallLineTree(ReadOnlySpan<int> lineLengths) {
        EnsureInitialPiece();

        // During streaming/paged loads, EnsureInitialPiece may have been
        // called while the buffer scan was still in progress, capturing a
        // partial _totalChars.  Now that the scan is complete, reconcile
        // the initial piece to match the final buffer length.
        var bufLen = Buffer.Length;
        if (_pieces.Count == 0) {
            if (bufLen > 0) _pieces.Add(new Piece(0, 0, bufLen));
        } else if (_pieces.Count == 1 && _pieces[0].BufIdx == 0
                   && _pieces[0].Len != bufLen) {
            _pieces[0] = new Piece(0, 0, bufLen);
        }

        // Check if any entry exceeds EffectiveMaxPseudoLine and needs splitting.
        var mpl = EffectiveMaxPseudoLine;
        var needsSplit = false;
        foreach (var len in lineLengths) {
            if (len > mpl) { needsSplit = true; break; }
        }

        if (needsSplit) {
            // Expand into mutable lists, splitting long entries.
            // Input lineLengths are buf-space (from PagedFileBuffer scan).
            var bufExpanded = new List<int>(lineLengths.Length);
            var docExpanded = new List<int>(lineLengths.Length);
            foreach (var len in lineLengths) {
                if (len <= mpl) {
                    bufExpanded.Add(len);
                    docExpanded.Add(len);  // no pseudo split: doc == buf
                } else {
                    // Determine content vs terminator in the original line.
                    // The last 1-2 chars may be a real terminator.
                    // Since these are buf-space values from the initial scan,
                    // and the buffer hasn't been edited yet, we rely on the
                    // fact that the split preserves terminators on the last chunk.
                    var remaining = len;
                    while (remaining > mpl) {
                        bufExpanded.Add(mpl);
                        docExpanded.Add(mpl + 1);  // pseudo-line: doc = buf + 1
                        remaining -= mpl;
                    }
                    if (remaining > 0) {
                        bufExpanded.Add(remaining);
                        docExpanded.Add(remaining);  // last chunk: real term, doc == buf
                    }
                }
            }
            _lineTree = LineIndexTree.FromValues(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bufExpanded),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(docExpanded));
            _maxLineLen = mpl + 1; // pseudo-lines have docLen = mpl + 1
        } else {
            // No pseudo-lines needed: doc == buf for all lines.
            _lineTree = LineIndexTree.FromValues(lineLengths, lineLengths);
            var max = 0;
            foreach (var len in lineLengths) {
                if (len > max) max = len;
            }
            _maxLineLen = max;
        }
        AssertLineTreeValid();
    }

    /// <summary>
    /// Installs a pre-split dual-value line tree from previously captured
    /// buf-space and doc-space lengths.  Used by undo to restore a tree
    /// that already has pseudo-lines split (no re-splitting needed).
    /// </summary>
    public void InstallLineTree(ReadOnlySpan<int> bufLengths, ReadOnlySpan<int> docLengths) {
        EnsureInitialPiece();

        var bufLen = Buffer.Length;
        if (_pieces.Count == 0) {
            if (bufLen > 0) _pieces.Add(new Piece(0, 0, bufLen));
        } else if (_pieces.Count == 1 && _pieces[0].BufIdx == 0
                   && _pieces[0].Len != bufLen) {
            _pieces[0] = new Piece(0, 0, bufLen);
        }

        _lineTree = LineIndexTree.FromValues(bufLengths, docLengths);
        var max = 0;
        foreach (var len in docLengths) {
            if (len > max) max = len;
        }
        _maxLineLen = max;
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
            Debug.Assert(_maxLineLen <= EffectiveMaxPseudoLine + 1,
                $"BuildLineTree produced _maxLineLen={_maxLineLen} > MaxPseudoLine+1={EffectiveMaxPseudoLine + 1}");
            return _lineTree!;
        }
    }

    private void BuildLineTree() {
        var scanner = new LineScanner(EffectiveMaxPseudoLine);
        var totalLen = Length;
        if (totalLen > 0) {
            VisitPieces(0, totalLen, span => scanner.Scan(span));
        }
        scanner.Finish();

        _lineTree = scanner.BuildTree();


        var maxLen = 0;
        foreach (var l in scanner.DocLineLengths) {
            if (l > maxLen) maxLen = l;
        }
        _maxLineLen = maxLen;

        // The scanner already handles pseudo-splits, but content + terminator
        // may still exceed MaxPseudoLine.  Verify and split if needed.
        if (_maxLineLen > EffectiveMaxPseudoLine + 1) { // +1: doc-space pseudo = mpl+1
            SplitLongLines(0, _lineTree.Count);
        }
    }


    /// <summary>
    /// Static helper: finds the 0-based line index for a character offset.
    /// <paramref name="ofs"/> is a BufOffset — must not be truncated to int.
    /// Returns a LineIndex.
    /// </summary>
    private static LineIndex FindLineByPrefixSum(LineIndexTree tree, BufOffset ofs) {
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
    /// <summary>
    /// Splices the line tree after a newline-containing insert. Scans the
    /// inserted text (via IBuffer) for newlines to compute line lengths.
    /// Uses chunked reads to avoid materializing the full insert as a string.
    /// </summary>
    private void SpliceInsertLines(LineIndex line, BufOffset lineStart, int oldLineLen,
                                   BufOffset insertOfs, IBuffer buf, long bufStart, int textLen) {
        var prefixLen = (int)(insertOfs - lineStart);
        var suffixLen = oldLineLen - prefixLen;

        // Scan the inserted text in chunks, building line lengths incrementally.
        var newLines = new List<int>();
        var cur = prefixLen;
        var prevCr = false;
        var scanBuf = new char[Math.Min(textLen, MaxVisitChunk)];
        var scanned = 0;
        while (scanned < textLen) {
            var take = Math.Min(textLen - scanned, scanBuf.Length);
            buf.CopyTo(bufStart + scanned, scanBuf, take);
            for (var i = 0; i < take; i++) {
                var ch = scanBuf[i];
                if (prevCr) {
                    prevCr = false;
                    if (ch == '\n') { newLines[^1]++; continue; }
                }
                cur++;
                if (ch == '\n') { newLines.Add(cur); cur = 0; } else if (ch == '\r') { newLines.Add(cur); cur = 0; prevCr = true; }
            }
            scanned += take;
        }
        cur += suffixLen;
        newLines.Add(cur);

        // Replace the single line with the new lines.
        // Real-newline splits: doc == buf for all entries.
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(newLines);
        _lineTree!.RemoveAt(line);
        _lineTree.InsertRange(line, span, span);
        if (_maxLineLen >= 0) {
            foreach (var l in newLines) {
                if (l > _maxLineLen) _maxLineLen = l;
            }
        }
    }

    /// <summary>
    /// Splices the line tree after a newline-crossing delete.
    /// Lines startLine..endLine merge into a single line.
    /// O(k log L) via treap RemoveRange/InsertAt.
    /// </summary>
    private void SpliceDeleteLines(LineIndex startLine, LineIndex endLine, BufOffset deleteOfs, long deleteLen) {
        var startLineStart = startLine == 0 ? 0L : _lineTree!.PrefixSum(startLine - 1);
        var endLineStart = endLine == 0 ? 0L : _lineTree!.PrefixSum(endLine - 1);
        var endLineLen = _lineTree!.ValueAt(endLine);

        var mergedLen = (int)((deleteOfs - startLineStart) +
            (endLineLen - ((deleteOfs + deleteLen) - endLineStart)));

        _lineTree.RemoveRange(startLine, endLine - startLine + 1);
        _lineTree.InsertAt(startLine, mergedLen, mergedLen);  // real merge: doc == buf
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
        var fullLen = _lineTree!.ValueAt(lineIdx);
        // Determine how much of the line is content vs terminator.
        var deadZone = DeriveTerminatorType(lineIdx).DeadZoneWidth();
        var contentLen = fullLen - deadZone;
        var mpl = EffectiveMaxPseudoLine;
        if (contentLen <= mpl) return;

        // Split the content into MaxPseudoLine chunks; the terminator
        // stays attached to the last chunk.
        var chunkCount = (contentLen + mpl - 1) / mpl;
        Span<int> bufChunks = chunkCount <= 64
            ? stackalloc int[chunkCount]
            : new int[chunkCount];
        Span<int> docChunks = chunkCount <= 64
            ? stackalloc int[chunkCount]
            : new int[chunkCount];
        var remaining = contentLen;
        for (var i = 0; i < chunkCount; i++) {
            var chunk = Math.Min(mpl, remaining);
            bufChunks[i] = chunk;
            docChunks[i] = chunk + 1; // pseudo-line: doc gets virtual terminator
            remaining -= chunk;
        }
        // Last chunk keeps the real terminator, not a pseudo boundary.
        bufChunks[chunkCount - 1] += deadZone;
        docChunks[chunkCount - 1] = bufChunks[chunkCount - 1]; // real term: doc == buf

        _lineTree.RemoveAt(lineIdx);
        _lineTree.InsertRange(lineIdx, bufChunks, docChunks);
        if (_maxLineLen >= 0 && _maxLineLen > mpl) {
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
    public (int StartLine, int[] LineLengths, int[] DocLineLengths)? CaptureLineInfo(BufOffset ofs, long len) {
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
        var docLengths = new int[count];
        for (var i = 0; i < count; i++) {
            lengths[i] = _lineTree.ValueAt(startLine + i);
            docLengths[i] = _lineTree.DocValueAt(startLine + i);
        }
        return (startLine, lengths, docLengths);
    }

    /// <summary>
    /// Restores line tree entries that were removed by a previous deletion.
    /// Called during undo to reverse <see cref="SpliceDeleteLines"/>.
    /// The merged line at <paramref name="startLine"/> is replaced with the
    /// original lines.
    /// </summary>
    public void RestoreLines(int startLine, int[] lineLengths, int[] docLineLengths) {

        Debug.Assert(_lineTree != null, "RestoreLines called without a line tree.");
        if (_lineTree == null) return; // Release safety net
        // Remove the merged line that SpliceDeleteLines created.
        _lineTree.RemoveAt(startLine);
        // Re-insert the original lines.
        _lineTree.InsertRange(startLine, lineLengths, docLineLengths);
        _maxLineLen = -1; // conservative recompute
        SplitLongLines(startLine, lineLengths.Length);
    }

    /// <summary>
    /// Updates the line tree after re-inserting non-newline characters at
    /// <paramref name="ofs"/>.  Used by undo for single-line deletes where
    /// the line just got shorter and needs its length restored.
    /// </summary>
    public void ReinsertedNonNewlineChars(BufOffset ofs, long len) {
        Debug.Assert(_lineTree != null, "ReinsertedNonNewlineChars called without a line tree.");
        if (_lineTree == null) return; // Release safety net
        var line = (int)LineFromOfs(ofs);
        _lineTree.Update(line, (int)len);
        var newLen = _lineTree.DocValueAt(line);
        if (newLen > _maxLineLen) _maxLineLen = newLen;
        SplitLongLine(line);
    }

    /// <summary>
    /// Captures the piece descriptors covering [ofs, ofs+len) so the text can
    /// be reconstructed later without reading from the buffer now.  Both the
    /// Original buffer and the Add buffer are immutable/append-only, so the
    /// returned pieces remain valid indefinitely.
    /// </summary>
    public Piece[] CapturePieces(BufOffset ofs, long len) {
        if (len == 0) return [];
        var (startPiece, startOfsInPiece) = FindPiece(ofs);
        var endOfs = ofs + len;
        var (endPiece, endOfsInPiece) = FindPiece(endOfs);

        var result = new List<Piece>();
        for (var i = startPiece; i <= endPiece && i < _pieces.Count; i++) {
            var p = _pieces[i];
            var skip = (i == startPiece) ? startOfsInPiece : 0L;
            var limit = (i == endPiece) ? endOfsInPiece : p.Len;
            var take = limit - skip;
            if (take <= 0) continue;
            result.Add(new Piece(p.BufIdx, p.Start + skip, take));
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
            var buf = _buffers[p.BufIdx];
            var ofs = p.Start;
            var pieceRemaining = p.Len;
            while (pieceRemaining > 0) {
                var take = (int)Math.Min(pieceRemaining, MaxVisitChunk);
                var chars = new char[take];
                buf.CopyTo(ofs, chars, take);
                sb.Append(chars);
                ofs += take;
                pieceRemaining -= take;
            }
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Bulk replace
    // -------------------------------------------------------------------------

    /// <summary>Current length of the append-only add buffer (in chars).</summary>
    public long AddBufferLength => _addBuf.CharLength;

    /// <summary>
    /// Returns a substring from the add buffer. Used by serialization to
    /// materialize text from a <see cref="History.SpanInsertEdit"/>.
    /// </summary>
    public string GetAddBufferSlice(long start, int len) =>
        _addBuf.GetSlice(start, len);

    /// <summary>
    /// Appends <paramref name="text"/> to the add buffer without creating a piece.
    /// Returns the start offset within the add buffer. Use with
    /// <see cref="InsertFromAddBuffer"/> to complete the insert.
    /// </summary>
    public long AppendToAddBuffer(ReadOnlySpan<char> text) =>
        _addBuf.Append(text);

    /// <summary>
    /// Inserts a piece referencing [<paramref name="bufStart"/>,
    /// <paramref name="bufStart"/>+<paramref name="len"/>) from the add buffer
    /// into the piece list at logical offset <paramref name="ofs"/>.
    /// </summary>
    public void InsertFromAddBuffer(BufOffset ofs, long bufStart, int len) =>
        InsertFromBuffer(ofs, _addBufIdx, bufStart, len);

    /// <summary>
    /// Inserts a piece referencing [<paramref name="bufStart"/>,
    /// <paramref name="bufStart"/>+<paramref name="len"/>) from buffer
    /// <paramref name="bufIdx"/> into the piece list at logical offset
    /// <paramref name="ofs"/>, and updates the line tree.
    /// </summary>
    public void InsertFromBuffer(BufOffset ofs, int bufIdx, long bufStart, int len) {
        if (len == 0) return;

        ArgumentOutOfRangeException.ThrowIfNegative(ofs);
        if (Buffer.LengthIsKnown) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(ofs, Length);
        }

        var buf = _buffers[bufIdx];

        // Pre-mutation: check for newlines using the target buffer.
        var hasNewlines = false;
        // Scan for newlines by reading a small chunk from the buffer.
        {
            var scanBuf = new char[Math.Min(len, MaxVisitChunk)];
            var scanned = 0;
            var scanOfs = bufStart;
            while (scanned < len && !hasNewlines) {
                var take = Math.Min(len - scanned, scanBuf.Length);
                buf.CopyTo(scanOfs, scanBuf, take);
                if (scanBuf.AsSpan(0, take).IndexOfAny('\n', '\r') >= 0) {
                    hasNewlines = true;
                }
                scanned += take;
                scanOfs += take;
            }
        }

        LineIndex affectedLine = -1;
        BufOffset lineStart = 0;
        int oldLineLen = 0;
        if (_lineTree != null) {
            affectedLine = (LineIndex)LineFromOfs(ofs);
            lineStart = affectedLine == 0 ? 0 : _lineTree.PrefixSum(affectedLine - 1);
            oldLineLen = _lineTree.ValueAt(affectedLine);
        }

        var newPiece = new Piece(bufIdx, bufStart, len);
        var (pieceIdx, ofsInPiece) = FindPiece(ofs);

        if (ofsInPiece == 0L) {
            _pieces.Insert(pieceIdx, newPiece);
        } else {
            var existing = _pieces[pieceIdx];
            var left = existing.TakeFirst(ofsInPiece);
            var right = existing.SkipFirst(ofsInPiece);
            _pieces[pieceIdx] = left;
            _pieces.Insert(pieceIdx + 1, newPiece);
            _pieces.Insert(pieceIdx + 2, right);
        }

        // Post-mutation: update line tree.
        if (affectedLine >= 0 && !hasNewlines) {
            _lineTree!.Update(affectedLine, len);
            var newLen = _lineTree.DocValueAt(affectedLine);
            if (newLen > _maxLineLen) _maxLineLen = newLen;
            SplitLongLine(affectedLine);
        } else if (affectedLine >= 0 && hasNewlines) {
            SpliceInsertLines(affectedLine, lineStart, oldLineLen, ofs,
                buf, bufStart, len);
            var nlCount = (int)(LineFromOfs(Math.Min(ofs + len, Length)) - affectedLine + 1);
            SplitLongLines(affectedLine, nlCount);
        } else {
            Debug.Assert(false, "InsertFromBuffer called without a line tree.");
            _maxLineLen = -1;
        }
        AssertLineTreeValid();
    }

    /// <summary>Snapshots the current piece list for later restore (undo).</summary>
    public Piece[] SnapshotPieces() => _pieces.ToArray();

    /// <summary>Snapshots the current buf-space line tree lengths for later restore (undo).</summary>
    public int[] SnapshotLineLengths() {
        var tree = LineTree;
        return tree.ExtractValues();
    }

    /// <summary>Snapshots the current doc-space line tree lengths for later restore (undo).</summary>
    public int[] SnapshotDocLineLengths() {
        var tree = LineTree;
        return tree.ExtractDocValues();
    }

    /// <summary>
    /// Replaces the piece list wholesale (used by bulk-replace undo).
    /// Caller must also restore the line tree via <see cref="InstallLineTree"/>.
    /// </summary>
    public void RestorePieces(Piece[] pieces) {

        _pieces.Clear();
        _pieces.AddRange(pieces);
    }

    /// <summary>
    /// Truncates the add buffer to <paramref name="len"/> characters.
    /// Used by bulk-replace undo to discard appended replacement text.
    /// </summary>
    public void TrimAddBuffer(long len) {
        _addBuf.TrimToCharLength(len);
    }

    /// <summary>
    /// Uniform bulk replace: all matches have the same length and the same
    /// replacement string. Match positions must be sorted ascending and
    /// non-overlapping.  O(pieces + matches) with one line tree rebuild.
    /// </summary>
    public void BulkReplace(long[] matchPositions, int matchLen, string replacement) {
        if (matchPositions.Length == 0) return;

        // Append replacement once to the add buffer.
        var addOfs = _addBuf.CharLength;
        if (replacement.Length > 0) {
            _addBuf.Append(replacement);
        }

        var replacementPiece = replacement.Length > 0
            ? new Piece(_addBufIdx, addOfs, replacement.Length)
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
            addSpans[i] = (_addBuf.CharLength, r.Length);
            if (r.Length > 0) {
                _addBuf.Append(r);
            }
        }

        BulkReplaceCore(matches.Length,
            i => matches[i].Pos,
            i => matches[i].Len,
            i => addSpans[i].Len > 0
                ? new Piece(_addBufIdx, addSpans[i].Ofs, addSpans[i].Len)
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
            var remaining = p.Len - ofsInPiece;
            var available = Math.Min(remaining, targetOfs - docOfs);

            if (available == remaining) {
                // Take the rest of this piece.
                dest.Add(ofsInPiece == 0
                    ? p
                    : new Piece(p.BufIdx, p.Start + ofsInPiece, remaining));
                pieceIdx++;
                ofsInPiece = 0;
                docOfs += remaining;
            } else {
                // Take a prefix of this piece.
                dest.Add(new Piece(p.BufIdx, p.Start + ofsInPiece, available));
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
            var remaining = p.Len - ofsInPiece;

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

    internal char CharAt(BufOffset ofs) {
        var (pieceIdx, ofsInPiece) = FindPiece(ofs);
        if (pieceIdx >= _pieces.Count) {
            throw new ArgumentOutOfRangeException(nameof(ofs));
        }
        var p = _pieces[pieceIdx];
        return _buffers[p.BufIdx][p.Start + ofsInPiece];
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
