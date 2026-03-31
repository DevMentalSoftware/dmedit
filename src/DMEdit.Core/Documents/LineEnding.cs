namespace DMEdit.Core.Documents;

/// <summary>
/// Represents the line-ending style of a document.
/// </summary>
public enum LineEnding {
    /// <summary>Unix-style: <c>\n</c></summary>
    LF,

    /// <summary>Windows-style: <c>\r\n</c></summary>
    CRLF,

    /// <summary>Classic Mac-style: <c>\r</c></summary>
    CR,
}

/// <summary>
/// Result of detecting line endings in a text buffer.
/// </summary>
public readonly record struct LineEndingInfo(LineEnding Dominant, bool IsMixed,
    int LfCount = 0, int CrlfCount = 0, int CrCount = 0) {
    /// <summary>Display label for the status bar (e.g. "LF", "CRLF", "CR").</summary>
    public string Label => Dominant switch {
        LineEnding.LF => "LF",
        LineEnding.CRLF => "CRLF",
        LineEnding.CR => "CR",
        _ => "LF",
    };

    /// <summary>The actual newline string to insert for this ending style.</summary>
    public string NewlineString => Dominant switch {
        LineEnding.LF => "\n",
        LineEnding.CRLF => "\r\n",
        LineEnding.CR => "\r",
        _ => "\n",
    };

    /// <summary>
    /// Returns the platform-appropriate default line ending.
    /// </summary>
    public static LineEndingInfo PlatformDefault =>
        new(Environment.NewLine == "\r\n" ? LineEnding.CRLF : LineEnding.LF, false);

    /// <summary>
    /// Detects the predominant line ending in a string.
    /// Returns the dominant style and whether the text has mixed endings.
    /// </summary>
    public static LineEndingInfo Detect(string text) {
        int lf = 0, crlf = 0, cr = 0;

        for (var i = 0; i < text.Length; i++) {
            var ch = text[i];
            if (ch == '\r') {
                if (i + 1 < text.Length && text[i + 1] == '\n') {
                    crlf++;
                    i++; // skip \n of \r\n pair
                } else {
                    cr++;
                }
            } else if (ch == '\n') {
                lf++;
            }
        }

        return FromCounts(lf, crlf, cr);
    }

    /// <summary>
    /// Detects the predominant line ending by scanning an <see cref="Buffers.IBuffer"/>.
    /// Only scans up to <paramref name="sampleLen"/> characters for performance.
    /// </summary>
    public static LineEndingInfo Detect(Buffers.IBuffer buffer, int sampleLen = 64 * 1024) {
        int lf = 0, crlf = 0, cr = 0;
        var len = Math.Min(buffer.Length, sampleLen);

        for (var i = 0L; i < len; i++) {
            var ch = buffer[i];
            if (ch == '\r') {
                if (i + 1 < len && buffer[i + 1] == '\n') {
                    crlf++;
                    i++; // skip \n of \r\n pair
                } else {
                    cr++;
                }
            } else if (ch == '\n') {
                lf++;
            }
        }

        return FromCounts(lf, crlf, cr);
    }

    /// <summary>
    /// Builds a <see cref="LineEndingInfo"/> from explicit counts.
    /// Returns <see cref="PlatformDefault"/> when all counts are zero.
    /// </summary>
    public static LineEndingInfo FromCounts(int lf, int crlf, int cr) {
        var total = lf + crlf + cr;
        if (total == 0) {
            return PlatformDefault;
        }

        // Determine which style has the most occurrences.
        var dominant = LineEnding.LF;
        var maxCount = lf;
        if (crlf > maxCount) {
            dominant = LineEnding.CRLF;
            maxCount = crlf;
        }
        if (cr > maxCount) {
            dominant = LineEnding.CR;
        }

        // Mixed = more than one style present.
        var styles = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return new LineEndingInfo(dominant, styles > 1, lf, crlf, cr);
    }
}
