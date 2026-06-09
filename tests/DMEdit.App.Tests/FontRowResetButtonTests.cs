using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.App.Settings;
using Xunit;

namespace DMEdit.App.Tests;

/// <summary>
/// Regression test for the Editor Font "reset to default" button.  Its
/// visibility was hard-set once at row-creation time via <c>IsVisible</c>,
/// while every later change only toggled <c>Opacity</c>/<c>IsHitTestVisible</c>
/// through <c>UpdateFontModified</c>.  Starting from the default font (button
/// created collapsed) and then changing the font left the button stuck
/// invisible until the new font was saved and the row rebuilt — exactly the
/// "switched to Georgia, reset button never appeared" report.  This locks in
/// the fix that lets <c>UpdateFontModified</c> fully drive the button.
/// </summary>
public class FontRowResetButtonTests {
    private static Button FindResetButton(Border row) {
        return row.GetLogicalDescendants()
            .OfType<Button>()
            .First(b => (b.Tag as string) == "reset");
    }

    private static DMEditableCombo FindFontBox(Border row) {
        return row.GetLogicalDescendants()
            .OfType<DMEditableCombo>()
            .First();
    }

    [AvaloniaFact]
    public void FontChange_FromDefault_RevealsResetButton() {
        // Non-persistent instance: Save/ScheduleSave are no-ops, so this never
        // touches the user's settings.json.
        var settings = new AppSettings();
        var row = SettingRowFactory.CreateFontRow(settings, _ => { });

        var resetBtn = FindResetButton(row);

        // Default font: the button occupies its slot but is faded out and inert.
        Assert.True(resetBtn.IsVisible);
        Assert.Equal(0d, resetBtn.Opacity);
        Assert.False(resetBtn.IsHitTestVisible);

        // Switch to a non-default font, mirroring the user's Georgia repro.
        FindFontBox(row).Text = "Georgia";
        Assert.Equal("Georgia", settings.EditorFontFamily);

        // The button must now be fully shown and interactive. Before the fix,
        // IsVisible was stuck false here even though Opacity went to 1.
        Assert.True(resetBtn.IsVisible);
        Assert.Equal(1d, resetBtn.Opacity);
        Assert.True(resetBtn.IsHitTestVisible);
    }
}
