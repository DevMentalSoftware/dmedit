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
        ScrollSelectionIntoView(SearchDirection.Forward);
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
        ScrollSelectionIntoView(SearchDirection.Backward);
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
                               bool wholeWord = false, SearchMode mode = SearchMode.Normal,
                               bool preserveCase = false) {
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
        var rep = preserveCase ? ApplyPreserveCase(selectedText, replacement) : replacement;
        doc.Insert(rep);
        InvalidateVisual();
        // Advance to next match — FindNext handles the scroll to reveal the
        // new match, which is the only scroll the user cares about here.
        // (Any ScrollCaretIntoView on the replacement's caret would be
        // immediately superseded by FindNext's ScrollSelectionIntoView.)
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
                          bool preserveCase = false,
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

        // Phase 2: piece-list swap on UI thread (fast), line tree
        // rebuild on background thread (slow — full document scan).
        FlushCompound();
        int count;
        if (preserveCase) {
            // Each match may produce a different replacement string, so
            // always use the varying path.
            var uniformLen = matchLengths == null ? opts.Needle.Length : 0;
            var matches = new (long Pos, int Len)[positions.Count];
            var replacements = new string[positions.Count];
            for (var i = 0; i < positions.Count; i++) {
                var mLen = matchLengths != null ? matchLengths[i] : uniformLen;
                matches[i] = (positions[i], mLen);
                var matchText = table.GetText(positions[i], mLen);
                replacements[i] = ApplyPreserveCase(matchText, replacement);
            }
            count = doc.BulkReplaceVarying(matches, replacements, deferLineTree: true);
        } else if (matchLengths == null) {
            count = doc.BulkReplaceUniform(
                positions.ToArray(), opts.Needle.Length, replacement, deferLineTree: true);
        } else {
            var matches = new (long Pos, int Len)[positions.Count];
            for (var i = 0; i < positions.Count; i++) {
                matches[i] = (positions[i], matchLengths[i]);
            }
            var replacements = new string[matches.Length];
            Array.Fill(replacements, replacement);
            count = doc.BulkReplaceVarying(matches, replacements, deferLineTree: true);
        }

        progress?.Report(($"Building line index\u2026", 99));

        // Heavy line-tree rebuild on background thread.
        await Task.Run(() => table.RebuildLineTree(), ct);

        // Back on UI thread — finalize.
        doc.RaiseChanged();
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
                    var chunk = new string(dest);
                    idx = opts.RegexWholeWord
                        ? MatchInSegments(chunk, opts, 0, chunk.Length)
                        : RegexFirstMatch(chunk, opts, 0);
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
            var segLen = opts.RegexWholeWord ? SegmentLength(text, 0) : remaining;
            var m = opts.CompiledRegex!.Match(text, 0, segLen);
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
            // For regex+WholeWord, constrain to the non-whitespace segment
            // so the match can't span across words.
            var segLen = opts.RegexWholeWord ? SegmentLength(text, 0) : text.Length;
            var m = opts.CompiledRegex.Match(text, 0, segLen);
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
                    ScrollSelectionIntoView(SearchDirection.Forward);
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
            ScrollSelectionIntoView(SearchDirection.Forward);
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
                SearchMode.Wildcard => BuildRegex(WildcardToRegex(needle, wholeWord), matchCase, wholeWord),
                _ => wholeWord ? BuildRegex(Regex.Escape(needle), matchCase, wholeWord) : null,
            };
        }

        public StringComparison Comparison =>
            MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// True when the search uses regex mode with whole-word enabled.
        /// This combination requires segment-by-segment matching so greedy
        /// quantifiers can't span across words.
        /// </summary>
        public bool RegexWholeWord => WholeWord && Mode == SearchMode.Regex;

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
                // For regex+WholeWord, constrain to the non-whitespace
                // segment at position 0 so greedy quantifiers can't span words.
                var segLen = RegexWholeWord ? SegmentLength(text, 0) : remaining;
                var m = CompiledRegex.Match(text, 0, segLen);
                return m.Success && m.Index == 0 ? m.Length : Needle.Length;
            } finally {
                ArrayPool<char>.Shared.Return(buf);
            }
        }

        private static Regex? BuildRegex(string pattern, bool matchCase, bool wholeWord) {
            if (wholeWord) {
                pattern = @"\b" + pattern + @"\b";
            }
            var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled
                | RegexOptions.Multiline;
            if (!matchCase) {
                opts |= RegexOptions.IgnoreCase;
            }
            try {
                return new Regex(pattern, opts);
            } catch {
                return null; // invalid regex — fall back to no matches
            }
        }

        private static string WildcardToRegex(string wildcard, bool wholeWord) {
            // When wholeWord is true, restrict wildcards to word characters
            // so the match stays within a single word.
            // * → \w* (or .*), ? → \w (or .), everything else escaped
            var sb = new System.Text.StringBuilder(wildcard.Length * 2);
            foreach (var ch in wildcard) {
                sb.Append(ch switch {
                    '*' => wholeWord ? @"\w*" : ".*",
                    '?' => wholeWord ? @"\w" : ".",
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
    // Preserve-case replacement
    // -----------------------------------------------------------------

    /// <summary>
    /// Transforms <paramref name="replacement"/> to match the casing
    /// pattern of <paramref name="matched"/>:
    /// <list type="bullet">
    /// <item>All upper → replacement uppercased</item>
    /// <item>All lower → replacement lowercased</item>
    /// <item>Title case (first upper, rest lower) → replacement title-cased</item>
    /// <item>Otherwise → replacement unchanged</item>
    /// </list>
    /// </summary>
    internal static string ApplyPreserveCase(string matched, string replacement) {
        if (matched.Length == 0 || replacement.Length == 0) {
            return replacement;
        }
        // Classify the matched text's casing pattern.
        bool hasUpper = false, hasLower = false;
        foreach (var ch in matched) {
            if (char.IsUpper(ch)) {
                hasUpper = true;
            }
            if (char.IsLower(ch)) {
                hasLower = true;
            }
        }
        if (hasUpper && !hasLower) {
            return replacement.ToUpperInvariant();
        }
        if (hasLower && !hasUpper) {
            return replacement.ToLowerInvariant();
        }
        // Title case: first letter upper, all subsequent letters lower.
        if (char.IsUpper(matched[0])) {
            var titleCase = true;
            for (var i = 1; i < matched.Length; i++) {
                if (char.IsUpper(matched[i])) {
                    titleCase = false;
                    break;
                }
            }
            if (titleCase) {
                return char.ToUpperInvariant(replacement[0])
                    + replacement.Substring(1).ToLowerInvariant();
            }
        }
        return replacement;
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
    /// Test-only wrapper for <see cref="SearchChunkedBackward"/>.  The
    /// inner function is private (and <see cref="SearchOptions"/> is
    /// private), but the 2026-04-09 hang regression needs direct access
    /// with a synthetic options object and a deadline-guarded thread.
    /// </summary>
    internal static long SearchChunkedBackwardForTest(
            PieceTable table, string needle, long start, long end) {
        var opts = new SearchOptions(needle,
            matchCase: false, wholeWord: false, mode: SearchMode.Normal);
        return SearchChunkedBackward(table, opts, start, end, maxOverlap: 1024);
    }

    /// <summary>
    /// Searches backward from <paramref name="end"/> to <paramref name="start"/>
    /// in bounded chunks, returning the last match position in the range.
    /// </summary>
    /// <remarks>
    /// Termination: each iteration searches <c>[chunkStart, chunkEnd)</c>
    /// where <c>chunkStart = max(start, chunkEnd - chunkSize - overlap)</c>.
    /// If the chunk we just searched started at <paramref name="start"/>,
    /// we have covered the entire range and must break — the overlap-step
    /// that normally retreats <c>chunkEnd</c> would otherwise leave us
    /// re-searching the same chunk forever (seen in 2026-04-09 as an app
    /// hang when FindPrevious wrapped on a document with a single match
    /// whose offset was small enough that <c>end - start ≤ chunkSize</c>
    /// on the first iteration).  The <c>chunkEnd &lt;= start</c> check
    /// below doesn't catch this case because the overlap-reset leaves
    /// <c>chunkEnd = start + overlap &gt; start</c>.
    /// </remarks>
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
                    var rangeEnd = (int)(end - chunkStart);
                    int lastIdx;
                    if (opts.RegexWholeWord) {
                        lastIdx = LastMatchInSegments(chunk, opts, 0, rangeEnd);
                    } else {
                        lastIdx = RegexLastMatch(chunk, opts, 0, rangeEnd);
                    }
                    hit = lastIdx >= 0 ? chunkStart + lastIdx : -1;
                } else {
                    var idx = dest.LastIndexOf(opts.Needle.AsSpan(), opts.Comparison);
                    hit = idx >= 0 ? chunkStart + idx : -1;
                }
                if (hit >= 0) return hit;

                // If we just searched the entire [start, chunkEnd) range,
                // there's no point reducing chunkEnd further — we've
                // covered everything.  Break before the overlap-step
                // that would leave chunkEnd above start forever.
                if (chunkStart == start) break;

                chunkEnd = chunkStart + overlap;
                if (chunkEnd <= start) break;
            }
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
        return -1;
    }

    // -----------------------------------------------------------------
    // Segment-based helpers for regex + WholeWord
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns the length of the non-whitespace segment starting at
    /// <paramref name="pos"/> in <paramref name="text"/>.
    /// </summary>
    private static int SegmentLength(string text, int pos) {
        var i = pos;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) {
            i++;
        }
        return i - pos;
    }

    /// <summary>
    /// Scans non-whitespace segments in <paramref name="chunk"/> and
    /// returns the index of the first regex match, or -1.  The regex
    /// is constrained to each segment via <c>Match(input, start, len)</c>
    /// so greedy quantifiers can't span across words.
    /// </summary>
    private static int MatchInSegments(string chunk, SearchOptions opts,
            int from, int limit) {
        var i = from;
        while (i < limit) {
            if (char.IsWhiteSpace(chunk[i])) {
                i++;
                continue;
            }
            var segLen = SegmentLength(chunk, i);
            var m = opts.CompiledRegex!.Match(chunk, i, Math.Min(segLen, limit - i));
            if (m.Success) {
                return m.Index;
            }
            i += segLen;
        }
        return -1;
    }

    /// <summary>
    /// Scans non-whitespace segments in <paramref name="chunk"/> and
    /// returns the index of the last regex match before
    /// <paramref name="limit"/>, or -1.
    /// </summary>
    private static int LastMatchInSegments(string chunk, SearchOptions opts,
            int from, int limit) {
        var lastIdx = -1;
        var i = from;
        while (i < limit) {
            if (char.IsWhiteSpace(chunk[i])) {
                i++;
                continue;
            }
            var segLen = SegmentLength(chunk, i);
            var segEnd = Math.Min(i + segLen, limit);
            // Find all non-overlapping matches within this segment.
            var at = i;
            while (at < segEnd) {
                var m = opts.CompiledRegex!.Match(chunk, at, segEnd - at);
                if (!m.Success) {
                    break;
                }
                lastIdx = m.Index;
                at = m.Index + Math.Max(m.Length, 1);
            }
            i += segLen;
        }
        return lastIdx;
    }

    /// <summary>Simple forward regex match (no segment restriction).</summary>
    private static int RegexFirstMatch(string chunk, SearchOptions opts, int from) {
        var m = opts.CompiledRegex!.Match(chunk, from);
        return m.Success ? m.Index : -1;
    }

    /// <summary>Simple backward regex scan (no segment restriction).</summary>
    private static int RegexLastMatch(string chunk, SearchOptions opts,
            int from, int limit) {
        var lastIdx = -1;
        var m = opts.CompiledRegex!.Match(chunk, from);
        while (m.Success && m.Index < limit) {
            lastIdx = m.Index;
            m = m.NextMatch();
        }
        return lastIdx;
    }
}
