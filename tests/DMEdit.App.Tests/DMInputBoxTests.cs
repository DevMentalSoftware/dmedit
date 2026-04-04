using Avalonia.Headless.XUnit;
using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

public class DMInputBoxTests {
    private static DMInputBox Create(string text = "") {
        var box = new DMInputBox { Text = text };
        return box;
    }

    // ---------------------------------------------------------------
    // Text property basics
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void Text_DefaultsToEmptyString() {
        var box = Create();
        Assert.Equal("", box.Text);
    }

    [AvaloniaFact]
    public void Text_SetNull_CoercesToEmpty() {
        var box = Create("hello");
        box.Text = null;
        Assert.Equal("", box.Text);
    }

    [AvaloniaFact]
    public void Text_SetValue_RoundTrips() {
        var box = Create();
        box.Text = "abc";
        Assert.Equal("abc", box.Text);
    }

    // ---------------------------------------------------------------
    // Caret clamping
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void CaretIndex_ClampedWhenTextShrinks() {
        var box = Create("hello");
        box.CaretIndex = 5;
        box.Text = "hi";
        Assert.True(box.CaretIndex <= 2);
    }

    [AvaloniaFact]
    public void SelectionStart_ClampedWhenTextShrinks() {
        var box = Create("hello");
        box.SelectionStart = 4;
        box.Text = "ab";
        Assert.True(box.SelectionStart <= 2);
    }

    [AvaloniaFact]
    public void SelectionEnd_ClampedWhenTextShrinks() {
        var box = Create("hello");
        box.SelectionEnd = 5;
        box.Text = "x";
        Assert.True(box.SelectionEnd <= 1);
    }

    // ---------------------------------------------------------------
    // SelectAll
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void SelectAll_SetsSelectionToFullText() {
        var box = Create("hello");
        box.SelectAll();
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(5, box.SelectionEnd);
        Assert.Equal(5, box.CaretIndex);
    }

    [AvaloniaFact]
    public void SelectAll_EmptyText_NoOp() {
        var box = Create("");
        box.SelectAll();
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(0, box.SelectionEnd);
    }

    // ---------------------------------------------------------------
    // SelectedText
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void SelectedText_ReturnsSubstring() {
        var box = Create("hello world");
        box.SelectionStart = 0;
        box.SelectionEnd = 5;
        Assert.Equal("hello", box.SelectedText);
    }

    [AvaloniaFact]
    public void SelectedText_ReversedSelection_StillWorks() {
        var box = Create("hello world");
        box.SelectionStart = 5;
        box.SelectionEnd = 0;
        Assert.Equal("hello", box.SelectedText);
    }

    [AvaloniaFact]
    public void SelectedText_NoSelection_ReturnsEmpty() {
        var box = Create("hello");
        box.SelectionStart = 3;
        box.SelectionEnd = 3;
        Assert.Equal("", box.SelectedText);
    }

    [AvaloniaFact]
    public void SelectedText_ClampedToTextLength() {
        var box = Create("hi");
        box.SelectionStart = 0;
        box.SelectionEnd = 100;
        Assert.Equal("hi", box.SelectedText);
    }

    // ---------------------------------------------------------------
    // IsReadOnly
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void IsReadOnly_DefaultsFalse() {
        var box = Create();
        Assert.False(box.IsReadOnly);
    }

    [AvaloniaFact]
    public void IsReadOnly_CanSet() {
        var box = Create();
        box.IsReadOnly = true;
        Assert.True(box.IsReadOnly);
    }

    // ---------------------------------------------------------------
    // Word boundary helpers (tested via SelectAll + caret movement
    // logic — exposed indirectly through the private methods,
    // but we can verify behavior through property manipulation)
    // ---------------------------------------------------------------

    // The word boundary helpers are private static, so we test them
    // indirectly through the observable behavior of the control.
    // PrevWordBoundary and NextWordBoundary are used by Ctrl+Left/Right,
    // Ctrl+Backspace, and Ctrl+Delete.  Since we can't send key events
    // easily in headless mode without a visual tree, we test the public
    // API surface that relies on them (SelectedText with various ranges).

    // ---------------------------------------------------------------
    // Property defaults
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void CaretWidth_DefaultsToOne() {
        var box = Create();
        Assert.Equal(1.0, box.CaretWidth);
    }

    [AvaloniaFact]
    public void Watermark_DefaultsToNull() {
        var box = Create();
        Assert.Null(box.Watermark);
    }

    [AvaloniaFact]
    public void Watermark_CanSet() {
        var box = Create();
        box.Watermark = "Type here...";
        Assert.Equal("Type here...", box.Watermark);
    }

    // ---------------------------------------------------------------
    // CaretIndex boundary behavior
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void CaretIndex_DefaultsToZero() {
        var box = Create("hello");
        Assert.Equal(0, box.CaretIndex);
    }

    [AvaloniaFact]
    public void CaretIndex_CanSetToEndOfText() {
        var box = Create("hello");
        box.CaretIndex = 5;
        Assert.Equal(5, box.CaretIndex);
    }

    [AvaloniaFact]
    public void CaretIndex_CanSetToMiddle() {
        var box = Create("hello");
        box.CaretIndex = 3;
        Assert.Equal(3, box.CaretIndex);
    }

    // ---------------------------------------------------------------
    // Selection range edge cases
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void Selection_FullText() {
        var box = Create("abcdef");
        box.SelectionStart = 0;
        box.SelectionEnd = 6;
        Assert.Equal("abcdef", box.SelectedText);
    }

    [AvaloniaFact]
    public void Selection_SingleChar() {
        var box = Create("abcdef");
        box.SelectionStart = 2;
        box.SelectionEnd = 3;
        Assert.Equal("c", box.SelectedText);
    }

    [AvaloniaFact]
    public void Selection_NegativeStart_ClampedToZero() {
        var box = Create("hello");
        box.SelectionStart = -5;
        box.SelectionEnd = 3;
        // SelectedText clamps internally
        Assert.Equal("hel", box.SelectedText);
    }

    // ---------------------------------------------------------------
    // Text property change clamps all indices
    // ---------------------------------------------------------------

    [AvaloniaFact]
    public void TextChange_ClampsAllIndices() {
        var box = Create("hello world");
        box.CaretIndex = 11;
        box.SelectionStart = 6;
        box.SelectionEnd = 11;
        box.Text = "hi";
        Assert.True(box.CaretIndex <= 2);
        Assert.True(box.SelectionStart <= 2);
        Assert.True(box.SelectionEnd <= 2);
    }

    [AvaloniaFact]
    public void TextChange_ToEmpty_ClampsToZero() {
        var box = Create("hello");
        box.CaretIndex = 3;
        box.SelectionStart = 1;
        box.SelectionEnd = 4;
        box.Text = "";
        Assert.Equal(0, box.CaretIndex);
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(0, box.SelectionEnd);
    }
}
