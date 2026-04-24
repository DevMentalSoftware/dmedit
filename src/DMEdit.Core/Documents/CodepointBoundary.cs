namespace DMEdit.Core.Documents;

/// <summary>
/// Single source of truth for "what is one user-perceived character" in the
/// document, expressed in UTF-16 code-unit offsets.  Currently this means a
/// BMP code unit (1 char wide) or a surrogate pair (2 chars wide).
///
/// Every operation that needs to step the caret by one character, measure the
/// width of the cell under the caret, or align an offset to a code-point
/// boundary should go through here.  Adding a new such operation in-place
/// is the wrong move — extend this helper instead, so we never re-introduce
/// the "stranded half of a surrogate pair" class of bugs.
/// </summary>
public static class CodepointBoundary {
    /// <summary>
    /// Returns the width in UTF-16 code units of the code point at
    /// <paramref name="ofs"/>: 2 for a well-formed surrogate pair, 1 for
    /// anything else (BMP, lone surrogate, end of buffer).
    /// </summary>
    public static int WidthAt(PieceTable table, long ofs) {
        if (ofs < 0 || ofs >= table.Length) return 0;
        if (ofs + 1 >= table.Length) return 1;
        var pair = table.GetText(ofs, 2);
        return pair.Length == 2
               && char.IsHighSurrogate(pair[0])
               && char.IsLowSurrogate(pair[1])
            ? 2 : 1;
    }

    /// <summary>
    /// Returns the width in UTF-16 code units of the code point that ends at
    /// <paramref name="ofs"/> — i.e. the code point you would delete with
    /// Backspace at this caret position.
    /// </summary>
    public static int WidthBefore(PieceTable table, long ofs) {
        if (ofs <= 0 || ofs > table.Length) return 0;
        if (ofs < 2) return 1;
        var pair = table.GetText(ofs - 2, 2);
        return pair.Length == 2
               && char.IsHighSurrogate(pair[0])
               && char.IsLowSurrogate(pair[1])
            ? 2 : 1;
    }

    /// <summary>
    /// Advances <paramref name="ofs"/> by one code point.  Returns
    /// <c>ofs + 1</c> for BMP characters and <c>ofs + 2</c> when a
    /// surrogate pair starts at <paramref name="ofs"/>.  Clamped to
    /// <c>table.Length</c>.
    /// </summary>
    public static long StepRight(PieceTable table, long ofs) {
        var w = WidthAt(table, ofs);
        return w == 0 ? ofs : ofs + w;
    }

    /// <summary>
    /// Retreats <paramref name="ofs"/> by one code point.  Returns
    /// <c>ofs - 1</c> for BMP characters and <c>ofs - 2</c> when a
    /// surrogate pair ends just before <paramref name="ofs"/>.  Clamped to 0.
    /// </summary>
    public static long StepLeft(PieceTable table, long ofs) {
        var w = WidthBefore(table, ofs);
        return w == 0 ? ofs : ofs - w;
    }

    /// <summary>
    /// If <paramref name="ofs"/> falls between the high and low halves of a
    /// surrogate pair in <paramref name="table"/>, rounds it to the nearest
    /// pair boundary — toward the end of the pair when
    /// <paramref name="forward"/> is true, toward the start when false.
    /// No-op for every other offset.  Call this whenever code that computes
    /// selection or caret offsets via per-char predicates hands an offset
    /// back to anything that touches the buffer (DeleteRange, PushInsert,
    /// selection).
    /// </summary>
    public static long SnapToBoundary(PieceTable table, long ofs, bool forward) {
        if (ofs <= 0 || ofs >= table.Length) return ofs;
        // Read the two chars straddling ofs. If they are a well-formed pair,
        // ofs is splitting the pair.
        var pair = table.GetText(ofs - 1, 2);
        if (pair.Length == 2
            && char.IsHighSurrogate(pair[0])
            && char.IsLowSurrogate(pair[1])) {
            return forward ? ofs + 1 : ofs - 1;
        }
        return ofs;
    }

    // -------------------------------------------------------------------------
    // String / span overloads — for code that already has a materialized
    // window of text and wants to walk it by code points without going back
    // through the PieceTable.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Width in UTF-16 code units of the code point at <paramref name="idx"/>
    /// in <paramref name="text"/>: 2 for a well-formed surrogate pair, 1 for
    /// anything else, 0 if <paramref name="idx"/> is at or past the end.
    /// </summary>
    public static int WidthAt(ReadOnlySpan<char> text, int idx) {
        if ((uint)idx >= (uint)text.Length) return 0;
        if (idx + 1 < text.Length
            && char.IsHighSurrogate(text[idx])
            && char.IsLowSurrogate(text[idx + 1])) {
            return 2;
        }
        return 1;
    }

    /// <summary>
    /// Width in UTF-16 code units of the code point that ends at
    /// <paramref name="idx"/> in <paramref name="text"/> — the code point
    /// you would delete with Backspace at this position.
    /// </summary>
    public static int WidthBefore(ReadOnlySpan<char> text, int idx) {
        if (idx <= 0 || idx > text.Length) return 0;
        if (idx >= 2
            && char.IsHighSurrogate(text[idx - 2])
            && char.IsLowSurrogate(text[idx - 1])) {
            return 2;
        }
        return 1;
    }

    /// <summary>Advances <paramref name="idx"/> by one code point in <paramref name="text"/>.</summary>
    public static int StepRight(ReadOnlySpan<char> text, int idx) {
        var w = WidthAt(text, idx);
        return w == 0 ? idx : idx + w;
    }

    /// <summary>Retreats <paramref name="idx"/> by one code point in <paramref name="text"/>.</summary>
    public static int StepLeft(ReadOnlySpan<char> text, int idx) {
        var w = WidthBefore(text, idx);
        return w == 0 ? idx : idx - w;
    }

    /// <summary>
    /// If <paramref name="idx"/> falls between the high and low halves of a
    /// surrogate pair in <paramref name="text"/>, rounds it to the nearest
    /// pair boundary (forward or backward).  No-op otherwise.
    /// </summary>
    public static int SnapToBoundary(ReadOnlySpan<char> text, int idx, bool forward) {
        if (idx <= 0 || idx >= text.Length) return idx;
        if (char.IsHighSurrogate(text[idx - 1]) && char.IsLowSurrogate(text[idx])) {
            return forward ? idx + 1 : idx - 1;
        }
        return idx;
    }
}
