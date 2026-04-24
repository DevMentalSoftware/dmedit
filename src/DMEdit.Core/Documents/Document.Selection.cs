using System.Buffers;
using System.Text;

namespace DMEdit.Core.Documents;

// Word and selection expansion partial of Document.  Owns SelectWord,
// SelectLine, ExpandSelection, and the private helpers IsWordRune,
// IsSubwordBoundary, and GetLineContentRange.
public sealed partial class Document {

    // -------------------------------------------------------------------------
    // Word / selection operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Expands the current selection outward to alphanumeric boundaries within
    /// the current line. No-op if the selection already contains a
    /// non-alphanumeric character or spans multiple lines. When collapsed
    /// (caret only), expands around the caret position.
    /// </summary>
    public void SelectWord() {
        var len = _table.Length;
        if (len == 0) {
            return;
        }

        var selStart = Selection.Start;
        var selEnd = Selection.End;

        var (lineStart, lineEnd) = GetLineContentRange(selStart);

        // If selection spans multiple lines, treat as containing non-word chars → no-op.
        if (!Selection.IsEmpty && selEnd > lineEnd) {
            return;
        }

        // Work in a bounded window around the selection so we don't
        // materialize an entire line (could be multi-GB for single-line files).
        // Start with a small window for the common case (selection in the
        // middle of a normal line), and double the radius if word expansion
        // hits a window edge that isn't also a line edge — that's the only
        // case where the previous fixed-1024 clamp silently truncated a long
        // identifier.  Doubling caps re-materialization at O(log finalSize).
        var radius = 1024;
        while (true) {
            var winStart = Math.Max(lineStart, selStart - radius);
            var winEnd = Math.Min(lineEnd, selEnd + radius);
            var winLen = (int)(winEnd - winStart);
            if (winLen == 0) {
                return;
            }
            var winText = _table.GetText(winStart, winLen);

            var selStartInWin = (int)(selStart - winStart);
            var selEndInWin = (int)(selEnd - winStart);

            // If selection contains a non-word code point → no-op.  Walks by
            // rune so that a surrogate pair is treated as one unit.
            for (var i = selStartInWin; i < selEndInWin; ) {
                if (!IsWordRune(winText, i)) {
                    return;
                }
                i = CodepointBoundary.StepRight(winText, i);
            }

            // Expand backward from selection start to non-word or window start.
            var left = selStartInWin;
            while (left > 0) {
                var prev = CodepointBoundary.StepLeft(winText, left);
                if (!IsWordRune(winText, prev)) break;
                left = prev;
            }

            // Expand forward from selection end to non-word or window end.
            var right = selEndInWin;
            while (right < winLen) {
                if (!IsWordRune(winText, right)) break;
                right = CodepointBoundary.StepRight(winText, right);
            }

            // If we ran into a window edge that isn't also the line edge, the
            // word may extend further — double the radius and re-scan.  When
            // either edge IS the line edge, we've found the true boundary.
            var leftHitInnerEdge = left == 0 && winStart > lineStart;
            var rightHitInnerEdge = right == winLen && winEnd < lineEnd;
            if (!leftHitInnerEdge && !rightHitInnerEdge) {
                Selection = new Selection(winStart + left, winStart + right);
                return;
            }

            // Double radius (with overflow guard).  In practice this loop
            // terminates after a handful of iterations even for very long
            // identifiers because the radius grows geometrically.
            if (radius > int.MaxValue / 2) {
                // Saturate; on the next iteration the line bounds will clamp.
                radius = int.MaxValue;
            } else {
                radius *= 2;
            }
        }
    }

    /// <summary>
    /// Returns true if the Unicode code point starting at
    /// <paramref name="idx"/> in <paramref name="text"/> is a word character
    /// (letter, digit, or underscore).  Rune-aware so a surrogate pair
    /// representing a non-BMP letter counts as one word character instead
    /// of two non-word halves.  Lone surrogates are never word characters.
    /// </summary>
    private static bool IsWordRune(ReadOnlySpan<char> text, int idx) {
        if ((uint)idx >= (uint)text.Length) return false;
        var status = Rune.DecodeFromUtf16(text[idx..], out var rune, out _);
        if (status != System.Buffers.OperationStatus.Done) return false;
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_';
    }

    /// <summary>
    /// Selects the content of the line containing the caret (excluding the
    /// line ending). Used for triple-click.
    /// </summary>
    public void SelectLine() {
        if (_table.Length == 0) {
            return;
        }
        var caret = Selection.Caret;
        var (lineStart, lineEnd) = GetLineContentRange(caret);
        Selection = new Selection(lineStart, lineEnd);
    }

    /// <summary>
    /// Expands the selection outward through progressively broader levels.
    /// The levels depend on the <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="ExpandSelectionMode.SubwordFirst"/>: subword → whitespace → line → document</item>
    ///   <item><see cref="ExpandSelectionMode.Word"/>: whitespace → line → document</item>
    /// </list>
    /// Each invocation detects the current level by inspecting selection boundaries
    /// and advances to the next level.
    /// </summary>
    public void ExpandSelection(ExpandSelectionMode mode) {
        var len = _table.Length;
        if (len == 0) {
            return;
        }
        var docLen = _table.Length;

        // Already the entire document?
        if (Selection.Start == 0 && Selection.End == docLen) {
            return;
        }

        var selStart = Selection.Start;
        var selEnd = Selection.End;

        var (lineStart, lineEnd) = GetLineContentRange(selStart);

        // If selection spans beyond this line → expand to entire document.
        if (!Selection.IsEmpty && selEnd > lineEnd) {
            Selection = new Selection(0L, docLen);
            return;
        }

        // Already the entire line?
        if (selStart == lineStart && selEnd == lineEnd) {
            Selection = new Selection(0L, docLen);
            return;
        }

        // Use a bounded window to avoid materializing a multi-GB single line.
        const int windowRadius = 1024;
        var winStart = Math.Max(lineStart, selStart - windowRadius);
        var winEnd = Math.Min(lineEnd, selEnd + windowRadius);
        var winLen = (int)(winEnd - winStart);
        var winText = winLen > 0 ? _table.GetText(winStart, winLen) : "";
        var selStartInWin = (int)(selStart - winStart);
        var selEndInWin = (int)(selEnd - winStart);

        // Compute whitespace-bounded range.  Whitespace is always BMP, so
        // stopping on a whitespace transition is pair-aligned even when
        // walking by code units — but we still step by code points so any
        // "stopped in non-whitespace" branch never lands inside a pair.
        var wsLeft = selStartInWin;
        while (wsLeft > 0) {
            var prev = CodepointBoundary.StepLeft(winText, wsLeft);
            if (char.IsWhiteSpace(winText[prev])) break;
            wsLeft = prev;
        }
        var wsRight = selEndInWin;
        while (wsRight < winLen && !char.IsWhiteSpace(winText[wsRight])) {
            wsRight = CodepointBoundary.StepRight(winText, wsRight);
        }

        var atWhitespaceBoundary = selStartInWin == wsLeft && selEndInWin == wsRight;

        if (mode == ExpandSelectionMode.SubwordFirst && !atWhitespaceBoundary) {
            // Try subword expansion, constrained within the whitespace-bounded word.
            var subLeft = selStartInWin;
            if (subLeft > wsLeft) {
                subLeft = CodepointBoundary.StepLeft(winText, subLeft);
                while (subLeft > wsLeft && !IsSubwordBoundary(winText, subLeft)) {
                    subLeft = CodepointBoundary.StepLeft(winText, subLeft);
                }
            }
            var subRight = selEndInWin;
            if (subRight < wsRight) {
                subRight = CodepointBoundary.StepRight(winText, subRight);
                while (subRight < wsRight && !IsSubwordBoundary(winText, subRight)) {
                    subRight = CodepointBoundary.StepRight(winText, subRight);
                }
            }

            var expanded = subLeft != selStartInWin || subRight != selEndInWin;
            var alreadyAtWhitespace = subLeft == wsLeft && subRight == wsRight;
            if (expanded && !alreadyAtWhitespace) {
                Selection = new Selection(
                    winStart + subLeft,
                    winStart + subRight);
                return;
            }
        }

        // Whitespace boundary level.
        if (!atWhitespaceBoundary) {
            Selection = new Selection(
                winStart + wsLeft,
                winStart + wsRight);
            return;
        }

        // Line level.
        Selection = new Selection(lineStart, lineEnd);
    }

    /// <summary>
    /// Returns (lineStart, lineEnd) where lineEnd is the offset of the first
    /// line-ending character (or document length). This gives the "content"
    /// range of the line excluding \r\n / \n / \r.
    /// </summary>
    private (long lineStart, long lineEnd) GetLineContentRange(long ofs) {
        var line = _table.LineFromOfs(Math.Min(ofs, _table.Length));
        var lineStart = _table.LineStartOfs(line);
        long lineEnd;
        if (line + 1 < _table.LineCount) {
            var nextLineStart = _table.LineStartOfs(line + 1);
            // Strip trailing \r\n, \n, or \r from the end.
            lineEnd = nextLineStart;
            if (lineEnd > lineStart) {
                var tail = _table.GetText(Math.Max(lineStart, lineEnd - 2), (int)Math.Min(2, lineEnd - lineStart));
                if (tail.EndsWith("\r\n")) {
                    lineEnd -= 2;
                } else if (tail.EndsWith("\n") || tail.EndsWith("\r")) {
                    lineEnd -= 1;
                }
            }
        } else {
            lineEnd = _table.Length;
        }
        return (lineStart, lineEnd);
    }

    /// <summary>
    /// Returns true if position <paramref name="i"/> in the text is a subword
    /// boundary: camelCase transitions, underscore, digit/letter transitions,
    /// or non-alphanumeric characters.  Rune-aware: a position inside a
    /// surrogate pair is never a boundary, and the predicates run against
    /// whole code points.
    /// </summary>
    private static bool IsSubwordBoundary(ReadOnlySpan<char> text, int i) {
        if (i <= 0 || i >= text.Length) {
            return false;
        }
        // Inside a surrogate pair is not a valid boundary.
        if (char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) {
            return false;
        }

        // Decode the rune ending at i (one code point back from i).
        var prevStart = CodepointBoundary.StepLeft(text, i);
        if (Rune.DecodeFromUtf16(text[prevStart..i], out var prev, out _)
            != System.Buffers.OperationStatus.Done) {
            return true;
        }
        // Decode the rune starting at i.
        if (Rune.DecodeFromUtf16(text[i..], out var curr, out _)
            != System.Buffers.OperationStatus.Done) {
            return true;
        }

        // Non-alphanumeric on either side is always a boundary.
        if (!Rune.IsLetterOrDigit(prev) || !Rune.IsLetterOrDigit(curr)) {
            return true;
        }
        // lowercase → Uppercase  (e.g. "camelCase" → boundary before 'C')
        if (Rune.IsLower(prev) && Rune.IsUpper(curr)) {
            return true;
        }
        // Uppercase → Uppercase+lowercase  (e.g. "HTMLParser" → boundary before 'P')
        if (Rune.IsUpper(prev) && Rune.IsUpper(curr)) {
            var afterCurr = i + curr.Utf16SequenceLength;
            if (afterCurr < text.Length
                && Rune.DecodeFromUtf16(text[afterCurr..], out var next, out _)
                   == System.Buffers.OperationStatus.Done
                && Rune.IsLower(next)) {
                return true;
            }
        }
        // digit ↔ letter transition
        if (Rune.IsDigit(prev) != Rune.IsDigit(curr)) {
            return true;
        }
        return false;
    }
}
