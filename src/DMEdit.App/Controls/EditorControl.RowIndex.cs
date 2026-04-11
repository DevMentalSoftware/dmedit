using System;
using System.Diagnostics;
using DMEdit.Core.Collections;
using DMEdit.Core.Documents;

namespace DMEdit.App.Controls;

/// <summary>
/// Per-line visual row count index.  Stores the number of wrapped visual
/// rows each logical line occupies at the current effective wrap width.
/// Used by the scroll math to provide exact (not estimated) mappings between
/// scroll values, topLine, and renderOffsetY — solving the drag-thumb
/// drift / jump / can't-reach-ends problems that every estimate-based
/// approach hits for docs with variable wrap counts.
///
/// <para><b>Design notes:</b></para>
/// <list type="bullet">
/// <item>Stored in a separate <see cref="LineIndexTree"/> instance so we can
/// invalidate and rebuild it on any input change (wrap column, font, viewport
/// width, doc edit) without touching the authoritative character-length
/// <c>LineIndexTree</c> inside <see cref="PieceTable"/>.</item>
/// <item>Built lazily on first scroll query and after any invalidation.
/// The build walks every line once, calling <see cref="MonoRowBreaker.CountRows"/>
/// (or a <c>TextLayout</c> measurement for lines with tabs / proportional
/// fonts).</item>
/// <item>For documents above <see cref="RowIndexLineThreshold"/> we skip the
/// build entirely and leave <see cref="_rowIndex"/> null — callers fall back
/// to the char-density estimate, which averages out well enough for huge
/// homogeneous docs.</item>
/// <item>Any edit invalidates the index (sets it to null).  The next scroll
/// query rebuilds it from scratch.  For small/medium docs this is cheap;
/// for docs near the threshold it's the main cost after an edit.</item>
/// </list>
/// </summary>
public sealed partial class EditorControl {
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    /// <summary>
    /// Row count prefix-sum tree.  Element i is the visual row count of
    /// logical line i.  Prefix sums give the top-of-line Y position (in
    /// row units).  Null when not yet built, invalidated, or the doc is
    /// over the build threshold.
    /// </summary>
    private LineIndexTree? _rowIndex;

    /// <summary>
    /// The effective wrap width (in characters) that <see cref="_rowIndex"/>
    /// was built for.  If the current effective width differs, the index
    /// is stale and must be rebuilt.  Zero means wrap-off (each line is
    /// exactly 1 row, no wrap math needed).
    /// </summary>
    private int _rowIndexBuildCharsPerRow = -1;

    /// <summary>
    /// The hanging-indent char count the row index was built with.  Any
    /// change re-invalidates the index.
    /// </summary>
    private int _rowIndexBuildHangingIndent;

    /// <summary>
    /// Above this character count we skip building the row index and fall
    /// back to the char-density estimate.  Character count (table.Length)
    /// is used instead of line count because it's known immediately — even
    /// during streaming loads when the line tree is only partially built.
    /// 20 million characters is roughly a 20 MB text file, which builds
    /// the row index in ~50–250ms depending on line lengths.  Larger docs
    /// use the old estimate, which averages out well thanks to the law of
    /// large numbers.
    /// </summary>
    private const long RowIndexCharThreshold = 20_000_000;

    // -------------------------------------------------------------------------
    // Invalidation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Marks the row index as stale.  Called from every code path that
    /// could change the per-line row count: doc edits, wrap setting
    /// toggles, font / viewport changes, hanging-indent changes.  The
    /// next scroll query will rebuild from scratch.
    /// </summary>
    private void InvalidateRowIndex() {
        _rowIndex = null;
        _rowIndexBuildCharsPerRow = -1;
    }

    // -------------------------------------------------------------------------
    // Build
    // -------------------------------------------------------------------------

    /// <summary>
    /// Ensures <see cref="_rowIndex"/> is populated and current.  Returns
    /// the populated tree, or <c>null</c> if the doc is over the build
    /// threshold (caller must fall back to the estimate).
    ///
    /// <para>Cost: O(N × lineLen / charsPerRow) on first call / after
    /// invalidation.  Subsequent calls return the cached tree in O(1).</para>
    /// </summary>
    private LineIndexTree? EnsureRowIndex() {
        var doc = Document;
        if (doc == null) return null;
        var table = doc.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return null;

        // Threshold guard: above this size the walk is too expensive
        // for interactive use, and the estimate is accurate enough.
        // Uses character count (not line count) because it's known
        // immediately even during streaming loads when the line tree
        // is only partially populated.
        if (table.Length > RowIndexCharThreshold) return null;

        // Determine current effective wrap width and hanging indent.
        var (cpr, hangingIndent) = GetRowIndexBuildParams();
        if (cpr == _rowIndexBuildCharsPerRow
                && hangingIndent == _rowIndexBuildHangingIndent
                && _rowIndex != null
                && _rowIndex.Count == lineCount) {
            return _rowIndex;
        }

        // Build (or rebuild).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _rowIndex = BuildRowIndexFor(table, lineCount, cpr, hangingIndent);
        sw.Stop();
        _rowIndexBuildCharsPerRow = cpr;
        _rowIndexBuildHangingIndent = hangingIndent;
        PerfStats.RowIndexBuilds++;
        PerfStats.RowIndexBuildMs = sw.Elapsed.TotalMilliseconds;
        return _rowIndex;
    }

    /// <summary>
    /// Computes the (charsPerRow, hangingIndentChars) pair the row index
    /// should use for the CURRENT layout parameters.  Zero charsPerRow
    /// means wrap-off — each line is 1 row.
    /// </summary>
    private (int charsPerRow, int hangingIndent) GetRowIndexBuildParams() {
        if (!_wrapLines) return (0, 0);
        var textW = GetEffectiveTextWidth();
        if (!double.IsFinite(textW) || textW <= 0) return (0, 0);
        var cw = GetCharWidth();
        if (cw <= 0) return (0, 0);
        var cpr = Math.Max(1, (int)(textW / cw));
        var hangingIndent = _hangingIndent && _wrapLines && !_charWrapMode
            ? Math.Max(0, _indentWidth / 2)
            : 0;
        return (cpr, hangingIndent);
    }

    /// <summary>
    /// Walks every line and returns a <see cref="LineIndexTree"/> whose
    /// element i is the visual row count of line i at the given wrap
    /// width and hanging indent.
    /// </summary>
    private LineIndexTree BuildRowIndexFor(PieceTable table, long lineCount,
            int charsPerRow, int hangingIndent) {
        // Wrap off: every line is exactly 1 row.  Cheap bulk build.
        if (charsPerRow <= 0) {
            var ones = new int[lineCount];
            for (var i = 0; i < ones.Length; i++) ones[i] = 1;
            return LineIndexTree.FromValues(ones);
        }

        var firstRowChars = charsPerRow;
        var contRowChars = Math.Max(1, charsPerRow - hangingIndent);

        // Always use MonoRowBreaker for the row index, even with
        // proportional fonts.  Rationale:
        //
        // 1. MonoRowBreaker is O(lineLen / charsPerRow) per line with no
        //    allocations — building the row index for 100K lines takes
        //    microseconds.  The TextLayout slow path creates a managed
        //    TextLayout object per line with full text shaping, which can
        //    be hundreds of milliseconds for the same line count.
        //
        // 2. For proportional fonts the row count from MonoRowBreaker
        //    will be approximate (charsPerRow is based on GetCharWidth
        //    which measures '0', roughly average glyph width).  The bias
        //    is systematic — row counts will be slightly over- or under-
        //    estimated depending on the text — but the error is small and
        //    consistent across lines, so the scroll-thumb position is
        //    still accurate.  This is the same approximation the estimate
        //    formula already makes.
        //
        // 3. DMEdit's primary target is monospace fonts, where
        //    MonoRowBreaker is exact.  Proportional fonts are a secondary
        //    use case that doesn't justify the 100× cost increase of
        //    per-line TextLayout measurement in the row index build.
        //
        // If a future change needs exact proportional-font row counts,
        // consider a background walk with TextLayout, populating the
        // row index asynchronously while the estimate handles the
        // transient.

        var counts = new int[lineCount];
        for (long i = 0; i < lineCount; i++) {
            counts[i] = ComputeLineRowCountForBuild(table, i, firstRowChars,
                contRowChars);
        }
        return LineIndexTree.FromValues(counts);
    }

    /// <summary>
    /// Per-line row count computation used during row-index build.
    /// Always uses <see cref="MonoRowBreaker.CountRows"/> — see the
    /// comment in <see cref="BuildRowIndexFor"/> for rationale.
    /// </summary>
    private static int ComputeLineRowCountForBuild(PieceTable table, long lineIdx,
            int firstRowChars, int contRowChars) {
        var lineStart = table.LineStartOfs(lineIdx);
        if (lineStart < 0) return 1; // streaming-load gap
        var len = table.LineContentLength((int)lineIdx);
        if (len <= 0) return 1;
        if (len > PieceTable.MaxGetTextLength) return 1; // too long, punt

        var text = table.GetText(lineStart, len);
        return MonoRowBreaker.CountRows(text, firstRowChars, contRowChars);
    }

    // -------------------------------------------------------------------------
    // Public query — used by status bar
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the total visual row count when wrapping is enabled, plus
    /// whether the count is exact (from the row index) or an estimate
    /// (char-density formula for docs above the build threshold).
    /// Returns <c>null</c> when wrapping is off or in char-wrap mode.
    /// </summary>
    public (long rows, bool isExact)? TotalVisualRows {
        get {
            if (!_wrapLines || _charWrapMode) return null;
            var doc = Document;
            if (doc == null) return null;
            var table = doc.Table;
            var lineCount = table.LineCount;
            if (lineCount <= 0) return null;

            var rowIdx = EnsureRowIndex();
            if (rowIdx != null) {
                // MonoRowBreaker is exact for monospace fonts but
                // approximate for proportional fonts (see comment in
                // BuildRowIndexFor).
                var exact = IsFontMonospace();
                return (rowIdx.TotalSum(), exact);
            }
            // Estimate for large docs.
            var maxW = Math.Max(100, (Bounds.Width > 0 ? Bounds.Width : 900) - _gutterWidth);
            var textW = GetTextWidth(maxW);
            var cpr = GetCharsPerRow(textW);
            if (cpr <= 0) return null;
            var totalChars = table.Length;
            var estRows = Math.Max(lineCount,
                (long)Math.Ceiling((double)totalChars / cpr));
            return (estRows, false);
        }
    }

    // -------------------------------------------------------------------------
    // Query helpers — used by scroll math
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the Y pixel position of the top of line <paramref name="lineIdx"/>
    /// using the exact row index when available.  Falls back to the
    /// char-density estimate for docs above the threshold.
    /// </summary>
    private double ExactOrEstimateLineY(long lineIdx, PieceTable table,
            int charsPerRow, double rh) {
        var rowIdx = EnsureRowIndex();
        if (rowIdx != null && lineIdx > 0) {
            return rowIdx.PrefixSum((int)lineIdx - 1) * rh;
        }
        if (rowIdx == null) {
            // Threshold exceeded — use the legacy estimate.
            return EstimateWrappedLineY(lineIdx, table, charsPerRow, rh);
        }
        return 0;
    }

    /// <summary>
    /// Returns the total extent in pixels (sum of all row counts × rh)
    /// using the exact row index when available, or the estimate otherwise.
    /// </summary>
    private long ExactOrEstimateTotalRows(PieceTable table, long lineCount,
            int charsPerRow) {
        var rowIdx = EnsureRowIndex();
        if (rowIdx != null) {
            return rowIdx.TotalSum();
        }
#if DEBUG
        // If the doc is below the threshold, the row index should exist.
        // Hitting this means EnsureRowIndex failed to build — likely a
        // charsPerRow mismatch or a bug in the build path.
        Debug.Assert(table.Length > RowIndexCharThreshold || !_wrapLines || lineCount <= 0,
            $"Estimate fallback used for {table.Length}-char doc (threshold "
            + $"{RowIndexCharThreshold}) — row index should have been available");
#endif
        // Char-density estimate (huge docs only).
        var estTotalChars = table.Length;
        return charsPerRow > 0
            ? Math.Max(lineCount, (long)Math.Ceiling((double)estTotalChars / charsPerRow))
            : lineCount;
    }

    /// <summary>
    /// Returns the top logical line that should appear at the given scroll
    /// offset Y using the exact row index when available.  Falls back to
    /// the estimate for huge docs.
    /// </summary>
    private long ExactOrEstimateTopLine(double scrollY, PieceTable table,
            long lineCount, int charsPerRow, double rh) {
        if (scrollY <= 0 || lineCount <= 0) return 0;
        var rowIdx = EnsureRowIndex();
        if (rowIdx != null) {
            // Find the line whose cumulative row count first reaches targetRow.
            var targetRow = (long)(scrollY / rh);
            // PrefixSum(i) returns rows through line i (inclusive).  We want
            // the smallest line index whose rows-through is strictly > targetRow.
            // That's FindByPrefixSum(targetRow + 1) translated to line space.
            var line = rowIdx.FindByPrefixSum(targetRow + 1);
            if (line < 0) return Math.Max(0, lineCount - 1);
            return Math.Clamp(line, 0, lineCount - 1);
        }
#if DEBUG
        Debug.Assert(table.Length > RowIndexCharThreshold || !_wrapLines || lineCount <= 0,
            $"Estimate fallback used for {table.Length}-char doc — row index "
            + "should have been available");
#endif
        // Char-density estimate (huge docs only).
        return EstimateWrappedTopLine(scrollY, table, lineCount, charsPerRow, rh);
    }
}
