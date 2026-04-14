using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests that MatchWholeWord interacts correctly with Wildcard and
/// Regex search modes — matches must not span multiple words.
/// </summary>
public class WholeWordSearchTests {
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

    private static string SelectedText(EditorControl e) =>
        e.Document!.GetSelectedText() ?? "";

    // =================================================================
    //  Wildcard + WholeWord
    // =================================================================

    [AvaloniaFact]
    public void Wildcard_WholeWord_StarMatchesWithinWord() {
        // "hello*" with WholeWord should match "helloWorld" (single word)
        var e = CreateEditor("prefix helloWorld suffix");
        e.LastSearchTerm = "hello*";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
        Assert.Equal("helloWorld", SelectedText(e));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_StarDoesNotSpanWords() {
        // "hello*world" with WholeWord should NOT match "hello beautiful world"
        // because * should only match word chars, not spaces.
        var e = CreateEditor("hello beautiful world");
        e.LastSearchTerm = "hello*world";
        Assert.False(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_StarMatchesSingleWord() {
        // "hello*world" with WholeWord SHOULD match "helloXYZworld"
        var e = CreateEditor("prefix helloXYZworld suffix");
        e.LastSearchTerm = "hello*world";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
        Assert.Equal("helloXYZworld", SelectedText(e));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_QuestionMarkMatchesWordChar() {
        // "h?llo" with WholeWord should match "hello"
        var e = CreateEditor("prefix hello suffix");
        e.LastSearchTerm = "h?llo";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
        Assert.Equal("hello", SelectedText(e));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_QuestionMarkDoesNotMatchSpace() {
        // "h?llo" with WholeWord should NOT match "h llo" (space is not a word char)
        var e = CreateEditor("h llo");
        e.LastSearchTerm = "h?llo";
        Assert.False(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
    }

    [AvaloniaFact]
    public void Wildcard_NoWholeWord_StarSpansWords() {
        // Without WholeWord, "hello*world" should match across spaces
        var e = CreateEditor("hello beautiful world");
        e.LastSearchTerm = "hello*world";
        Assert.True(e.FindNext(wholeWord: false, mode: SearchMode.Wildcard));
        Assert.Equal("hello beautiful world", SelectedText(e));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_DoesNotMatchPartialWord() {
        // "test*" with WholeWord should not match "testing" inside "atesting"
        // because \b requires a word boundary at the start
        var e = CreateEditor("atesting");
        e.LastSearchTerm = "test*";
        Assert.False(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
    }

    [AvaloniaFact]
    public void Wildcard_WholeWord_MatchesStandaloneWord() {
        // "test*" with WholeWord should match "testing" as a standalone word
        var e = CreateEditor("prefix testing suffix");
        e.LastSearchTerm = "test*";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Wildcard));
        Assert.Equal("testing", SelectedText(e));
    }

    // =================================================================
    //  Regex + WholeWord
    // =================================================================

    [AvaloniaFact]
    public void Regex_WholeWord_DotStarDoesNotSpanWords() {
        // "foo.*bar" with WholeWord should not match "foo something bar"
        var e = CreateEditor("foo something bar");
        e.LastSearchTerm = "foo.*bar";
        Assert.False(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_DotStarMatchesWithinWord() {
        // "foo.*bar" with WholeWord should match "fooXYZbar" (no whitespace)
        var e = CreateEditor("prefix fooXYZbar suffix");
        e.LastSearchTerm = "foo.*bar";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("fooXYZbar", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_SkipsMultiWordMatchFindsNextSingleWord() {
        // First potential match "foo bar" spans words — skip it.
        // Second match "foobar" is a single word — should be found.
        var e = CreateEditor("foo bar then foobar");
        e.LastSearchTerm = "foo.*bar";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("foobar", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_AllowsHyphenAndPunctuation() {
        // "foo-bar" has no whitespace, so it should match with WholeWord
        var e = CreateEditor("prefix foo-bar suffix");
        e.LastSearchTerm = "foo.bar";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("foo-bar", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_AllowsDigitsWithDot() {
        // "3.14" should match — no whitespace inside
        var e = CreateEditor("value is 3.14 here");
        e.LastSearchTerm = @"\d+\.\d+";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("3.14", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_NoWholeWord_DotStarSpansWords() {
        // Without WholeWord, "foo.*bar" should match across spaces
        var e = CreateEditor("foo something bar");
        e.LastSearchTerm = "foo.*bar";
        Assert.True(e.FindNext(wholeWord: false, mode: SearchMode.Regex));
        Assert.Equal("foo something bar", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_SimpleWordBoundary() {
        // Basic whole-word regex: "test" should not match inside "testing"
        var e = CreateEditor("testing test tested");
        e.LastSearchTerm = "test";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("test", SelectedText(e));
        // The match should be at position 8 ("test" between spaces)
        Assert.Equal(8, e.Document!.Selection.Start);
    }

    // =================================================================
    //  FindPrevious with WholeWord
    // =================================================================

    [AvaloniaFact]
    public void Wildcard_WholeWord_FindPrevDoesNotSpanWords() {
        var e = CreateEditor("hello world helloWorld");
        e.Document!.Selection = Selection.Collapsed(e.Document.Table.Length);
        e.LastSearchTerm = "hello*";
        Assert.True(e.FindPrevious(wholeWord: true, mode: SearchMode.Wildcard));
        Assert.Equal("helloWorld", SelectedText(e));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_FindPrevSkipsMultiWordMatch() {
        var e = CreateEditor("foobar then foo bar");
        e.Document!.Selection = Selection.Collapsed(e.Document.Table.Length);
        e.LastSearchTerm = "foo.*bar";
        Assert.True(e.FindPrevious(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("foobar", SelectedText(e));
    }

    // =================================================================
    //  Normal mode + WholeWord (should be unaffected by changes)
    // =================================================================

    [AvaloniaFact]
    public void Normal_WholeWord_StillWorks() {
        var e = CreateEditor("testing test tested");
        e.LastSearchTerm = "test";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Normal));
        Assert.Equal("test", SelectedText(e));
        Assert.Equal(8, e.Document!.Selection.Start);
    }

    [AvaloniaFact]
    public void Regex_WholeWord_TheStarDoesNotMatchOther() {
        // "the.*" with WholeWord should not match "other" — there's no
        // word boundary before 't' in "other".
        var e = CreateEditor("other");
        e.LastSearchTerm = "the.*";
        Assert.False(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
    }

    [AvaloniaFact]
    public void Regex_WholeWord_TheStarMatchesTheNotOther() {
        // In "the other", "the.*" with WholeWord should match "the" only,
        // not "other" and not the entire "the other".
        var e = CreateEditor("the other");
        e.LastSearchTerm = "the.*";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("the", SelectedText(e));
        // FindNext again should wrap and find "the" again, never "other"
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Regex));
        Assert.Equal("the", SelectedText(e));
    }

    [AvaloniaFact]
    public void Normal_WholeWord_MultiWordPhrase() {
        // Searching for "hello world" with WholeWord in Normal mode
        // should still match the exact phrase (the user typed it)
        var e = CreateEditor("say hello world today");
        e.LastSearchTerm = "hello world";
        Assert.True(e.FindNext(wholeWord: true, mode: SearchMode.Normal));
        Assert.Equal("hello world", SelectedText(e));
    }
}
