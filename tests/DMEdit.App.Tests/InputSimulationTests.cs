using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests that exercise Avalonia's real layout/arrange cycle through the
/// headless window, catching state/timing issues that direct method
/// calls miss (e.g., _layout being null between dispose and rebuild).
///
/// <para><b>Limitation:</b> EditorControl's key dispatch goes through
/// MainWindow.OnKeyDown, so keyboard commands (arrow keys, Ctrl+Home,
/// etc.) can't be tested via window.KeyPress without the full app
/// bootstrap.  These tests use the editor's public API + direct
/// ScrollValue writes to simulate scrolling, then verify the caret
/// layer survives the layout cycle.</para>
///
/// <para><b>Filtering:</b> use <c>--filter "Category!=InputSim"</c>
/// to exclude from fast test runs.</para>
/// </summary>
[Trait("Category", "InputSim")]
public class InputSimulationTests {
    private const double VpW = 600;
    private const double VpH = 400;

    private static (Window window, EditorControl editor) CreateWindow(
            Document doc, bool wrap = true) {
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            WrapLines = wrap,
        };
        var window = new Window {
            Width = VpW,
            Height = VpH,
            Content = editor,
        };
        window.Show();
        return (window, editor);
    }

    private static Document MakeDoc(int lineCount, int lineLen = 80) {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++) {
            var prefix = $"L{i:D4} ";
            sb.Append(prefix + new string('a', Math.Max(0, lineLen - prefix.Length)));
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        return doc;
    }

    private static long Caret(EditorControl e) => e.Document!.Selection.Caret;

    // ------------------------------------------------------------------
    //  Scroll via ScrollValue, then verify caret layer survives.
    //  This exercises the real Avalonia layout cycle (Measure→Arrange)
    //  that runs when the headless window processes pending layout.
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void ScrollPastCaret_ThenBack_CaretReappears() {
        var doc = MakeDoc(100);
        var (window, editor) = CreateWindow(doc, wrap: false);
        editor.Focus();

        editor.GoToPosition(doc.Table.LineStartOfs(10));

        // Scroll past the caret via direct ScrollValue writes
        // (same as Ctrl+Down commands).
        var rh = editor.RowHeightValue;
        for (var i = 0; i < 15; i++) {
            editor.ScrollValue += rh;
        }

        // Caret should be off-screen now.
        // Scroll back.
        for (var i = 0; i < 15; i++) {
            editor.ScrollValue -= rh;
        }

        // Force the full layout cycle.
        editor.ResetCaretBlink();

        var caretY = editor.GetCaretScreenYForTest();
        Assert.True(caretY.HasValue,
            "Caret not on screen after scrolling past and back");
        Assert.InRange(caretY!.Value, -1, VpH + 1);
        Assert.Equal(doc.Table.LineStartOfs(10), Caret(editor));

        window.Close();
    }

    // ------------------------------------------------------------------
    //  Mouse click positions caret (exercises pointer input pipeline)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void MouseClick_PositionsCaret() {
        var doc = MakeDoc(50);
        var (window, editor) = CreateWindow(doc, wrap: false);
        editor.Focus();

        // Click in the middle of the editor area.
        var clickPoint = new Point(VpW / 2, VpH / 2);
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);

        Assert.True(Caret(editor) > 0,
            "Mouse click didn't move caret from position 0");

        window.Close();
    }

    // ------------------------------------------------------------------
    //  Mouse wheel scrolls and caret stays correct
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void MouseWheel_ScrollsViewport() {
        var doc = MakeDoc(100);
        var (window, editor) = CreateWindow(doc, wrap: false);
        editor.Focus();

        var scrollBefore = editor.ScrollValue;

        // Scroll down via mouse wheel.
        window.MouseWheel(new Point(VpW / 2, VpH / 2),
            new Vector(0, -3), RawInputModifiers.None);

        Assert.True(editor.ScrollValue > scrollBefore,
            "Mouse wheel didn't scroll the viewport");

        window.Close();
    }

    // ------------------------------------------------------------------
    //  Edge-scroll via MoveCaretVertical then verify the layout cycle
    //  doesn't leave the caret hidden (the _layout=null bug).
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void EdgeScroll_MoveDown_CaretSurvivesLayoutCycle() {
        var doc = MakeDoc(100);
        var (window, editor) = CreateWindow(doc, wrap: false);
        editor.Focus();

        // Walk caret to near the bottom of the viewport.
        editor.GoToPosition(doc.Table.LineStartOfs(30));

        // Use the internal method which triggers the edge-scroll
        // (ScrollValue setter → _layout=null → ArrangeOverride).
        for (var step = 0; step < 40; step++) {
            var before = Caret(editor);
            editor.MoveCaretVerticalForTest(+1, false);
            if (Caret(editor) == before) break;
        }

        var caretY = editor.GetCaretScreenYForTest();
        Assert.True(caretY.HasValue,
            "Caret not on screen after walking down 40 lines");

        window.Close();
    }
}
