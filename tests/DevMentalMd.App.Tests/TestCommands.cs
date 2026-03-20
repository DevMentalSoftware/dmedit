using DevMentalMd.App.Commands;

namespace DevMentalMd.App.Tests;

/// <summary>
/// Registers all known command IDs with no-op actions for testing.
/// Mirrors the commands that the real app registers via MainWindow and EditorControl.
/// </summary>
static class TestCommands {
    public static CommandRegistry CreateRegistry() {
        var r = new CommandRegistry();
        // File
        r.Register("File.New", "New", Noop);
        r.Register("File.Open", "Open", Noop);
        r.Register("File.Save", "Save", Noop);
        r.Register("File.SaveAs", "Save As", Noop);
        r.Register("File.SaveAll", "Save All", Noop);
        r.Register("File.Close", "Close", Noop);
        r.Register("File.CloseAll", "Close All", Noop);
        r.Register("File.Print", "Print", Noop);
        r.Register("File.SaveAsPdf", "Save As PDF", Noop);
        r.Register("File.Exit", "Exit", Noop);
        r.Register("File.RevertFile", "Revert File", Noop);
        r.Register("File.ReloadFile", "Reload File", Noop);
        r.Register("File.ClearRecentFiles", "Clear Recent Files", Noop);
        // Edit
        r.Register("Edit.Undo", "Undo", Noop);
        r.Register("Edit.Redo", "Redo", Noop);
        r.Register("Edit.Cut", "Cut", Noop);
        r.Register("Edit.Copy", "Copy", Noop);
        r.Register("Edit.Paste", "Paste", Noop);
        r.Register("Edit.PasteMore", "Paste More", Noop);
        r.Register("Edit.ClipboardRing", "Clipboard Ring", Noop);
        r.Register("Edit.Delete", "Delete", Noop, showInPalette: false);
        r.Register("Edit.Backspace", "Backspace", Noop, showInPalette: false);
        r.Register("Edit.SelectAll", "Select All", Noop);
        r.Register("Edit.SelectWord", "Select Word", Noop);
        r.Register("Edit.DeleteLine", "Delete Line", Noop);
        r.Register("Edit.MoveLineUp", "Move Line Up", Noop);
        r.Register("Edit.MoveLineDown", "Move Line Down", Noop);
        r.Register("Edit.UpperCase", "Upper Case", Noop);
        r.Register("Edit.LowerCase", "Lower Case", Noop);
        r.Register("Edit.ProperCase", "Proper Case", Noop);
        r.Register("Edit.Newline", "Insert Newline", Noop, showInPalette: false);
        r.Register("Edit.Tab", "Insert Tab", Noop, showInPalette: false);
        r.Register("Edit.InsertLineBelow", "Insert Line Below", Noop);
        r.Register("Edit.InsertLineAbove", "Insert Line Above", Noop);
        r.Register("Edit.DeleteWordLeft", "Delete Word Left", Noop);
        r.Register("Edit.DeleteWordRight", "Delete Word Right", Noop);
        r.Register("Edit.DuplicateLine", "Duplicate Line", Noop);
        r.Register("Edit.SmartIndent", "Smart Indent", Noop);
        r.Register("Edit.Indent", "Indent", Noop);
        r.Register("Edit.Outdent", "Outdent", Noop);
        r.Register("Edit.ExpandSelection", "Expand Selection", Noop);
        r.Register("Edit.LineEndingLF", "Convert Line Endings to LF", Noop);
        r.Register("Edit.LineEndingCRLF", "Convert Line Endings to CRLF", Noop);
        r.Register("Edit.LineEndingCR", "Convert Line Endings to CR", Noop);
        r.Register("Edit.IndentToSpaces", "Convert Indentation to Spaces", Noop);
        r.Register("Edit.IndentToTabs", "Convert Indentation to Tabs", Noop);
        r.Register("Edit.EncodingUtf8", "Set Encoding to UTF-8", Noop);
        r.Register("Edit.EncodingUtf8Bom", "Set Encoding to UTF-8 with BOM", Noop);
        r.Register("Edit.EncodingUtf16Le", "Set Encoding to UTF-16 LE", Noop);
        r.Register("Edit.EncodingUtf16Be", "Set Encoding to UTF-16 BE", Noop);
        r.Register("Edit.EncodingWin1252", "Set Encoding to Windows-1252", Noop);
        r.Register("Edit.EncodingAscii", "Set Encoding to ASCII", Noop);
        // Find
        r.Register("Find.Find", "Find", Noop);
        r.Register("Find.Replace", "Replace", Noop);
        r.Register("Find.FindNext", "Find Next", Noop);
        r.Register("Find.FindPrevious", "Find Previous", Noop);
        r.Register("Find.FindNextSelection", "Find Next Selection", Noop);
        r.Register("Find.FindPreviousSelection", "Find Previous Selection", Noop);
        r.Register("Find.IncrementalSearch", "Incremental Search", Noop);
        // View
        r.Register("View.LineNumbers", "Line Numbers", Noop);
        r.Register("View.StatusBar", "Status Bar", Noop);
        r.Register("View.WrapLines", "Wrap Lines", Noop);
        r.Register("View.ZoomIn", "Zoom In", Noop);
        r.Register("View.ZoomOut", "Zoom Out", Noop);
        r.Register("View.Whitespace", "Show Whitespace", Noop);
        r.Register("View.ZoomReset", "Zoom Reset", Noop);
        // Window
        r.Register("Window.NextTab", "Next Tab", Noop);
        r.Register("Window.PrevTab", "Previous Tab", Noop);
        r.Register("Window.Settings", "Settings", Noop);
        r.Register("Window.CommandPalette", "Command Palette", Noop);
        // Nav: movement
        r.Register("Nav.MoveLeft", "Move Left", Noop);
        r.Register("Nav.MoveRight", "Move Right", Noop);
        r.Register("Nav.MoveUp", "Move Up", Noop);
        r.Register("Nav.MoveDown", "Move Down", Noop);
        r.Register("Nav.MoveWordLeft", "Move Word Left", Noop);
        r.Register("Nav.MoveWordRight", "Move Word Right", Noop);
        r.Register("Nav.MoveHome", "Move to Line Start", Noop);
        r.Register("Nav.MoveEnd", "Move to Line End", Noop);
        r.Register("Nav.MoveDocStart", "Move to Document Start", Noop);
        r.Register("Nav.MoveDocEnd", "Move to Document End", Noop);
        r.Register("Nav.PageUp", "Page Up", Noop);
        r.Register("Nav.PageDown", "Page Down", Noop);
        r.Register("Nav.GoToLine", "Go to Line", Noop);
        r.Register("Nav.ScrollLineUp", "Scroll Line Up", Noop);
        r.Register("Nav.ScrollLineDown", "Scroll Line Down", Noop);
        r.Register("Nav.FocusEditor", "Focus Editor", Noop);
        // Nav: selection
        r.Register("Nav.SelectLeft", "Select Left", Noop);
        r.Register("Nav.SelectRight", "Select Right", Noop);
        r.Register("Nav.SelectUp", "Select Up", Noop);
        r.Register("Nav.SelectDown", "Select Down", Noop);
        r.Register("Nav.SelectWordLeft", "Select Word Left", Noop);
        r.Register("Nav.SelectWordRight", "Select Word Right", Noop);
        r.Register("Nav.SelectHome", "Select to Line Start", Noop);
        r.Register("Nav.SelectEnd", "Select to Line End", Noop);
        r.Register("Nav.SelectDocStart", "Select to Document Start", Noop);
        r.Register("Nav.SelectDocEnd", "Select to Document End", Noop);
        r.Register("Nav.SelectPageUp", "Select Page Up", Noop);
        r.Register("Nav.SelectPageDown", "Select Page Down", Noop);
        // Nav: column selection
        r.Register("Nav.ColumnSelectUp", "Column Select Up", Noop);
        r.Register("Nav.ColumnSelectDown", "Column Select Down", Noop);
        r.Register("Nav.ColumnSelectLeft", "Column Select Left", Noop);
        r.Register("Nav.ColumnSelectRight", "Column Select Right", Noop);
        return r;
    }

    private static void Noop() { }
}
