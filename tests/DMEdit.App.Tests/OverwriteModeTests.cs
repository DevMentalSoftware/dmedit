using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for overwrite mode: insert-key toggle, overwrite-insert logic,
/// and caret layer state.
/// </summary>
public class OverwriteModeTests {
    private const double VpW = 600;
    private const double VpH = 400;

    private static EditorControl CreateEditor(string content) {
        var doc = new Document();
        doc.Insert(content);
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = false,
        };
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
        return editor;
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
    }

    private static string GetAllText(EditorControl e) {
        var table = e.Document!.Table;
        return table.GetText(0, (int)table.Length);
    }

    // ==================================================================
    //  Toggle
    // ==================================================================

    [AvaloniaFact]
    public void OverwriteMode_DefaultsFalse() {
        var editor = CreateEditor("abc\n");
        Assert.False(editor.OverwriteMode);
    }

    [AvaloniaFact]
    public void OverwriteMode_Toggle_Roundtrips() {
        var editor = CreateEditor("abc\n");
        editor.OverwriteMode = true;
        Assert.True(editor.OverwriteMode);
        editor.OverwriteMode = false;
        Assert.False(editor.OverwriteMode);
    }

    [AvaloniaFact]
    public void OverwriteMode_FiresChangedEvent() {
        var editor = CreateEditor("abc\n");
        var fired = 0;
        editor.OverwriteModeChanged += (_, _) => fired++;
        editor.OverwriteMode = true;
        Assert.Equal(1, fired);
        editor.OverwriteMode = false;
        Assert.Equal(2, fired);
    }

    // ==================================================================
    //  Overwrite-insert logic
    // ==================================================================

    [AvaloniaFact]
    public void Overwrite_SingleChar_ReplacesFirst() {
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.Document!.Selection = Selection.Collapsed(0);

        editor.TypeTextForTest("X");
        Relayout(editor);

        Assert.Equal("Xbcdef\n", GetAllText(editor));
        Assert.Equal(1, editor.Document.Selection.Caret);
    }

    [AvaloniaFact]
    public void Overwrite_MultipleChars_ReplacesSequentially() {
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.Document!.Selection = Selection.Collapsed(0);

        editor.TypeTextForTest("XY");
        Relayout(editor);

        Assert.Equal("XYcdef\n", GetAllText(editor));
        Assert.Equal(2, editor.Document.Selection.Caret);
    }

    [AvaloniaFact]
    public void Overwrite_MidLine_ReplacesAtCaret() {
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.Document!.Selection = Selection.Collapsed(3);

        editor.TypeTextForTest("XY");
        Relayout(editor);

        Assert.Equal("abcXYf\n", GetAllText(editor));
        Assert.Equal(5, editor.Document.Selection.Caret);
    }

    [AvaloniaFact]
    public void Overwrite_AtEndOfLine_DoesNotConsumeNewline() {
        var editor = CreateEditor("abc\ndef\n");
        editor.OverwriteMode = true;
        editor.Document!.Selection = Selection.Collapsed(3); // at '\n'

        editor.TypeTextForTest("X");
        Relayout(editor);

        // Should insert before the newline, not consume it.
        Assert.Equal("abcX\ndef\n", GetAllText(editor));
    }

    [AvaloniaFact]
    public void Overwrite_AtEndOfDocument_AppendsInsteadOfReplace() {
        var editor = CreateEditor("abc");
        editor.OverwriteMode = true;
        editor.Document!.Selection = Selection.Collapsed(3); // at end

        editor.TypeTextForTest("X");
        Relayout(editor);

        Assert.Equal("abcX", GetAllText(editor));
    }

    [AvaloniaFact]
    public void Overwrite_WithSelection_ReplacesSelection_NotOverwrite() {
        // When a selection is active, overwrite mode is irrelevant:
        // the selection is replaced (same as insert mode).
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.Document!.Selection = new Selection(1, 4); // "bcd"

        editor.TypeTextForTest("Z");
        Relayout(editor);

        Assert.Equal("aZef\n", GetAllText(editor));
    }

    [AvaloniaFact]
    public void Overwrite_InsertMode_InsertsWithoutReplace() {
        var editor = CreateEditor("abcdef\n");
        // OverwriteMode is false by default.
        editor.Document!.Selection = Selection.Collapsed(0);

        editor.TypeTextForTest("XY");
        Relayout(editor);

        Assert.Equal("XYabcdef\n", GetAllText(editor));
    }

    // ==================================================================
    //  IsEditBlocked interaction
    // ==================================================================

    [AvaloniaFact]
    public void Overwrite_IsEditBlocked_NoInsert() {
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.IsEditBlocked = true;
        editor.Document!.Selection = Selection.Collapsed(0);

        editor.TypeTextForTest("X");
        Relayout(editor);

        // Text unchanged — edit was blocked.
        Assert.Equal("abcdef\n", GetAllText(editor));
    }
}
