using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for the Preserve Case replacement feature.  Covers the static
/// <see cref="EditorControl.ApplyPreserveCase"/> helper and the
/// end-to-end <see cref="EditorControl.ReplaceCurrent"/> path.
/// </summary>
public class PreserveCaseTests {

    // =================================================================
    //  ApplyPreserveCase unit tests
    // =================================================================

    [Fact]
    public void AllUpper_ReplacementIsUppercased() {
        Assert.Equal("WORLD", EditorControl.ApplyPreserveCase("HELLO", "world"));
    }

    [Fact]
    public void AllLower_ReplacementIsLowercased() {
        Assert.Equal("world", EditorControl.ApplyPreserveCase("hello", "WORLD"));
    }

    [Fact]
    public void TitleCase_ReplacementIsTitleCased() {
        Assert.Equal("World", EditorControl.ApplyPreserveCase("Hello", "world"));
    }

    [Fact]
    public void MixedCase_ReplacementUnchanged() {
        // "hELLO" is mixed — not all-upper, not all-lower, not title
        Assert.Equal("world", EditorControl.ApplyPreserveCase("hELLO", "world"));
    }

    [Fact]
    public void AllUpper_WithDigits_StillUppercased() {
        // "ABC123" has no lowercase letters → all-upper
        Assert.Equal("XYZ", EditorControl.ApplyPreserveCase("ABC123", "xyz"));
    }

    [Fact]
    public void TitleCase_WithTrailingDigits() {
        // "Hello123" — first upper, remaining letters all lower
        Assert.Equal("World", EditorControl.ApplyPreserveCase("Hello123", "world"));
    }

    [Fact]
    public void SingleUpperChar_TreatedAsAllUpper() {
        Assert.Equal("Y", EditorControl.ApplyPreserveCase("X", "y"));
    }

    [Fact]
    public void SingleLowerChar_TreatedAsAllLower() {
        Assert.Equal("y", EditorControl.ApplyPreserveCase("x", "Y"));
    }

    [Fact]
    public void EmptyMatch_ReturnsReplacementUnchanged() {
        Assert.Equal("world", EditorControl.ApplyPreserveCase("", "world"));
    }

    [Fact]
    public void EmptyReplacement_ReturnsEmpty() {
        Assert.Equal("", EditorControl.ApplyPreserveCase("HELLO", ""));
    }

    [Fact]
    public void NoLetters_ReturnsReplacementUnchanged() {
        // "123" has no letters → neither upper nor lower
        Assert.Equal("abc", EditorControl.ApplyPreserveCase("123", "abc"));
    }

    [Fact]
    public void TitleCase_MultiWord() {
        // "Hello world" — first letter upper, rest lower
        Assert.Equal("Goodbye", EditorControl.ApplyPreserveCase("Hello world", "goodbye"));
    }

    [Fact]
    public void CamelCase_IsNotTitleCase() {
        // "camelCase" — first char lower, mixed
        Assert.Equal("newName", EditorControl.ApplyPreserveCase("camelCase", "newName"));
    }

    [Fact]
    public void PascalCase_IsMixedNotTitle() {
        // "HelloWorld" — has uppercase after first char
        Assert.Equal("goodbye", EditorControl.ApplyPreserveCase("HelloWorld", "goodbye"));
    }

    [Fact]
    public void AllUpper_LongerReplacement() {
        Assert.Equal("GOODBYE WORLD", EditorControl.ApplyPreserveCase("HELLO", "goodbye world"));
    }

    [Fact]
    public void TitleCase_LongerReplacement() {
        Assert.Equal("Goodbye world", EditorControl.ApplyPreserveCase("Hello", "goodbye world"));
    }

    // =================================================================
    //  End-to-end ReplaceCurrent with preserveCase
    // =================================================================

    private const double W = 800;
    private const double H = 400;

    private static EditorControl CreateEditor(string text) {
        var doc = new Document();
        doc.Insert(text);
        doc.Selection = Selection.Collapsed(0);
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = W,
            Height = H,
        };
        editor.Measure(new Size(W, H));
        editor.Arrange(new Rect(0, 0, W, H));
        return editor;
    }

    private static string AllText(EditorControl e) =>
        e.Document!.Table.GetText(0, (int)e.Document.Table.Length);

    [AvaloniaFact]
    public void ReplaceCurrent_PreserveCase_UpperMatch() {
        var e = CreateEditor("HELLO world");
        e.LastSearchTerm = "hello";
        e.FindNext();
        Assert.True(e.ReplaceCurrent("goodbye", preserveCase: true));
        Assert.StartsWith("GOODBYE", AllText(e));
    }

    [AvaloniaFact]
    public void ReplaceCurrent_PreserveCase_LowerMatch() {
        var e = CreateEditor("hello WORLD");
        e.LastSearchTerm = "hello";
        e.FindNext();
        Assert.True(e.ReplaceCurrent("goodbye", preserveCase: true));
        Assert.StartsWith("goodbye", AllText(e));
    }

    [AvaloniaFact]
    public void ReplaceCurrent_PreserveCase_TitleMatch() {
        var e = CreateEditor("Hello world");
        e.LastSearchTerm = "hello";
        e.FindNext();
        Assert.True(e.ReplaceCurrent("goodbye", preserveCase: true));
        Assert.StartsWith("Goodbye", AllText(e));
    }

    [AvaloniaFact]
    public void ReplaceCurrent_PreserveCase_Disabled_UsesLiteralReplacement() {
        var e = CreateEditor("HELLO world");
        e.LastSearchTerm = "hello";
        e.FindNext();
        Assert.True(e.ReplaceCurrent("goodbye", preserveCase: false));
        Assert.StartsWith("goodbye", AllText(e));
    }

    [AvaloniaFact]
    public void ReplaceCurrent_PreserveCase_MixedCase_NoTransform() {
        var e = CreateEditor("hELLO world");
        e.LastSearchTerm = "hello";
        e.FindNext();
        Assert.True(e.ReplaceCurrent("goodbye", preserveCase: true));
        Assert.StartsWith("goodbye", AllText(e));
    }
}
