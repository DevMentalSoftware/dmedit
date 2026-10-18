using Avalonia;
using Avalonia.Headless.XUnit;
using DMEdit.App.Controls;

namespace DMEdit.App.Tests;

/// <summary>
/// The toolbar is anchored to its left edge (beside the menu bar) rather
/// than centred, so resizing the window does not move it (GH #20).
/// </summary>
public class ToolbarAnchorTests {
    private static ToolbarControl CreateToolbar(double width) {
        var items = Enumerable.Range(0, 5)
            .Select(i => new ToolbarItem {
                CommandId = $"Test.Item{i}",
                Glyph = "x",
                Tooltip = $"Item {i}",
            })
            .ToList();
        var toolbar = new ToolbarControl();
        toolbar.SetItems(items);
        toolbar.Measure(new Size(width, 32));
        toolbar.Arrange(new Rect(0, 0, width, 32));
        return toolbar;
    }

    [AvaloniaFact]
    public void FirstButton_DoesNotMoveWhenWidthChanges() {
        var narrow = CreateToolbar(400);
        var wide = CreateToolbar(1600);
        Assert.Equal(narrow.ContentOffsetXForTest, wide.ContentOffsetXForTest);
    }

    [AvaloniaFact]
    public void FirstButton_DoesNotMoveWhenOverflowing() {
        var roomy = CreateToolbar(800);
        var cramped = CreateToolbar(90);
        Assert.Equal(roomy.ContentOffsetXForTest, cramped.ContentOffsetXForTest);
        Assert.NotEmpty(cramped.GetOverflowItems());
    }
}
