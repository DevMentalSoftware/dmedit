namespace DMEdit.Core.Documents;

/// <summary>
/// Shared word-break row-break helper used by every monospace plain-text
/// paginator/renderer in the codebase.  Previously duplicated in
/// <c>MonoLineLayout.NextRow</c> (DMEdit.Rendering) and
/// <c>WpfPrintService.PlainTextPaginator.NextRow</c> (DMEdit.Windows) as
/// comment-documented mirrors — every fix to one had to be manually mirrored
/// to the other.  Hoisting to Core guarantees pagination and on-screen/print
/// wrapping stay byte-identical.
/// </summary>
public static class MonoRowBreaker {
    /// <summary>
    /// Computes the next row break for a monospace word-wrap layout.
    ///
    /// <para>Rules:</para>
    /// <list type="bullet">
    ///   <item>If the remaining content fits in <paramref name="charsPerRow"/>,
    ///     the remainder is one row and <c>NextStart == line.Length</c>.</item>
    ///   <item>Otherwise, scan backward from the hard limit for a space; if
    ///     found, break at the last space — the space itself is dropped from
    ///     the drawn row (<c>DrawLen</c> excludes it) and <c>NextStart</c>
    ///     is the position after the space.</item>
    ///   <item>If no space exists inside the row width (long unbroken token),
    ///     fall back to a hard mid-token break at exactly
    ///     <paramref name="charsPerRow"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="line">The line being laid out.</param>
    /// <param name="rowStart">Starting position of the row inside <paramref name="line"/>.</param>
    /// <param name="charsPerRow">Maximum characters for this row (first-row width
    ///   or continuation-row width after hanging-indent subtraction).</param>
    /// <returns>
    /// <c>DrawLen</c>: number of characters to draw on this row.
    /// <c>NextStart</c>: position to begin the next row (past a soft-break
    /// space, or equal to <c>rowStart + charsPerRow</c> on a hard break).
    /// </returns>
    public static (int DrawLen, int NextStart) NextRow(string line, int rowStart, int charsPerRow) {
        var remaining = line.Length - rowStart;
        if (remaining <= charsPerRow) {
            return (remaining, line.Length);
        }
        var hardLimit = rowStart + charsPerRow;
        for (var i = hardLimit - 1; i > rowStart; i--) {
            if (line[i] == ' ') {
                return (i - rowStart, i + 1);
            }
        }
        return (charsPerRow, hardLimit);
    }
}
