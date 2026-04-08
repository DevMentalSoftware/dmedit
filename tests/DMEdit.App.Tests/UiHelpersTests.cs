using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

/// <summary>
/// Pure-text tests for <see cref="UiHelpers.FormatPathTooltipText"/>.
/// The tooltip-rendering side of the helper (which constructs an Avalonia
/// <c>TextBlock</c>) is exercised by the App at runtime; this test focuses
/// on the inner-entry-name annotation contract that we just added so the
/// tab tooltip can show "outer.zip → inner.txt" without widening the tab.
/// </summary>
public class UiHelpersTests {
    [Fact]
    public void FormatPathTooltipText_NullPath_ReturnsNull() {
        Assert.Null(UiHelpers.FormatPathTooltipText(null, null));
        Assert.Null(UiHelpers.FormatPathTooltipText("", null));
    }

    [Fact]
    public void FormatPathTooltipText_PlainPath_NoArrowSuffix() {
        var text = UiHelpers.FormatPathTooltipText(@"C:\dir\file.txt", innerEntryName: null);
        Assert.NotNull(text);
        Assert.DoesNotContain("\u2192", text!);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void FormatPathTooltipText_PlainPath_InsertsZeroWidthSpacesAfterSeparators() {
        // Zero-width spaces let the wrapping renderer break the path between
        // directories without inserting visible characters.
        var text = UiHelpers.FormatPathTooltipText(@"C:\dir\sub\file.txt", null);
        Assert.NotNull(text);
        Assert.Contains("\\\u200b", text!);
    }

    [Fact]
    public void FormatPathTooltipText_WithInnerEntryName_AppendsArrowLine() {
        var text = UiHelpers.FormatPathTooltipText(@"C:\archives\bundle.zip", "inner.txt");
        Assert.NotNull(text);
        // Path on first line, inner entry on second line prefixed with "→ ".
        Assert.Contains("bundle.zip", text!);
        Assert.Contains("\n\u2192 inner.txt", text);
    }

    [Fact]
    public void FormatPathTooltipText_EmptyInnerEntryName_NoArrowAppended() {
        var text = UiHelpers.FormatPathTooltipText(@"C:\file.txt", "");
        Assert.NotNull(text);
        Assert.DoesNotContain("\u2192", text!);
    }

    [Fact]
    public void FormatPathTooltipText_ForwardSlashPath_AlsoSplitsAtSeparators() {
        var text = UiHelpers.FormatPathTooltipText("/usr/local/bin/file", null);
        Assert.NotNull(text);
        Assert.Contains("/\u200b", text!);
    }
}
