using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

/// <summary>
/// Test-only helpers for asserting caret state.  These derive layout-
/// dependent predicates from <see cref="EditorControl.CaretPosition"/>
/// using the editor's internal layout accessor.  No production code
/// needs them — <see cref="EditorControl.CaretPosition"/>'s row/col
/// fields fully describe the caret's visual placement.
/// </summary>
internal static class CaretTestHelpers {
    /// <summary>
    /// True when the caret is parked at the end of a non-final visual
    /// row — the upstream side of a wrap boundary, where its char
    /// offset coincides with the start of the next row but visually
    /// it sits on the previous row.
    /// </summary>
    internal static bool CaretIsAtEnd(this EditorControl editor) {
        if (editor.CaretPosition is not { } pos) {
            return false;
        }
        if (editor.CharWrapMode && editor.CharsPerRow > 0) {
            return pos.Col == editor.CharsPerRow;
        }
        var layout = editor.CurrentLayoutForTest;
        if (layout == null) {
            return false;
        }
        var localOfs = pos.CharOffset - layout.ViewportBase;
        for (var i = layout.Lines.Count - 1; i >= 0; i--) {
            if (layout.Lines[i].CharStart <= localOfs) {
                var ll = layout.Lines[i];
                if (ll.Mono is not { } mono
                        || pos.RowInLine < 0
                        || pos.RowInLine >= mono.Rows.Length) {
                    return false;
                }
                return pos.RowInLine + 1 < mono.Rows.Length
                    && pos.Col == mono.Rows[pos.RowInLine].CharLen;
            }
        }
        return false;
    }
}
