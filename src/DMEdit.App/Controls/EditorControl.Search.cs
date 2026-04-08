using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

// Search / Replace / Incremental-search partial of EditorControl.
// Owns the Find, FindPrevious, FindSelection, ReplaceCurrent,
// ReplaceAllAsync surface, the chunked search helpers, and the
// SearchOptions nested struct.  Shared fields (_lastSearchTerm,
// _inIncrementalSearch, _isearchString, _isearchFailed) live in the
// main EditorControl.cs.
public sealed partial class EditorControl {

    /// <summary>
    /// The most recent successful search term. Shared across Find Bar,
    /// incremental search, and Find Word/Selection. Used by Find Next / Find Previous.
    /// </summary>
    public string LastSearchTerm {
        get => _lastSearchTerm;
        set => _lastSearchTerm = value ?? "";
    }

    /// <summary>
    /// Selects the next occurrence of <see cref="LastSearchTerm"/> after the
    /// current selection (or caret), wrapping around if needed.
    /// Returns true if a match was found.
    /// </summary>
    public bool FindNext(bool matchCase = false, bool wholeWord = false,
                         SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        var table = doc.Table;
        var sel = doc.Selection;
        // Start searching one character past the start of the current selection
        // so we advance past the current match.
        var searchFrom = sel.IsEmpty ? sel.Caret : sel.Start + 1;
        if (searchFrom >= table.Length) {
            searchFrom = 0;
        }
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var found = FindInDocument(table, opts, searchFrom);
        if (found < 0) {
            return false;
        }
        doc.Selection = new Selection(found, found + opts.MatchLength(table, found));
        ScrollCaretIntoView();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Selects the previous occurrence of <see cref="LastSearchTerm"/> before
    /// the current selection (or caret), wrapping around if needed.
    /// Returns true if a match was found.
    /// </summary>
    public bool FindPrevious(bool matchCase = false, bool wholeWord = false,
                             SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        var table = doc.Table;
        var sel = doc.Selection;
        var searchBefore = sel.IsEmpty ? sel.Caret : sel.Start;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var found = FindInDocumentBackward(table, opts, searchBefore);
        if (found < 0) {
            return false;
        }
        doc.Selection = new Selection(found, found + opts.MatchLength(table, found));
        ScrollCaretIntoView();
        InvalidateVisual();
        return true;
    }

    /// <summary>Maximum length for a search term derived from the selection.</summary>
    private const int MaxSearchTermLength = 1024;

    /// <summary>
    /// Returns the selected text if it is a single line and within
    /// <see cref="MaxSearchTermLength"/>, or null otherwise.
    /// Does not modify the selection.
    /// </summary>
    public string? GetSelectionAsSearchTerm() {
        var doc = Document;
        if (doc == null || doc.Selection.IsEmpty) return null;
        var sel = doc.Selection;
        if (sel.Len > MaxSearchTermLength) return null;
        var table = doc.Table;
        var startLine = table.LineFromOfs(sel.Start);
        var endLine = table.LineFromOfs(sel.End - 1);
        if (startLine != endLine) return null;
        return doc.GetSelectedText();
    }

    /// <summary>
    /// Returns the selected text as a search term, or null if the selection
    /// is empty or spans more than one line.  When collapsed, selects the
    /// word at the caret first.
    /// </summary>
    private string? GetSingleLineSelectionTerm() {
        var doc = Document;
        if (doc == null) return null;
        if (doc.Selection.IsEmpty) {
            doc.SelectWord();
            if (doc.Selection.IsEmpty) return null;
        }
        return GetSelectionAsSearchTerm();
    }

    public bool FindNextSelection() {
        var term = GetSingleLineSelectionTerm();
        if (term == null) return false;
        _lastSearchTerm = term;
        return FindNext();
    }

    /// <summary>
    /// Uses the current selection (or selects the word at the caret if collapsed)
    /// as the search term, then finds the previous occurrence.
    /// </summary>
    public bool FindPreviousSelection() {
        var term = GetSingleLineSelectionTerm();
        if (term == null) return false;
        _lastSearchTerm = term;
        return FindPrevious();
    }

    // -------------------------------------------------------------------------
    // Replace
    // -------------------------------------------------------------------------

    /// <summary>
    /// Replaces the current selection (if it matches the search term) with
    /// <paramref name="replacement"/>, then advances to the next match.
    /// Returns true if a replacement was made.
    /// </summary>
    public bool ReplaceCurrent(string replacement, bool matchCase = false,
                               bool wholeWord = false, SearchMode mode = SearchMode.Normal) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return false;
        }
        // Only replace if the current selection matches the search term.
        if (doc.Selection.IsEmpty) {
            // No selection — try to find the next match first.
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        // The selection should match the search term, so its length is bounded.
        if (doc.Selection.Len > MaxSearchTermLength) {
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        var selectedText = doc.GetSelectedText()!; // bounded by MaxSearchTermLength above
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        if (!IsSelectionMatch(selectedText, opts)) {
            // Selection doesn't match — find next match instead.
            FindNext(matchCase, wholeWord, mode);
            return false;
        }
        FlushCompound();
        doc.Insert(replacement);
        ScrollCaretIntoView();
        InvalidateVisual();
        // Advance to next match.
        FindNext(matchCase, wholeWord, mode);
        return true;
    }

    /// <summary>
    /// Collects match positions on a background thread, then applies a single
    /// bulk PieceTable replace on the UI thread.  Reports progress via
    /// <paramref name="progress"/> (0–100) and supports cancellation.
    /// Returns the number of replacements made, or 0 if cancelled.
    /// </summary>
    public async Task<int> ReplaceAllAsync(string replacement, bool matchCase = false,
                          bool wholeWord = false, SearchMode mode = SearchMode.Normal,
                          IProgress<(string Message, double Percent)>? progress = null,
                          CancellationToken ct = default) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0) {
            return 0;
        }
        var table = doc.Table;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var isRegex = opts.CompiledRegex != null;
        var docLen = table.Length;
        var maxOverlap = Settings?.MaxRegexMatchLength ?? 1024;

        // Phase 1: collect all match positions on a background thread.
        // Progress updates throttled to ~100ms to avoid UI marshalling overhead.
        var (positions, matchLengths) = await Task.Run(() => {
            var pos = new List<long>();
            var lens = isRegex ? new List<int>() : null;
            long searchFrom = 0;
            var lastProgressTicks = Environment.TickCount64;
            while (searchFrom <= docLen) {
                ct.ThrowIfCancellationRequested();
                var found = SearchChunked(table, opts, searchFrom, maxOverlap);
                if (found < 0) break;
                var matchLen = isRegex
                    ? RegexMatchLengthAt(table, opts, found)
                    : opts.Needle.Length;
                pos.Add(found);
                lens?.Add(matchLen);
                searchFrom = found + Math.Max(matchLen, 1);

                var now = Environment.TickCount64;
                if (docLen > 0 && now - lastProgressTicks >= 100) {
                    lastProgressTicks = now;
                    var pct = (double)searchFrom / docLen * 99.0;
                    progress?.Report(($"Searching\u2026 {pos.Count:N0} matches found", pct));
                }
            }
            return (pos, lens);
        }, ct);

        if (positions.Count == 0) return 0;

        progress?.Report(($"Replacing {positions.Count:N0} matches\u2026", 99));

        // Phase 2: single bulk PieceTable operation on the UI thread.
        FlushCompound();
        int count;
        if (matchLengths == null) {
            count = doc.BulkReplaceUniform(
                positions.ToArray(), opts.Needle.Length, replacement);
        } else {
            var matches = new (long Pos, int Len)[positions.Count];
            for (var i = 0; i < positions.Count; i++) {
                matches[i] = (positions[i], matchLengths[i]);
            }
            var replacements = new string[matches.Length];
            Array.Fill(replacements, replacement);
            count = doc.BulkReplaceVarying(matches, replacements);
        }

        progress?.Report(("Done", 100));

        ScrollCaretIntoView();
        InvalidateVisual();
        return count;
    }

    /// <summary>
    /// Copies document text into a caller-owned array without allocating
    /// a string.  Uses <see cref="PieceTable.ForEachPiece"/>.
    /// </summary>
    private static void CopyFromTable(PieceTable table, long start, char[] buf, int len) {
        var pos = 0;
        table.ForEachPiece(start, len, span => {
            span.CopyTo(buf.AsSpan(pos));
            pos += span.Length;
        });
    }

    /// <summary>
    /// Searches forward from <paramref name="start"/> in bounded chunks
    /// so we never materialize the entire remaining document.
    /// </summary>
    private static long SearchChunked(PieceTable table, SearchOptions opts, long start,
        int maxRegexMatchLen = 1024, long endLimit = -1) {
        const int chunkSize = 64 * 1024;
        var docLen = endLimit >= 0 ? Math.Min(endLimit, table.Length) : table.Length;
        // Overlap by needle length (or maxRegexMatchLen for regex) so
        // matches spanning chunk boundaries are found.
        var overlap = opts.CompiledRegex != null
            ? Math.Min(maxRegexMatchLen, (int)(docLen - start))
            : opts.Needle.Length;

        var bufLen = chunkSize + overlap;
        var buf = ArrayPool<char>.Shared.Rent(bufLen);
        try {
            while (start < docLen) {
                var readLen = (int)Math.Min(bufLen, docLen - start);
                CopyFromTable(table, start, buf, readLen);
                var dest = buf.AsSpan(0, readLen);
                int idx;
                if (opts.CompiledRegex != null) {
                    var m = opts.CompiledRegex.Match(new string(dest));
                    idx = m.Success ? m.Index : -1;
                } else {
                    idx = dest.IndexOf(opts.Needle.AsSpan(), opts.Comparison);
                }
                if (idx >= 0 && start + idx < docLen) return start + idx;
                start += chunkSize;
            }
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
        return -1;
    }

    /// <summary>Maximum matches to count before returning a capped result.</summary>
    private const int MaxMatchCount = 9999;

    /// <summary>
    /// Counts matches on a background thread.  The caller should cancel
    /// <paramref name="ct"/> when the search term or document changes.
    /// </summary>
    public Task<(int Current, int Total, bool Capped)> GetMatchInfoAsync(
            bool matchCase, bool wholeWord, SearchMode mode,
            CancellationToken ct = default) {
        var doc = Document;
        if (doc == null || _lastSearchTerm.Length == 0)
            return Task.FromResult((0, 0, false));
        // Capture UI-thread state before offloading.
        var table = doc.Table;
        var opts = new SearchOptions(_lastSearchTerm, matchCase, wholeWord, mode);
        var selStart = doc.Selection.Start;
        var maxOverlap = Settings?.MaxRegexMatchLength ?? 1024;
        return Task.Run(() => CountMatches(table, opts, selStart, maxOverlap, ct), ct);
    }

    private static (int Current, int Total, bool Capped) CountMatches(
            PieceTable table, SearchOptions opts, long selStart,
            int maxOverlap, CancellationToken ct) {
        int total = 0, current = 0;
        long pos = 0;
        while (pos <= table.Length) {
            if (ct.IsCancellationRequested) return (current, total, false);
            var found = SearchChunked(table, opts, pos, maxOverlap);
            if (found < 0) break;
            total++;
            var matchLen = opts.CompiledRegex != null
                ? RegexMatchLengthAt(table, opts, found)
                : opts.Needle.Length;
            if (found == selStart) current = total;
            if (total >= MaxMatchCount) return (current, total, true);
            pos = found + Math.Max(matchLen, 1);
        }
        return (current, total, false);
    }

    /// <summary>
    /// Returns the regex match length at an exact document position.
    /// Reads only enough text to determine the match.
    /// </summary>
    private static int RegexMatchLengthAt(PieceTable table, SearchOptions opts, long pos) {
        var remaining = (int)Math.Min(table.Length - pos, 1024);
        var buf = ArrayPool<char>.Shared.Rent(remaining);
        try {
            CopyFromTable(table, pos, buf, remaining);
            var text = new string(buf, 0, remaining);
            var m = opts.CompiledRegex!.Match(text);
            return m.Success && m.Index == 0 ? m.Length : 0;
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="text"/> matches the search pattern.
    /// For plain/wildcard/regex modes the check varies.
    /// </summary>
    private static bool IsSelectionMatch(string text, SearchOptions opts) {
        if (opts.CompiledRegex != null) {
            var m = opts.CompiledRegex.Match(text);
            return m.Success && m.Index == 0 && m.Length == text.Length;
        }
        return string.Equals(text, opts.Needle, opts.Comparison);
    }

    // -------------------------------------------------------------------------
    // Incremental search
    // -------------------------------------------------------------------------

    public bool InIncrementalSearch => _inIncrementalSearch;
    public string IncrementalSearchText => _isearchString;
    public bool IncrementalSearchFailed => _isearchFailed;

    /// <summary>Raised when incremental search state changes (for status bar updates).</summary>
    public event EventHandler? IncrementalSearchChanged;

    public void StartIncrementalSearch() {
        _inIncrementalSearch = true;
        _isearchString = "";
        _isearchFailed = false;
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExitIncrementalSearch() {
        if (!_inIncrementalSearch) {
            return;
        }
        _inIncrementalSearch = false;
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HandleIncrementalSearchChar(string text) {
        if (_isearchFailed || string.IsNullOrEmpty(text)) {
            return;
        }

        _isearchString += text;
        var doc = Document;
        if (doc == null) {
            return;
        }

        var table = doc.Table;
        var sel = doc.Selection;

        // Try 1: extend current selection in-place if the next char(s) match.
        if (!sel.IsEmpty) {
            var endOfSel = sel.End;
            if (endOfSel + text.Length <= table.Length) {
                var nextChars = table.GetText(endOfSel, text.Length);
                if (nextChars.Equals(text, StringComparison.OrdinalIgnoreCase)) {
                    doc.Selection = new Selection(sel.Start, endOfSel + text.Length);
                    _lastSearchTerm = _isearchString;
                    ScrollCaretIntoView();
                    InvalidateVisual();
                    IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }

        // Try 2: search the document for the full accumulated string.
        var searchFrom = sel.IsEmpty ? sel.Caret : sel.Start;
        var found = FindInDocument(table, _isearchString, searchFrom);
        if (found >= 0) {
            doc.Selection = new Selection(found, found + _isearchString.Length);
            _lastSearchTerm = _isearchString;
            ScrollCaretIntoView();
            InvalidateVisual();
        } else {
            _isearchFailed = true;
        }
        IncrementalSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------
    // Search options
    // -----------------------------------------------------------------

    /// <summary>
    /// Encapsulates search parameters so helpers don't need many arguments.
    /// </summary>
    private readonly struct SearchOptions {
        public readonly string Needle;
        public readonly bool MatchCase;
        public readonly bool WholeWord;
        public readonly SearchMode Mode;
        public readonly Regex? CompiledRegex;

        public SearchOptions(string needle, bool matchCase, bool wholeWord, SearchMode mode) {
            Needle = needle;
            MatchCase = matchCase;
            WholeWord = wholeWord;
            Mode = mode;
            CompiledRegex = mode switch {
                SearchMode.Regex => BuildRegex(needle, matchCase, wholeWord),
                SearchMode.Wildcard => BuildRegex(WildcardToRegex(needle), matchCase, wholeWord),
                _ => wholeWord ? BuildRegex(Regex.Escape(needle), matchCase, wholeWord) : null,
            };
        }

        public StringComparison Comparison =>
            MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>Returns the length of the match at <paramref name="pos"/>.</summary>
        public int MatchLength(PieceTable table, long pos) {
            if (CompiledRegex == null) {
                return Needle.Length;
            }
            // For regex matches we need to re-match at the position to get length.
            var remaining = (int)Math.Min(table.Length - pos, Needle.Length * 4 + 256);
            var buf = ArrayPool<char>.Shared.Rent(remaining);
            try {
                var written = 0;
                table.ForEachPiece(pos, remaining, span => {
                    span.CopyTo(buf.AsSpan(written));
                    written += span.Length;
                });
                var text = new string(buf, 0, remaining);
                var m = CompiledRegex.Match(text);
                return m.Success && m.Index == 0 ? m.Length : Needle.Length;
            } finally {
                ArrayPool<char>.Shared.Return(buf);
            }
        }

        private static Regex? BuildRegex(string pattern, bool matchCase, bool wholeWord) {
            if (wholeWord) {
                pattern = @"\b" + pattern + @"\b";
            }
            var opts = RegexOptions.CultureInvariant;
            if (!matchCase) {
                opts |= RegexOptions.IgnoreCase;
            }
            try {
                return new Regex(pattern, opts);
            } catch {
                return null; // invalid regex — fall back to no matches
            }
        }

        private static string WildcardToRegex(string wildcard) {
            // * → .*, ? → ., everything else escaped
            var sb = new System.Text.StringBuilder(wildcard.Length * 2);
            foreach (var ch in wildcard) {
                sb.Append(ch switch {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString()),
                });
            }
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------
    // Forward search
    // -----------------------------------------------------------------

    private static long FindInDocument(PieceTable table, string needle, long fromOfs) =>
        FindInDocument(table, new SearchOptions(needle, false, false, SearchMode.Normal), fromOfs);

    private static long FindInDocument(PieceTable table, SearchOptions opts, long fromOfs,
                                       int maxOverlap = 1024) {
        var docLen = table.Length;
        if (opts.Needle.Length == 0 || docLen == 0) {
            return -1;
        }

        // Search forward from caret to end (chunked).
        var hit = SearchChunked(table, opts, fromOfs, maxOverlap);
        if (hit >= 0) {
            return hit;
        }

        // Wrap around: search from start up to fromOfs + needle overlap.
        if (fromOfs > 0) {
            var wrapEnd = Math.Min(fromOfs + opts.Needle.Length - 1, docLen);
            hit = SearchChunked(table, opts, 0, maxOverlap, wrapEnd);
            if (hit >= 0) {
                return hit;
            }
        }

        return -1;
    }

    // -----------------------------------------------------------------
    // Backward search
    // -----------------------------------------------------------------

    private static long FindInDocumentBackward(PieceTable table, SearchOptions opts,
                                               long beforeOfs, int maxOverlap = 1024) {
        var docLen = table.Length;
        if (opts.Needle.Length == 0 || docLen == 0) {
            return -1;
        }

        var hit = SearchChunkedBackward(table, opts, 0, beforeOfs, maxOverlap);
        if (hit >= 0) {
            return hit;
        }

        // Wrap around: search backward from end to beforeOfs.
        if (beforeOfs < docLen) {
            hit = SearchChunkedBackward(table, opts, beforeOfs, docLen, maxOverlap);
        }
        return hit;
    }

    /// <summary>
    /// Searches backward from <paramref name="end"/> to <paramref name="start"/>
    /// in bounded chunks, returning the last match position in the range.
    /// </summary>
    private static long SearchChunkedBackward(PieceTable table, SearchOptions opts,
            long start, long end, int maxOverlap) {
        const int chunkSize = 64 * 1024;
        var overlap = opts.CompiledRegex != null
            ? Math.Min(maxOverlap, (int)(end - start))
            : opts.Needle.Length;

        var bufLen = chunkSize + overlap;
        var buf = ArrayPool<char>.Shared.Rent(bufLen);
        try {
            // Walk backward from end in chunks.
            var chunkEnd = end;
            while (chunkEnd > start) {
                var chunkStart = Math.Max(start, chunkEnd - chunkSize - overlap);
                var readLen = (int)(chunkEnd - chunkStart);
                CopyFromTable(table, chunkStart, buf, readLen);
                var dest = buf.AsSpan(0, readLen);

                long hit;
                if (opts.CompiledRegex != null) {
                    var chunk = new string(dest);
                    Match? last = null;
                    foreach (Match m in opts.CompiledRegex.Matches(chunk)) {
                        if (chunkStart + m.Index < end) last = m;
                    }
                    hit = last != null ? chunkStart + last.Index : -1;
                } else {
                    var idx = dest.LastIndexOf(opts.Needle.AsSpan(), opts.Comparison);
                    hit = idx >= 0 ? chunkStart + idx : -1;
                }
                if (hit >= 0) return hit;

                chunkEnd = chunkStart + overlap;
                if (chunkEnd <= start) break;
            }
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
        return -1;
    }
}
