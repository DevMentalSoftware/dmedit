namespace DMEdit.Core.Documents;

/// <summary>
/// Unambiguous visual position in a text document: identifies WHICH logical
/// line, WHICH visual row within that line (for wrapped content), WHICH
/// character position within that row, and carries the global character
/// offset for free O(1) round-trips to edit/selection APIs.
/// </summary>
/// <remarks>
/// Replaces the logical-offset-plus-affinity model (the old
/// <c>_caretIsAtEnd</c> flag) for caret state.  Two <see cref="TextPosition"/>
/// values with the same <see cref="CharOffset"/> but different
/// <see cref="RowInLine"/> / <see cref="Col"/> are legitimately distinct
/// positions at a wrap boundary (end of previous row vs start of next row).
///
/// <para>
/// <see cref="Col"/> is the raw character position within the row, NOT the
/// tab-expanded display column.  Rendering computes the display column via
/// <see cref="MonoRowBreaker.ColumnOfChar"/> when tabs are present.
/// </para>
///
/// <para>
/// Named <c>TextPosition</c> rather than <c>VisualPos</c> in anticipation of
/// a future <c>IPosition</c> abstraction covering other document types
/// (block / WYSIWYG).  For plain text the row/col/offset triple is enough.
/// </para>
/// </remarks>
public readonly record struct TextPosition(
    long LineIdx,
    int RowInLine,
    int Col,
    long CharOffset);
