using System.Collections.Generic;
using System.Linq;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Single source of truth for all application commands. Each entry defines
/// a command's identity and display name. Keyboard bindings are loaded from
/// profile JSON resources by <see cref="KeyBindingService"/>.
/// The category is derived from the command ID prefix (e.g. "Edit.Undo" →
/// "Edit"). Parallels <see cref="Settings.SettingsRegistry"/> for settings.
/// </summary>
public static class CommandRegistry {
    public static readonly IReadOnlyList<CommandDescriptor> All = [
        // -- File --
        new(CommandIds.FileNew, "New"),
        new(CommandIds.FileOpen, "Open"),
        new(CommandIds.FileSave, "Save"),
        new(CommandIds.FileSaveAs, "Save As"),
        new(CommandIds.FileSaveAll, "Save All"),
        new(CommandIds.FileClose, "Close"),
        new(CommandIds.FileCloseAll, "Close All"),
        new(CommandIds.FilePrint, "Print"),
        new(CommandIds.FileSaveAsPdf, "Save As PDF"),
        new(CommandIds.FileExit, "Exit"),
        new(CommandIds.FileRevertFile, "Revert File"),
        new(CommandIds.FileReloadFile, "Reload File"),
        new(CommandIds.FileClearRecentFiles, "Clear Recent Files"),

        // -- Edit --
        new(CommandIds.EditUndo, "Undo"),
        new(CommandIds.EditRedo, "Redo"),
        new(CommandIds.EditCut, "Cut"),
        new(CommandIds.EditCopy, "Copy"),
        new(CommandIds.EditPaste, "Paste"),
        new(CommandIds.EditPasteMore, "Paste More"),
        new(CommandIds.EditClipboardRing, "Clipboard Ring"),
        new(CommandIds.EditDelete, "Delete"),
        new(CommandIds.EditBackspace, "Backspace"),
        new(CommandIds.EditSelectAll, "Select All"),
        new(CommandIds.EditSelectWord, "Select Word"),
        new(CommandIds.EditDeleteLine, "Delete Line"),
        new(CommandIds.EditMoveLineUp, "Move Line Up"),
        new(CommandIds.EditMoveLineDown, "Move Line Down"),
        new(CommandIds.EditUpperCase, "Upper Case"),
        new(CommandIds.EditLowerCase, "Lower Case"),
        new(CommandIds.EditProperCase, "Proper Case"),
        new(CommandIds.EditNewline, "Insert Newline"),
        new(CommandIds.EditTab, "Insert Tab"),
        new(CommandIds.EditInsertLineBelow, "Insert Line Below"),
        new(CommandIds.EditInsertLineAbove, "Insert Line Above"),
        new(CommandIds.EditDeleteWordLeft, "Delete Word Left"),
        new(CommandIds.EditDeleteWordRight, "Delete Word Right"),
        new(CommandIds.EditDuplicateLine, "Duplicate Line"),
        new(CommandIds.EditSmartIndent, "Smart Indent"),
        new(CommandIds.EditIndent, "Indent"),
        new(CommandIds.EditOutdent, "Outdent"),
        new(CommandIds.EditExpandSelection, "Expand Selection"),
        new(CommandIds.EditLineEndingLF, "Convert Line Endings to LF"),
        new(CommandIds.EditLineEndingCRLF, "Convert Line Endings to CRLF"),
        new(CommandIds.EditLineEndingCR, "Convert Line Endings to CR"),
        new(CommandIds.EditIndentToSpaces, "Convert Indentation to Spaces"),
        new(CommandIds.EditIndentToTabs, "Convert Indentation to Tabs"),
        new(CommandIds.EditEncodingUtf8, "Set Encoding to UTF-8"),
        new(CommandIds.EditEncodingUtf8Bom, "Set Encoding to UTF-8 with BOM"),
        new(CommandIds.EditEncodingUtf16Le, "Set Encoding to UTF-16 LE"),
        new(CommandIds.EditEncodingUtf16Be, "Set Encoding to UTF-16 BE"),
        new(CommandIds.EditEncodingWin1252, "Set Encoding to Windows-1252"),
        new(CommandIds.EditEncodingAscii, "Set Encoding to ASCII"),

        // -- Find --
        new(CommandIds.FindFind, "Find"),
        new(CommandIds.FindReplace, "Replace"),
        new(CommandIds.FindFindNext, "Find Next"),
        new(CommandIds.FindFindPrevious, "Find Previous"),
        new(CommandIds.FindNextSelection, "Find Next Selection"),
        new(CommandIds.FindPreviousSelection, "Find Previous Selection"),
        new(CommandIds.FindIncrementalSearch, "Incremental Search"),

        // -- View --
        new(CommandIds.ViewLineNumbers, "Line Numbers"),
        new(CommandIds.ViewStatusBar, "Status Bar"),
        new(CommandIds.ViewWrapLines, "Wrap Lines"),
        new(CommandIds.ViewZoomIn, "Zoom In"),
        new(CommandIds.ViewZoomOut, "Zoom Out"),
        new(CommandIds.ViewWhitespace, "Show Whitespace"),
        new(CommandIds.ViewZoomReset, "Zoom Reset"),

        // -- Window --
        new(CommandIds.WindowNextTab, "Next Tab"),
        new(CommandIds.WindowPrevTab, "Previous Tab"),
        new(CommandIds.WindowSettings, "Settings"),
        new(CommandIds.WindowCommandPalette, "Command Palette"),

        // -- Nav: movement --
        new(CommandIds.NavMoveLeft, "Move Left"),
        new(CommandIds.NavMoveRight, "Move Right"),
        new(CommandIds.NavMoveUp, "Move Up"),
        new(CommandIds.NavMoveDown, "Move Down"),
        new(CommandIds.NavMoveWordLeft, "Move Word Left"),
        new(CommandIds.NavMoveWordRight, "Move Word Right"),
        new(CommandIds.NavMoveHome, "Move to Line Start"),
        new(CommandIds.NavMoveEnd, "Move to Line End"),
        new(CommandIds.NavMoveDocStart, "Move to Document Start"),
        new(CommandIds.NavMoveDocEnd, "Move to Document End"),
        new(CommandIds.NavPageUp, "Page Up"),
        new(CommandIds.NavPageDown, "Page Down"),
        new(CommandIds.NavGoToLine, "Go to Line"),
        new(CommandIds.NavScrollLineUp, "Scroll Line Up"),
        new(CommandIds.NavScrollLineDown, "Scroll Line Down"),
        new(CommandIds.NavFocusEditor, "Focus Editor"),

        // -- Nav: selection extension --
        new(CommandIds.NavSelectLeft, "Select Left"),
        new(CommandIds.NavSelectRight, "Select Right"),
        new(CommandIds.NavSelectUp, "Select Up"),
        new(CommandIds.NavSelectDown, "Select Down"),
        new(CommandIds.NavSelectWordLeft, "Select Word Left"),
        new(CommandIds.NavSelectWordRight, "Select Word Right"),
        new(CommandIds.NavSelectHome, "Select to Line Start"),
        new(CommandIds.NavSelectEnd, "Select to Line End"),
        new(CommandIds.NavSelectDocStart, "Select to Document Start"),
        new(CommandIds.NavSelectDocEnd, "Select to Document End"),
        new(CommandIds.NavSelectPageUp, "Select Page Up"),
        new(CommandIds.NavSelectPageDown, "Select Page Down"),

        // -- Nav: column (block) selection --
        new(CommandIds.NavColumnSelectUp, "Column Select Up"),
        new(CommandIds.NavColumnSelectDown, "Column Select Down"),
        new(CommandIds.NavColumnSelectLeft, "Column Select Left"),
        new(CommandIds.NavColumnSelectRight, "Column Select Right"),
    ];

    /// <summary>
    /// Ordered list of categories, derived once from the registered commands.
    /// </summary>
    public static readonly IReadOnlyList<string> Categories =
        All.Select(c => c.Category).Distinct().ToList();
}
