using System.Text;
using DMEdit.Core.Documents.History;

namespace DMEdit.Core.Documents;

// Case transformation, bulk replace, and line ending / indentation
// conversion partial of Document.  Owns TransformCase, ToProperCase,
// BulkReplaceUniform, BulkReplaceVarying, ConvertLineEndings, and
// ConvertIndentation.
public sealed partial class Document {

    // -------------------------------------------------------------------------
    // Case transformation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Transforms the case of the selected text. No-op if selection is empty.
    /// Uses a compound edit so undo reverts in one step.
    /// </summary>
    public void TransformCase(CaseTransform transform) {
        if (Selection.IsEmpty) {
            return;
        }
        var start = Selection.Start;
        var selLen = (int)Selection.Len;
        var sb = new StringBuilder(selLen);
        _table.ForEachPiece(start, selLen, span => sb.Append(span));
        var original = sb.ToString();

        var transformed = transform switch {
            CaseTransform.Upper => original.ToUpperInvariant(),
            CaseTransform.Lower => original.ToLowerInvariant(),
            CaseTransform.Proper => ToProperCase(original),
            _ => original,
        };

        if (transformed == original) {
            return;
        }

        _history.BeginCompound();
        DeleteRange(start, selLen);
        PushInsert(start, transformed);
        _history.EndCompound();

        // Preserve selection over the transformed text
        Selection = new Selection(start, start + transformed.Length);
        RaiseChanged();
    }

    private static string ToProperCase(string text) {
        var chars = text.ToCharArray();
        var newWord = true;
        for (var i = 0; i < chars.Length; i++) {
            if (char.IsWhiteSpace(chars[i]) || chars[i] == '-' || chars[i] == '_') {
                newWord = true;
            } else if (newWord) {
                chars[i] = char.ToUpperInvariant(chars[i]);
                newWord = false;
            } else {
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
        }
        return new string(chars);
    }

    // -------------------------------------------------------------------------
    // Bulk replace
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uniform bulk replace: all matches have the same length and the same
    /// replacement string.  Single undo entry, one line tree rebuild.
    /// </summary>
    public int BulkReplaceUniform(long[] matchPositions, int matchLen, string replacement) {
        if (matchPositions.Length == 0) return 0;

        var savedPieces = _table.SnapshotPieces();
        var savedLines = _table.SnapshotLineLengths();
        var savedAddLen = _table.AddBufferLength;

        var edit = new UniformBulkReplaceEdit(
            matchPositions, matchLen, replacement,
            savedPieces, savedLines, savedAddLen);
        _history.Push(edit, _table, Selection);

        Selection = Selection.Collapsed(Math.Min(Selection.Caret, _table.Length));
        RaiseChanged();
        return matchPositions.Length;
    }

    /// <summary>
    /// Varying bulk replace: matches have different lengths and/or different
    /// replacements (e.g. regex replace, indentation conversion).
    /// Single undo entry, one line tree rebuild.
    /// </summary>
    public int BulkReplaceVarying((long Pos, int Len)[] matches, string[] replacements) {
        if (matches.Length == 0) return 0;

        var savedPieces = _table.SnapshotPieces();
        var savedLines = _table.SnapshotLineLengths();
        var savedAddLen = _table.AddBufferLength;

        var edit = new VaryingBulkReplaceEdit(
            matches, replacements,
            savedPieces, savedLines, savedAddLen);
        _history.Push(edit, _table, Selection);

        Selection = Selection.Collapsed(Math.Min(Selection.Caret, _table.Length));
        RaiseChanged();
        return matches.Length;
    }

    // -------------------------------------------------------------------------
    // Line ending conversion
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets the document's line ending style. No physical edit is performed —
    /// the actual conversion happens at save time in <see cref="IO.FileSaver"/>.
    /// New lines typed by the user already use <see cref="LineEndingInfo.NewlineString"/>.
    /// </summary>
    public void ConvertLineEndings(LineEnding target) {
        LineEndingInfo = new LineEndingInfo(target, false);
    }

    // -------------------------------------------------------------------------
    // Indentation conversion
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts all leading indentation in the document between tabs and spaces.
    /// Uses bulk replace — no full-document string materialization.
    /// </summary>
    public void ConvertIndentation(IndentStyle target, int tabSize = 4) {
        var spacesStr = new string(' ', tabSize);

        // Phase 1: walk the document to find indent regions that need changing.
        var matches = new List<(long Pos, int Len)>();
        var replacements = new List<string>();
        var leadingBuf = new StringBuilder();
        var atLineStart = true;
        var indentStart = 0L;
        var docPos = 0L;

        void FlushLeading() {
            if (leadingBuf.Length == 0) return;
            var before = leadingBuf.ToString();
            string after;
            if (target == IndentStyle.Spaces) {
                var sb = new StringBuilder(before.Length * tabSize);
                foreach (var c in before) {
                    if (c == '\t') { sb.Append(spacesStr); }
                    else { sb.Append(c); }
                }
                after = sb.ToString();
            } else {
                var expandedSpaces = 0;
                foreach (var c in before) {
                    if (c == '\t') { expandedSpaces += tabSize; }
                    else { expandedSpaces++; }
                }
                var wholeTabs = expandedSpaces / tabSize;
                var remainSpaces = expandedSpaces % tabSize;
                after = new string('\t', wholeTabs) + new string(' ', remainSpaces);
            }
            if (after != before) {
                matches.Add((indentStart, before.Length));
                replacements.Add(after);
            }
            leadingBuf.Clear();
        }

        _table.ForEachPiece(0, _table.Length, span => {
            foreach (var ch in span) {
                if (atLineStart && (ch == ' ' || ch == '\t')) {
                    if (leadingBuf.Length == 0) {
                        indentStart = docPos;
                    }
                    leadingBuf.Append(ch);
                    docPos++;
                    continue;
                }
                if (atLineStart) {
                    FlushLeading();
                    atLineStart = false;
                }
                docPos++;
                if (ch == '\n' || ch == '\r') {
                    atLineStart = true;
                }
            }
        });
        // Flush any trailing leading whitespace (file ends with whitespace-only line).
        if (atLineStart) FlushLeading();

        if (matches.Count == 0) {
            IndentInfo = new IndentInfo(target, false);
            return;
        }

        BulkReplaceVarying(matches.ToArray(), replacements.ToArray());
        IndentInfo = new IndentInfo(target, false);
    }
}
