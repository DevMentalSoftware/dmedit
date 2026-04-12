using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for the IsLoading / IsEditBlocked interaction-locking guards.
///
/// <para>Since we can't easily create a streaming-load buffer in headless
/// tests, we exercise the guards via the <c>IsEditBlocked</c> property
/// (a simple settable bool that the streaming-load path sets at runtime)
/// and verify the resulting behavior.</para>
/// </summary>
public class IsLoadingTests {
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
    //  IsEditBlocked — text input
    // ==================================================================

    [AvaloniaFact]
    public void EditBlocked_TypeText_NoChange() {
        var editor = CreateEditor("hello\n");
        editor.IsEditBlocked = true;

        editor.TypeTextForTest("X");
        Relayout(editor);

        Assert.Equal("hello\n", GetAllText(editor));
    }

    [AvaloniaFact]
    public void EditBlocked_Cleared_TypingWorks() {
        var editor = CreateEditor("hello\n");
        editor.IsEditBlocked = true;

        editor.TypeTextForTest("X");
        Assert.Equal("hello\n", GetAllText(editor));

        editor.IsEditBlocked = false;
        editor.TypeTextForTest("X");
        Relayout(editor);

        Assert.Equal("Xhello\n", GetAllText(editor));
    }

    // ==================================================================
    //  IsEditBlocked — overwrite mode respects the block
    // ==================================================================

    [AvaloniaFact]
    public void EditBlocked_OverwriteMode_NoChange() {
        var editor = CreateEditor("abcdef\n");
        editor.OverwriteMode = true;
        editor.IsEditBlocked = true;

        editor.TypeTextForTest("X");
        Relayout(editor);

        Assert.Equal("abcdef\n", GetAllText(editor));
    }

    // ==================================================================
    //  Navigation still works while editing is blocked
    // ==================================================================

    [AvaloniaFact]
    public void EditBlocked_NavigationStillWorks() {
        var editor = CreateEditor("first\nsecond\n");
        editor.IsEditBlocked = true;

        // Navigation commands (e.g., move right) should still work
        // because they're in the "Nav" category, not "Edit".
        editor.MoveCaretHorizontalForTest(+1, false, false);
        Relayout(editor);

        Assert.Equal(1, editor.Document!.Selection.Caret);
    }

    // ==================================================================
    //  OverwriteMode toggle allowed while editing is blocked
    // ==================================================================

    [AvaloniaFact]
    public void EditBlocked_OverwriteToggle_Allowed() {
        var editor = CreateEditor("abc\n");
        editor.IsEditBlocked = true;

        // The overwrite toggle is explicitly whitelisted in the
        // IsEditBlocked guard (Commands.cs line 394).
        editor.OverwriteMode = true;
        Assert.True(editor.OverwriteMode);
    }
}
