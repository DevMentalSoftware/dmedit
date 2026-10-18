using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DMEdit.App.Services;

namespace DMEdit.App.Tests;

/// <summary>
/// Escape and Ctrl+F4 close dialogs as a cancel (GH #17).
/// </summary>
public class DialogCloseKeysTests {
    [Theory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.F4, KeyModifiers.Control, true)]
    [InlineData(Key.F4, KeyModifiers.None, false)]
    [InlineData(Key.F4, KeyModifiers.Alt, false)]
    [InlineData(Key.Escape, KeyModifiers.Shift, false)]
    [InlineData(Key.Enter, KeyModifiers.None, false)]
    public void IsCloseGesture(Key key, KeyModifiers mods, bool expected) {
        Assert.Equal(expected, DialogCloseKeys.IsCloseGesture(key, mods));
    }

    private static bool PressAndCheckClosed(Window window, Key key, RawInputModifiers mods) {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.KeyPress(key, mods, PhysicalKey.None, null);
        return closed;
    }

    [AvaloniaFact]
    public void Escape_ClosesAboutDialog() {
        var dlg = new AboutDialog(EditorTheme.Light);
        Assert.True(PressAndCheckClosed(dlg, Key.Escape, RawInputModifiers.None));
    }

    [AvaloniaFact]
    public void CtrlF4_ClosesAboutDialog() {
        var dlg = new AboutDialog(EditorTheme.Light);
        Assert.True(PressAndCheckClosed(dlg, Key.F4, RawInputModifiers.Control));
    }

    [AvaloniaFact]
    public void Escape_ClosesErrorDialog_KeepsDefaultResult() {
        var dlg = new ErrorDialog("t", "d", [ErrorDialogButton.SaveAs, ErrorDialogButton.OK]);
        Assert.True(PressAndCheckClosed(dlg, Key.Escape, RawInputModifiers.None));
        Assert.Equal(ErrorDialogButton.OK, dlg.Result);
    }

    [AvaloniaFact]
    public void Escape_ClosesSaveChangesDialog_AsCancel() {
        var dlg = new SaveChangesDialog("file.txt");
        Assert.True(PressAndCheckClosed(dlg, Key.Escape, RawInputModifiers.None));
        Assert.Equal(SaveChoice.Cancel, dlg.Result.Choice);
    }

    [AvaloniaFact]
    public void OtherKey_DoesNotClose() {
        var dlg = new AboutDialog(EditorTheme.Light);
        Assert.False(PressAndCheckClosed(dlg, Key.F4, RawInputModifiers.None));
        dlg.Close();
    }

    [AvaloniaFact]
    public void Escape_CancelsProgressDialog_WithoutClosing() {
        var dlg = new ProgressDialog("t", "working");
        var closed = PressAndCheckClosed(dlg, Key.Escape, RawInputModifiers.None);
        Assert.False(closed);
        Assert.True(dlg.WasCancelled);
        Assert.True(dlg.CancellationToken.IsCancellationRequested);
        dlg.Close();
    }
}
