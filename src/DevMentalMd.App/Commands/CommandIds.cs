namespace DevMentalMd.App.Commands;

/// <summary>
/// Compile-time constants for all command identifiers. Using const strings
/// instead of an enum allows future extensibility and string-based lookup
/// in settings persistence.
/// </summary>
public static class CommandIds {
    // -----------------------------------------------------------------
    // File
    // -----------------------------------------------------------------
    public const string FileNew      = "File.New";
    public const string FileOpen     = "File.Open";
    public const string FileSave     = "File.Save";
    public const string FileSaveAs   = "File.SaveAs";
    public const string FileSaveAll  = "File.SaveAll";
    public const string FileClose    = "File.Close";
    public const string FileCloseAll = "File.CloseAll";
    public const string FileExit       = "File.Exit";
    public const string FileRevertFile = "File.RevertFile";

    // -----------------------------------------------------------------
    // Edit
    // -----------------------------------------------------------------
    public const string EditUndo         = "Edit.Undo";
    public const string EditRedo         = "Edit.Redo";
    public const string EditCut          = "Edit.Cut";
    public const string EditCopy         = "Edit.Copy";
    public const string EditPaste        = "Edit.Paste";
    public const string EditDelete       = "Edit.Delete";
    public const string EditBackspace    = "Edit.Backspace";
    public const string EditSelectAll    = "Edit.SelectAll";
    public const string EditSelectWord   = "Edit.SelectWord";
    public const string EditDeleteLine   = "Edit.DeleteLine";
    public const string EditMoveLineUp   = "Edit.MoveLineUp";
    public const string EditMoveLineDown = "Edit.MoveLineDown";
    public const string EditUpperCase    = "Edit.UpperCase";
    public const string EditLowerCase    = "Edit.LowerCase";
    public const string EditProperCase   = "Edit.ProperCase";
    public const string EditNewline              = "Edit.Newline";
    public const string EditTab                  = "Edit.Tab";
    public const string EditInsertLineBelow      = "Edit.InsertLineBelow";
    public const string EditInsertLineAbove      = "Edit.InsertLineAbove";
    public const string EditDeleteWordLeft       = "Edit.DeleteWordLeft";
    public const string EditDeleteWordRight      = "Edit.DeleteWordRight";
    public const string EditDuplicateLine        = "Edit.DuplicateLine";
    public const string EditIndent               = "Edit.Indent";
    public const string EditSelectAllOccurrences = "Edit.SelectAllOccurrences";
    public const string EditColumnSelect         = "Edit.ColumnSelect";

    // -----------------------------------------------------------------
    // Find
    // -----------------------------------------------------------------
    public const string FindFind              = "Find.Find";
    public const string FindReplace           = "Find.Replace";
    public const string FindFindNext          = "Find.FindNext";
    public const string FindFindPrevious      = "Find.FindPrevious";
    public const string FindFindWordOrSel     = "Find.FindWordOrSelection";
    public const string FindIncrementalSearch = "Find.IncrementalSearch";

    // -----------------------------------------------------------------
    // View
    // -----------------------------------------------------------------
    public const string ViewLineNumbers = "View.LineNumbers";
    public const string ViewStatusBar   = "View.StatusBar";
    public const string ViewWrapLines   = "View.WrapLines";
    public const string ViewZoomIn      = "View.ZoomIn";
    public const string ViewZoomOut     = "View.ZoomOut";
    public const string ViewZoomReset   = "View.ZoomReset";

    // -----------------------------------------------------------------
    // Window
    // -----------------------------------------------------------------
    public const string WindowNextTab        = "Window.NextTab";
    public const string WindowPrevTab        = "Window.PrevTab";
    public const string WindowSettings       = "Window.Settings";
    public const string WindowCommandPalette = "Window.CommandPalette";

    // -----------------------------------------------------------------
    // Navigation — movement
    // -----------------------------------------------------------------
    public const string NavMoveLeft     = "Nav.MoveLeft";
    public const string NavMoveRight    = "Nav.MoveRight";
    public const string NavMoveUp       = "Nav.MoveUp";
    public const string NavMoveDown     = "Nav.MoveDown";
    public const string NavMoveWordLeft  = "Nav.MoveWordLeft";
    public const string NavMoveWordRight = "Nav.MoveWordRight";
    public const string NavMoveHome     = "Nav.MoveHome";
    public const string NavMoveEnd      = "Nav.MoveEnd";
    public const string NavMoveDocStart = "Nav.MoveDocStart";
    public const string NavMoveDocEnd   = "Nav.MoveDocEnd";
    public const string NavPageUp         = "Nav.PageUp";
    public const string NavPageDown      = "Nav.PageDown";
    public const string NavGoToLine      = "Nav.GoToLine";
    public const string NavScrollLineUp  = "Nav.ScrollLineUp";
    public const string NavScrollLineDown = "Nav.ScrollLineDown";

    // -----------------------------------------------------------------
    // Navigation — selection extension
    // -----------------------------------------------------------------
    public const string NavSelectLeft      = "Nav.SelectLeft";
    public const string NavSelectRight     = "Nav.SelectRight";
    public const string NavSelectUp        = "Nav.SelectUp";
    public const string NavSelectDown      = "Nav.SelectDown";
    public const string NavSelectWordLeft  = "Nav.SelectWordLeft";
    public const string NavSelectWordRight = "Nav.SelectWordRight";
    public const string NavSelectHome      = "Nav.SelectHome";
    public const string NavSelectEnd       = "Nav.SelectEnd";
    public const string NavSelectDocStart  = "Nav.SelectDocStart";
    public const string NavSelectDocEnd    = "Nav.SelectDocEnd";
    public const string NavSelectPageUp    = "Nav.SelectPageUp";
    public const string NavSelectPageDown  = "Nav.SelectPageDown";
}
